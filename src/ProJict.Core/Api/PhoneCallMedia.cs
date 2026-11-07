using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace ProJict.Core.Api;

/// <summary>What a phone call's media connection sends next: an audio frame for the phone to play, or a JSON event.</summary>
public readonly record struct PhoneCallOutput(byte[]? Audio, string? Text, long Generation)
{
    public bool IsAudio => Audio != null;
}

/// <summary>What happened to an audio frame from the phone.</summary>
public enum PhoneUplink { Accepted, Muted, Ended, Invalid, TooFast }

/// <summary>
/// One phone call's audio, both ways (phone calls API v1). The phone's microphone arrives as 16 kHz, 16-bit mono PCM
/// frames and goes to whoever listens (the call's recognizer), never while muted. What the agents say goes back as PCM
/// frames, paced at about real time so nothing piles up. Events (the call's state, captions) share the same queue, so
/// they arrive in order with the audio. When the phone can't keep up, the queue fills and the call ends rather than
/// buffering without bound. Thread-safe.
/// </summary>
public sealed class PhoneCallMedia
{
    public const int SampleRate = 16000;
    public const int BytesPerSecond = SampleRate * 2;
    public const int FrameMs = 100;
    public const int FrameBytes = BytesPerSecond * FrameMs / 1000;
    /// <summary>The largest frame the phone may send: 200 ms.</summary>
    public const int MaxUplinkFrameBytes = FrameBytes * 2;
    /// <summary>How many items may wait to be sent before the phone counts as gone.</summary>
    public const int QueueLimit = 256;
    /// <summary>How far ahead of real time the agent's audio is sent (the phone's playback buffer).</summary>
    public static readonly TimeSpan Lead = TimeSpan.FromMilliseconds(400);
    /// <summary>The phone may send audio this much faster than real time (bursts after a network stall), and no more.</summary>
    public const double UplinkAllowance = 1.5;
    /// <summary>A burst of up to this much audio is taken at once.</summary>
    public static readonly TimeSpan UplinkBurst = TimeSpan.FromSeconds(2);

    private readonly Channel<PhoneCallOutput> _out = Channel.CreateBounded<PhoneCallOutput>(new BoundedChannelOptions(QueueLimit)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.Wait,
    });
    private readonly Lock _lock = new();
    private readonly List<Action<byte[]>> _listeners = [];
    private readonly Func<TimeSpan> _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private long _generation;
    private double _budget = BytesPerSecond * UplinkBurst.TotalSeconds;
    private TimeSpan _budgetAt;
    private volatile bool _muted;
    private volatile bool _ended;

    public PhoneCallMedia(Guid callId = default, Func<TimeSpan>? clock = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        CallId = callId;
        var watch = Stopwatch.StartNew();
        _clock = clock ?? (() => watch.Elapsed);
        _delay = delay ?? ((wait, stop) => Task.Delay(wait, stop));
        _budgetAt = _clock();
    }

    /// <summary>The call it carries (set once the call has started).</summary>
    public Guid CallId { get; set; }
    public ChannelReader<PhoneCallOutput> Outgoing => _out.Reader;
    /// <summary>The audio now playing belongs to this generation; an audio stop starts the next, and older frames are dropped.</summary>
    public long Generation => Interlocked.Read(ref _generation);
    public bool Ended => _ended;
    /// <summary>Muted: the phone's audio goes nowhere (not to a recognizer, not to a speech provider).</summary>
    public bool Muted { get => _muted; set => _muted = value; }
    /// <summary>The phone was too slow to take what was sent, so the call can't go on.</summary>
    public bool Overflowed { get; private set; }
    public long UplinkFrames { get; private set; }
    public long RejectedFrames { get; private set; }

    /// <summary>Someone listens to the phone's microphone (any thread); disposing stops it.</summary>
    public IDisposable Listen(Action<byte[]> heard)
    {
        lock (_lock) _listeners.Add(heard);
        return new Subscription(() => { lock (_lock) _listeners.Remove(heard); });
    }

    /// <summary>A frame from the phone's microphone: passed to the listeners unless it's muted, over, malformed or too fast.</summary>
    public PhoneUplink Uplink(ReadOnlySpan<byte> frame)
    {
        Action<byte[]>[] listeners;
        byte[] pcm;
        lock (_lock)
        {
            if (_ended) return Reject(PhoneUplink.Ended);
            if (frame.Length == 0 || frame.Length % 2 != 0 || frame.Length > MaxUplinkFrameBytes) return Reject(PhoneUplink.Invalid);
            var now = _clock();
            var max = BytesPerSecond * UplinkBurst.TotalSeconds;
            _budget = Math.Min(max, _budget + (now - _budgetAt).TotalSeconds * BytesPerSecond * UplinkAllowance);
            _budgetAt = now;
            if (_budget < frame.Length) return Reject(PhoneUplink.TooFast);
            _budget -= frame.Length;
            if (_muted) return PhoneUplink.Muted;
            UplinkFrames++;
            listeners = [.. _listeners];
            pcm = frame.ToArray();
        }
        foreach (var listener in listeners) listener(pcm);
        return PhoneUplink.Accepted;
    }

    private PhoneUplink Reject(PhoneUplink why)
    {
        RejectedFrames++;
        return why;
    }

    /// <summary>An event for the phone (any thread); a full queue means the phone is gone, and the call is over for it.</summary>
    public bool Post(JsonObject message)
    {
        if (_ended) return false;
        if (_out.Writer.TryWrite(new PhoneCallOutput(null, message.ToJsonString(), Generation))) return true;
        Overflow();
        return false;
    }

    /// <summary>
    /// Sends speech to the phone (16 kHz, 16-bit mono PCM), about as fast as it plays, and is done once the phone has
    /// played it all; cut off (<paramref name="stop"/>), the phone is told to drop what it has buffered.
    /// </summary>
    public async Task PlayAsync(byte[] pcm, CancellationToken stop)
    {
        if (pcm.Length % 2 != 0) pcm = pcm[..^1];
        var generation = Generation;
        var started = _clock();
        var sent = TimeSpan.Zero;
        try
        {
            for (var offset = 0; offset < pcm.Length && !_ended; offset += FrameBytes)
            {
                var ahead = sent - (_clock() - started);
                if (ahead > Lead) await _delay(ahead - Lead, stop);
                stop.ThrowIfCancellationRequested();
                var length = Math.Min(FrameBytes, pcm.Length - offset);
                if (!_out.Writer.TryWrite(new PhoneCallOutput(pcm.AsSpan(offset, length).ToArray(), null, generation)))
                {
                    Overflow();
                    return;
                }
                sent += TimeSpan.FromSeconds(length / (double)BytesPerSecond);
            }
            var left = sent - (_clock() - started);
            if (left > TimeSpan.Zero && !_ended) await _delay(left, stop);
        }
        catch (OperationCanceledException)
        {
            StopAudio();
            throw;
        }
    }

    /// <summary>The phone drops the agent's audio it hasn't played (Stop, or speaking over it); frames already queued aren't sent.</summary>
    public void StopAudio()
    {
        Interlocked.Increment(ref _generation);
        Post(new JsonObject { ["type"] = "audio_stop" });
    }

    /// <summary>Whether a queued item is still to be sent: events always, audio only from the current generation.</summary>
    public bool IsCurrent(PhoneCallOutput output) => !output.IsAudio || output.Generation == Generation;

    /// <summary>The call is over for the phone: nothing more is heard, and the queue ends once its last event is sent.</summary>
    public void End(JsonObject? last = null)
    {
        if (last != null) Post(last);
        lock (_lock)
        {
            if (_ended) return;
            _ended = true;
            _listeners.Clear();
        }
        _out.Writer.TryComplete();
    }

    private void Overflow()
    {
        Overflowed = true;
        lock (_lock)
        {
            _ended = true;
            _listeners.Clear();
        }
        _out.Writer.TryComplete();
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;
        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}

using System.Net.WebSockets;
using System.Text;

namespace ProJict.Core.Api;

/// <summary>
/// A phone call's media WebSocket (phone calls API v1): the phone's microphone comes in as binary PCM frames, the
/// agent's voice goes out as binary PCM frames and the call's events as JSON text. When the phone closes it the call
/// ends; when the call ends it closes.
/// </summary>
public static class PhoneCallSocket
{
    /// <summary>Text from the phone isn't part of v1; up to this much is read and ignored.</summary>
    public const int MaxText = 4096;
    /// <summary>A phone that keeps sending audio that doesn't fit the format (or too fast) is cut off after this many frames.</summary>
    public const int BadFramesAllowed = 50;

    /// <summary>Runs the connection until either side ends it; why it ended ("media_closed", "ended", "bad_audio", "token_invalid", "phone_too_slow").</summary>
    public static async Task<string> RunAsync(WebSocket socket, PhoneCallMedia media, Func<bool> tokenValid, CancellationToken stop)
    {
        using var both = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var sending = SendAsync(socket, media, both.Token);
        var receiving = ReceiveAsync(socket, media, tokenValid, both.Token);
        var first = await Task.WhenAny(sending, receiving);
        string reason;
        if (first == receiving && receiving.IsCompletedSuccessfully) reason = receiving.Result;
        else if (media.Overflowed) reason = "phone_too_slow";
        else if (first == sending && sending.IsCompletedSuccessfully) reason = "ended";
        else reason = "media_lost";
        // The call is over with its last event sent: close properly and give the phone a moment to answer the close.
        // Otherwise the phone closed or misbehaved: stop sending first. (Cancelling a pending send or receive aborts the socket.)
        if (first != sending) both.Cancel();
        await Close(socket);
        if (first == sending) await Task.WhenAny(receiving, Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None));
        both.Cancel();
        await Task.WhenAll(sending.ContinueWith(_ => { }, TaskScheduler.Default), receiving.ContinueWith(_ => { }, TaskScheduler.Default));
        return reason;
    }

    private static async Task Close(WebSocket socket)
    {
        try
        {
            if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived)) return;
            using var bye = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "The call is over.", bye.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException or IOException) { }
    }

    private static async Task SendAsync(WebSocket socket, PhoneCallMedia media, CancellationToken stop)
    {
        await foreach (var output in media.Outgoing.ReadAllAsync(stop))
        {
            if (!media.IsCurrent(output)) continue;
            if (output.Audio is { } pcm) await socket.SendAsync(pcm, WebSocketMessageType.Binary, true, stop);
            else await socket.SendAsync(Encoding.UTF8.GetBytes(output.Text!), WebSocketMessageType.Text, true, stop);
        }
    }

    private static async Task<string> ReceiveAsync(WebSocket socket, PhoneCallMedia media, Func<bool> tokenValid, CancellationToken stop)
    {
        var buffer = new byte[PhoneCallMedia.MaxUplinkFrameBytes + MaxText];
        var scratch = new byte[MaxText];
        var bad = 0;
        while (true)
        {
            if (!tokenValid()) return "token_invalid";
            var length = 0;
            var tooBig = false;
            ValueWebSocketReceiveResult result;
            do
            {
                // Past the buffer, the rest of an oversized message is read and thrown away.
                Memory<byte> into = tooBig ? scratch : buffer.AsMemory(length);
                result = await socket.ReceiveAsync(into, stop);
                if (result.MessageType == WebSocketMessageType.Close) return "media_closed";
                if (tooBig) continue;
                length += result.Count;
                if (length == buffer.Length && !result.EndOfMessage) tooBig = true;
            } while (!result.EndOfMessage);
            if (result.MessageType != WebSocketMessageType.Binary) continue;
            var heard = tooBig ? PhoneUplink.Invalid : media.Uplink(buffer.AsSpan(0, length));
            if (heard == PhoneUplink.Ended) return "ended";
            if (heard is PhoneUplink.Invalid or PhoneUplink.TooFast && ++bad > BadFramesAllowed) return "bad_audio";
        }
    }
}

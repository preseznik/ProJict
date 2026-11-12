using ProJict.Core.Storage;

namespace ProJict.Core.Agents;

/// <summary>
/// A shared thing agents take turns with (#bb95b66d), such as "PC4080S game + GPU": who holds it, what for, since when
/// and until about when (a lease the holder sets and extends), and who waits for it. Kept in the workspace settings, so
/// every project's agents see it.
/// </summary>
public sealed class SharedResource
{
    public string Name { get; set; } = "";
    /// <summary>The agent holding it; null while it's free.</summary>
    public string? HeldBy { get; set; }
    /// <summary>What for, in a few words ("GRID2VR VR test").</summary>
    public string? For { get; set; }
    public DateTime Since { get; set; }
    /// <summary>Until about when the holder needs it; after that it's overdue and others may take it over.</summary>
    public DateTime Until { get; set; }
    /// <summary>The holder disconnected with no other session left; after <see cref="ResourceLocks.Grace"/> it counts as free.</summary>
    public DateTime? HolderLeftAt { get; set; }
    /// <summary>Who asked for it while someone else held it, first first; they're told when it's free.</summary>
    public List<string> Waiting { get; set; } = [];
    /// <summary>When it was last freed (released, or its holder left), and by whom, for telling those who wait.</summary>
    public DateTime? FreedAt { get; set; }
    public string? FreedBy { get; set; }
    /// <summary>Who held it before it was freed or taken over, and until when they'd said, for telling them.</summary>
    public string? LastHolder { get; set; }
    public DateTime? LastUntil { get; set; }
    /// <summary>The holder now took it over from <see cref="LastHolder"/>, whose time had run out (or the user took it, #6f04e17e).</summary>
    public bool TakenOver { get; set; }
    /// <summary>
    /// Reserved by the user in ProJict, not by an agent (#6f04e17e): agents wait for it like for any other holder, and it
    /// isn't let go when an agent disconnects. With no end time (<see cref="ResourceLocks.OpenEnded"/>) it's never overdue.
    /// </summary>
    public bool Person { get; set; }
    /// <summary>The user's reservations ahead of time (#68c97287): booked slots, and a place in the queue.</summary>
    public List<Booking> Bookings { get; set; } = [];
    /// <summary>Who held it when, for the calendar (#68c97287); the last <see cref="ResourceLocks.HistoryKept"/>.</summary>
    public List<HeldSpan> History { get; set; } = [];
}

/// <summary>
/// The user's reservation ahead of time (#68c97287): a booked slot (from, to), or a place in the queue that becomes theirs
/// the moment the thing is freed, for <see cref="Minutes"/> (or until they free it).
/// </summary>
public sealed class Booking
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string By { get; set; } = "";
    /// <summary>When the booked slot starts; null for a place in the queue.</summary>
    public DateTime? From { get; set; }
    /// <summary>When the booked slot ends; null for a place in the queue.</summary>
    public DateTime? To { get; set; }
    /// <summary>In the queue: for how long once it's theirs; null until they free it.</summary>
    public int? Minutes { get; set; }
    public string? For { get; set; }
    public DateTime BookedAt { get; set; }
    public bool Queued => From == null;
}

/// <summary>A stretch someone held a shared thing, for the calendar (#68c97287).</summary>
public sealed class HeldSpan
{
    public string By { get; set; } = "";
    public bool Person { get; set; }
    public string? For { get; set; }
    public DateTime From { get; set; }
    public DateTime To { get; set; }
}

/// <summary>What became of a claim: Booked when the user's booking (or their place in the queue) comes first.</summary>
public enum ClaimOutcome { Taken, Extended, TakenOver, Busy, Booked }

/// <summary>A reservation of the user's that began (#68c97287): their booked slot came, or what they queued for was freed.</summary>
public sealed record BookingStarted(SharedResource Resource, Booking Booking, string? TakenFrom);

/// <summary>The rules for taking turns with shared things: claim with a lease, extend, release, take over when overdue.</summary>
public static class ResourceLocks
{
    /// <summary>How long a holder that disconnected has to come back before the thing counts as free.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(2);
    public const int MinMinutes = 1, MaxMinutes = 8 * 60, DefaultMinutes = 30, MaxName = 60, MaxPurpose = 120;
    /// <summary>Free things nobody waits for go after this long.</summary>
    public static readonly TimeSpan ForgottenAfter = TimeSpan.FromDays(14);
    /// <summary>The calendar's past: this far back, and at most this many stretches per thing.</summary>
    public static readonly TimeSpan HistoryKept = TimeSpan.FromDays(60);
    public const int MaxHistory = 400;
    /// <summary>A slot can be booked this far ahead, and this long.</summary>
    public static readonly TimeSpan BookAhead = TimeSpan.FromDays(60), LongestBooking = TimeSpan.FromDays(7);

    public static List<SharedResource> Of(WorkspaceSettings settings) => settings.SharedResources ??= [];

    public static SharedResource? Find(WorkspaceSettings settings, string name) =>
        settings.SharedResources?.FirstOrDefault(r => string.Equals(r.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Held, and its holder still counts (it hasn't been gone past the grace time).</summary>
    public static bool IsHeld(SharedResource r, DateTime now) => r.HeldBy != null && !HasLeft(r, now);

    public static bool HasLeft(SharedResource r, DateTime now) => r.HolderLeftAt is { } left && now - left >= Grace;

    /// <summary>Held past the time its holder said: others may take it over.</summary>
    public static bool IsOverdue(SharedResource r, DateTime now) => IsHeld(r, now) && now > r.Until;

    /// <summary>Why the name or purpose can't be used, or null.</summary>
    public static string? Problem(string name, string? purpose)
    {
        if (name.Trim().Length < 2) return "Name the shared thing, e.g. \"PC4080S game + GPU\".";
        if (name.Trim().Length > MaxName) return $"Keep the name to {MaxName} characters.";
        if (purpose?.Trim().Length > MaxPurpose) return $"Say what for in {MaxPurpose} characters at most.";
        return null;
    }

    private static SharedResource FindOrAdd(WorkspaceSettings settings, string name, DateTime now)
    {
        var all = Of(settings);
        Forget(all, now);
        var r = Find(settings, name);
        if (r == null) all.Add(r = new SharedResource { Name = name.Trim() });
        Settle(r, now);
        return r;
    }

    /// <summary>The user's next booked slot (from now on), if any.</summary>
    public static Booking? NextSlot(SharedResource r, DateTime now) =>
        r.Bookings.Where(b => !b.Queued && b.To > now).OrderBy(b => b.From).FirstOrDefault();

    /// <summary>The user's place in the queue, if they have one.</summary>
    public static Booking? QueuedFor(SharedResource r) => r.Bookings.FirstOrDefault(b => b.Queued);

    /// <summary>
    /// The agent claims it for <paramref name="minutes"/>: taken when free (or its holder left), extended when it's the
    /// agent's already, taken over when overdue, else busy (and the agent waits for it). The user's bookings come first
    /// (#68c97287): a claim ends when their next slot begins, and when it begins now (or they're queued for it while it's
    /// free) the claim is Booked. Returns the outcome and who held it before (or booked it).
    /// </summary>
    public static (ClaimOutcome Outcome, SharedResource Resource, string? Before) Claim(WorkspaceSettings settings, string name, string agent, string? purpose, int minutes, DateTime now)
    {
        if (Problem(name, purpose) is { } problem) throw new ArgumentException(problem);
        minutes = Math.Clamp(minutes, MinMinutes, MaxMinutes);
        var r = FindOrAdd(settings, name, now);
        var until = now.AddMinutes(minutes);
        var slot = NextSlot(r, now);
        if (slot != null && slot.From < until) until = slot.From!.Value;
        var mine = string.Equals(r.HeldBy, agent, StringComparison.OrdinalIgnoreCase);
        if (mine && until - now >= TimeSpan.FromMinutes(MinMinutes))
        {
            r.Until = until;
            if (!string.IsNullOrWhiteSpace(purpose)) r.For = purpose.Trim();
            r.HolderLeftAt = null;
            return (ClaimOutcome.Extended, r, null);
        }
        if (!mine && IsHeld(r, now) && !IsOverdue(r, now))
        {
            Wait(r, agent);
            return (ClaimOutcome.Busy, r, r.HeldBy);
        }
        // The user's booking begins (about) now, or they're next in the queue: it's theirs, the agent waits.
        var first = slot != null && until - now < TimeSpan.FromMinutes(MinMinutes) ? slot : !IsHeld(r, now) || IsOverdue(r, now) ? QueuedFor(r) : null;
        if (first != null)
        {
            Wait(r, agent);
            return (ClaimOutcome.Booked, r, first.By);
        }
        if (mine) return (ClaimOutcome.Extended, r, null);
        var before = IsHeld(r, now) ? r.HeldBy : null;
        Take(r, agent, person: false, purpose, until, now);
        return (before == null ? ClaimOutcome.Taken : ClaimOutcome.TakenOver, r, before);
    }

    private static void Wait(SharedResource r, string who)
    {
        if (!r.Waiting.Contains(who, StringComparer.OrdinalIgnoreCase)) r.Waiting.Add(who);
    }

    /// <summary>
    /// It's <paramref name="holder"/>'s now, until <paramref name="until"/>: whoever held it before is written in its
    /// history (and, taken off it by the user, waits for it first); an overdue holder was taken over.
    /// </summary>
    private static void Take(SharedResource r, string holder, bool person, string? purpose, DateTime until, DateTime now)
    {
        var before = IsHeld(r, now) ? r.HeldBy : null;
        if (before != null)
        {
            Record(r, now);
            r.LastHolder = before;
            r.LastUntil = r.Until;
            if (person && !r.Person && !r.Waiting.Contains(before, StringComparer.OrdinalIgnoreCase)) r.Waiting.Insert(0, before);
        }
        r.TakenOver = before != null;
        r.HeldBy = holder;
        r.Person = person;
        r.For = string.IsNullOrWhiteSpace(purpose) ? null : purpose.Trim();
        r.Since = now;
        r.Until = until;
        r.HolderLeftAt = null;
        r.Waiting.RemoveAll(w => string.Equals(w, holder, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The user reserves it themselves (#6f04e17e), for <paramref name="minutes"/> or, when null, until they free it. Free or
    /// not, it's theirs at once: an agent holding it is taken off it (and waits for it, so it's told when it's free again).
    /// Agents asking for it meanwhile wait, as for any holder. Returns the outcome and the agent that held it before.
    /// </summary>
    public static (ClaimOutcome Outcome, SharedResource Resource, string? Before) Reserve(WorkspaceSettings settings, string name, string person, string? purpose,
        int? minutes, DateTime now)
    {
        if (Problem(name, purpose) is { } problem) throw new ArgumentException(problem);
        var r = FindOrAdd(settings, name, now);
        var until = minutes is { } m ? now.AddMinutes(Math.Clamp(m, MinMinutes, MaxMinutes)) : DateTime.MaxValue;
        // Taking it now ends their place in the queue.
        r.Bookings.RemoveAll(b => b.Queued && string.Equals(b.By, person, StringComparison.OrdinalIgnoreCase));
        if (string.Equals(r.HeldBy, person, StringComparison.OrdinalIgnoreCase) && IsHeld(r, now))
        {
            r.Until = until;
            r.Person = true;
            if (!string.IsNullOrWhiteSpace(purpose)) r.For = purpose.Trim();
            return (ClaimOutcome.Extended, r, null);
        }
        var before = IsHeld(r, now) ? r.HeldBy : null;
        Take(r, person, person: true, purpose, until, now);
        return (before == null ? ClaimOutcome.Taken : ClaimOutcome.TakenOver, r, before);
    }

    /// <summary>
    /// The user queues for it (#68c97287): when it's freed it's theirs at once, ahead of agents just waiting, for
    /// <paramref name="minutes"/> (or until they free it). Free now, it's theirs now. True when it's theirs now.
    /// </summary>
    public static (bool Now, SharedResource Resource) Queue(WorkspaceSettings settings, string name, string person, int? minutes, DateTime now)
    {
        if (Problem(name, null) is { } problem) throw new ArgumentException(problem);
        var r = FindOrAdd(settings, name, now);
        if (string.Equals(r.HeldBy, person, StringComparison.OrdinalIgnoreCase) && IsHeld(r, now)) return (true, r);
        if (!IsHeld(r, now))
        {
            Reserve(settings, name, person, null, minutes, now);
            return (true, r);
        }
        r.Bookings.RemoveAll(b => b.Queued && string.Equals(b.By, person, StringComparison.OrdinalIgnoreCase));
        r.Bookings.Add(new Booking { By = person, Minutes = minutes is { } m ? Math.Clamp(m, MinMinutes, MaxMinutes) : null, BookedAt = now });
        return (false, r);
    }

    /// <summary>
    /// The user books a slot ahead (#68c97287): from <paramref name="from"/> to <paramref name="to"/> it's theirs (an agent
    /// on it then is taken off), and agents' claims before it end when it begins. A slot that has begun already is theirs now.
    /// Returns the booking (null when it began at once). Throws ArgumentException when it can't be booked.
    /// </summary>
    public static Booking? Book(WorkspaceSettings settings, string name, string person, DateTime from, DateTime to, string? purpose, DateTime now)
    {
        if (Problem(name, purpose) is { } problem) throw new ArgumentException(problem);
        if (to <= from) throw new ArgumentException("A booking ends after it starts.");
        if (to <= now) throw new ArgumentException("That time is over already.");
        if (to - from > LongestBooking) throw new ArgumentException($"Book at most {LongestBooking.TotalDays:0} days at a time.");
        if (from - now > BookAhead) throw new ArgumentException($"Book at most {BookAhead.TotalDays:0} days ahead.");
        var r = FindOrAdd(settings, name, now);
        if (r.Bookings.FirstOrDefault(b => !b.Queued && b.From < to && from < b.To) is { } clash)
            throw new ArgumentException($"It's booked from {Clock(clash.From!.Value)} to {Clock(clash.To!.Value)} already.");
        var booking = new Booking { By = person, From = from, To = to, For = string.IsNullOrWhiteSpace(purpose) ? null : purpose.Trim(), BookedAt = now };
        r.Bookings.Add(booking);
        if (from > now) return booking;
        Advance(settings, now);
        return null;
    }

    /// <summary>Cancels one of the user's bookings, or their place in the queue; false when it isn't there.</summary>
    public static bool Cancel(WorkspaceSettings settings, string name, Guid booking) =>
        Find(settings, name)?.Bookings.RemoveAll(b => b.Id == booking) > 0;

    /// <summary>
    /// What's due now (#68c97287): a booked slot that began becomes the user's (an agent on it is taken off it, and waits
    /// first), and what the user queued for becomes theirs once it's free. Slots that went by unused are dropped. Call it on
    /// every change and every little while. Returns the reservations that began.
    /// </summary>
    public static List<BookingStarted> Advance(WorkspaceSettings settings, DateTime now)
    {
        var started = new List<BookingStarted>();
        foreach (var r in settings.SharedResources ?? [])
        {
            Settle(r, now);
            r.Bookings.RemoveAll(b => !b.Queued && b.To <= now);
            if (r.Bookings.Where(b => !b.Queued && b.From <= now).OrderBy(b => b.From).FirstOrDefault() is { } due)
            {
                r.Bookings.Remove(due);
                var before = IsHeld(r, now) && !string.Equals(r.HeldBy, due.By, StringComparison.OrdinalIgnoreCase) ? r.HeldBy : null;
                if (before == null && IsHeld(r, now))
                {
                    // Theirs already: it lasts at least to the slot's end.
                    r.Person = true;
                    if (!OpenEnded(r) && r.Until < due.To!.Value) r.Until = due.To.Value;
                }
                else Take(r, due.By, person: true, due.For, due.To!.Value, now);
                started.Add(new BookingStarted(r, due, before));
                continue;
            }
            if ((!IsHeld(r, now) || IsOverdue(r, now)) && QueuedFor(r) is { } next)
            {
                r.Bookings.Remove(next);
                var before = IsHeld(r, now) ? r.HeldBy : null;
                var until = next.Minutes is { } m ? now.AddMinutes(m) : DateTime.MaxValue;
                if (NextSlot(r, now) is { } slot && slot.From < until) until = slot.From!.Value;
                Take(r, next.By, person: true, next.For, until, now);
                started.Add(new BookingStarted(r, next, before));
            }
        }
        return started;
    }

    /// <summary>A holder that left for good lets it go (at the end of its grace time), so it's written in the history.</summary>
    private static void Settle(SharedResource r, DateTime now)
    {
        if (r.HeldBy != null && HasLeft(r, now)) Free(r, r.HeldBy, r.HolderLeftAt!.Value + Grace);
    }

    /// <summary>Reserved with no end time: it's held until its holder frees it, and never overdue.</summary>
    public static bool OpenEnded(SharedResource r) => r.Until == DateTime.MaxValue;

    /// <summary>"until 13:35 (8 min left)" (with "about" when <paramref name="about"/>), or "until they free it".</summary>
    public static string UntilPhrase(SharedResource r, DateTime now, bool about = false) =>
        OpenEnded(r) ? "until they free it" : "until " + (about ? "about " : "") + UntilText(r, now);

    /// <summary>The agent (or the user, <paramref name="by"/>) frees it; null when the agent doesn't hold it.</summary>
    public static SharedResource? Release(WorkspaceSettings settings, string name, string? holder, string by, DateTime now)
    {
        var r = Find(settings, name);
        if (r?.HeldBy == null || (holder != null && !string.Equals(r.HeldBy, holder, StringComparison.OrdinalIgnoreCase))) return null;
        Free(r, by, now);
        return r;
    }

    /// <summary>The agent's last session ended: what it holds counts as free after the grace time unless it comes back.</summary>
    public static void Left(WorkspaceSettings settings, string agent, DateTime now)
    {
        foreach (var r in settings.SharedResources ?? [])
            if (!r.Person && string.Equals(r.HeldBy, agent, StringComparison.OrdinalIgnoreCase) && r.HolderLeftAt == null) r.HolderLeftAt = now;
    }

    /// <summary>The agent is back: what it held is still its own.</summary>
    public static bool Back(WorkspaceSettings settings, string agent, DateTime now)
    {
        var changed = false;
        foreach (var r in settings.SharedResources ?? [])
            if (string.Equals(r.HeldBy, agent, StringComparison.OrdinalIgnoreCase) && r.HolderLeftAt != null)
            {
                if (HasLeft(r, now)) Free(r, r.HeldBy!, r.HolderLeftAt.Value + Grace);
                else r.HolderLeftAt = null;
                changed = true;
            }
        return changed;
    }

    private static void Free(SharedResource r, string by, DateTime at)
    {
        Record(r, at);
        r.LastHolder = r.HeldBy;
        r.LastUntil = r.Until;
        r.TakenOver = false;
        r.FreedBy = by;
        r.FreedAt = at;
        r.HeldBy = null;
        r.Person = false;
        r.For = null;
        r.HolderLeftAt = null;
    }

    /// <summary>Writes the holder's stretch, up to <paramref name="end"/>, in the history (the last 60 days of it are kept).</summary>
    private static void Record(SharedResource r, DateTime end)
    {
        if (r.HeldBy == null) return;
        r.History.Add(new HeldSpan { By = r.HeldBy, Person = r.Person, For = r.For, From = r.Since, To = end < r.Since ? r.Since : end });
        r.History.RemoveAll(h => end - h.To > HistoryKept);
        if (r.History.Count > MaxHistory) r.History.RemoveRange(0, r.History.Count - MaxHistory);
    }

    /// <summary>Free things nobody waits for or booked, freed long ago, go.</summary>
    private static void Forget(List<SharedResource> all, DateTime now) =>
        all.RemoveAll(r => r.HeldBy == null && r.Waiting.Count == 0 && r.Bookings.Count == 0 && r.FreedAt is { } freed && now - freed > ForgottenAfter);

    /// <summary>"13:35 (8 min left)" or "13:35 (overdue by 5 min)".</summary>
    public static string UntilText(SharedResource r, DateTime now)
    {
        var minutes = (int)Math.Round(Math.Abs((r.Until - now).TotalMinutes));
        return r.Until.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture) +
               (now > r.Until ? $" (overdue by {minutes} min)" : $" ({minutes} min left)");
    }

    /// <summary>"14:00" today, "Thu 14:00" within the week, "13 Nov 14:00" further.</summary>
    public static string Clock(DateTime at)
    {
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        var days = (at.Date - DateTime.Today).TotalDays;
        return at.ToString(days == 0 ? "HH:mm" : days is > 0 and < 7 ? "ddd HH:mm" : "d MMM HH:mm", culture);
    }

    // ───────────── The calendar (#68c97287) ─────────────

    /// <summary>
    /// What the calendar shows between <paramref name="from"/> and <paramref name="to"/>, for each thing: who held it
    /// (the past), who holds it now (until when they said, or an hour on when there's no end), the user's place in the
    /// queue (from when it's expected to be free) and their booked slots.
    /// </summary>
    public static List<CalendarSpan> Calendar(IEnumerable<SharedResource> all, DateTime from, DateTime to, DateTime now)
    {
        var spans = new List<CalendarSpan>();
        foreach (var r in all)
        {
            foreach (var h in r.History) spans.Add(new CalendarSpan(r.Name, h.By, h.Person, SpanKind.Past, h.From, h.To, h.For, null, false));
            var freeFrom = now;
            if (IsHeld(r, now))
            {
                var open = OpenEnded(r);
                var end = open ? now.AddHours(1) : r.Until < now ? now : r.Until;
                spans.Add(new CalendarSpan(r.Name, r.HeldBy!, r.Person, SpanKind.Now, r.Since, end, r.For, null, open));
                freeFrom = end;
            }
            if (QueuedFor(r) is { } q)
                spans.Add(new CalendarSpan(r.Name, q.By, true, SpanKind.Queued, freeFrom, freeFrom.AddMinutes(q.Minutes ?? 60), q.For, q.Id, q.Minutes == null));
            foreach (var b in r.Bookings.Where(b => !b.Queued))
                spans.Add(new CalendarSpan(r.Name, b.By, true, SpanKind.Booked, b.From!.Value, b.To!.Value, b.For, b.Id, false));
        }
        return spans.Where(s => s.From < to && s.To > from).OrderBy(s => s.From).ToList();
    }
}

/// <summary>Held before, held now, the user's place in the queue, or a slot they booked.</summary>
public enum SpanKind { Past, Now, Queued, Booked }

/// <summary>
/// A stretch on the calendar: which thing, who, held or reserved how, from when to when, what for; the booking's id for
/// cancelling it; <see cref="OpenEnded"/> when it lasts until it's freed (drawn as an hour, fading).
/// </summary>
public sealed record CalendarSpan(string Resource, string By, bool Person, SpanKind Kind, DateTime From, DateTime To, string? For, Guid? Booking, bool OpenEnded);

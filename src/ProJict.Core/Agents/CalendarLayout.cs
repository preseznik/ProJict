namespace ProJict.Core.Agents;

/// <summary>
/// Which calendar blocks go side by side, like Outlook (#08597a3b). It goes by where they're drawn, not by their times: a
/// short hold is drawn taller than its minutes, so holds a few minutes apart would cover each other's names. Each run of
/// overlapping blocks shares the column between only as many as it needs, not the whole day's most.
/// </summary>
public static class CalendarLayout
{
    /// <summary>For each block (its top and height as drawn), the slot it takes and how many slots its run has.</summary>
    public static (int Slot, int Of)[] SideBySide(IReadOnlyList<(double Top, double Height)> blocks)
    {
        var result = new (int Slot, int Of)[blocks.Count];
        var order = Enumerable.Range(0, blocks.Count).OrderBy(i => blocks[i].Top).ThenByDescending(i => blocks[i].Height);
        var run = new List<int>();
        var bottoms = new List<double>();
        var runBottom = double.NegativeInfinity;
        void Close()
        {
            foreach (var i in run) result[i].Of = bottoms.Count;
            run.Clear();
            bottoms.Clear();
        }
        foreach (var i in order)
        {
            var (top, height) = blocks[i];
            if (top >= runBottom) Close();
            // The first slot that's free by then, else a new one.
            var slot = bottoms.FindIndex(b => b <= top);
            if (slot < 0)
            {
                slot = bottoms.Count;
                bottoms.Add(0);
            }
            bottoms[slot] = top + height;
            runBottom = Math.Max(runBottom, top + height);
            run.Add(i);
            result[i] = (slot, 1);
        }
        Close();
        return result;
    }

    /// <summary>
    /// Holds before now on the same machine that would be drawn over each other (each is drawn at least
    /// <paramref name="minDrawn"/> long) become one stretch, from the first's start to the last's end, made of them
    /// (<see cref="CalendarSpan.Parts"/>) (#7ee43345): agents' many holds of a few minutes read as one busy stretch, not a
    /// heap of slivers. What's held now, bookings and the queue stay as they are.
    /// </summary>
    public static List<CalendarSpan> JoinPast(IEnumerable<CalendarSpan> spans, TimeSpan minDrawn)
    {
        var all = spans.ToList();
        var result = all.Where(s => s.Kind != SpanKind.Past).ToList();
        foreach (var machine in all.Where(s => s.Kind == SpanKind.Past).GroupBy(s => s.Resource, StringComparer.OrdinalIgnoreCase))
        {
            var run = new List<CalendarSpan>();
            var drawnEnd = DateTime.MinValue;
            foreach (var s in machine.OrderBy(s => s.From))
            {
                if (run.Count > 0 && s.From >= drawnEnd)
                {
                    result.Add(Joined(run));
                    run = [];
                }
                run.Add(s);
                var end = s.From + minDrawn > s.To ? s.From + minDrawn : s.To;
                if (run.Count == 1 || end > drawnEnd) drawnEnd = end;
            }
            if (run.Count > 0) result.Add(Joined(run));
        }
        return [.. result.OrderBy(s => s.From)];
    }

    private static CalendarSpan Joined(List<CalendarSpan> run)
    {
        if (run.Count == 1) return run[0];
        var first = run[0];
        var who = string.Join(", ", run.Select(p => p.By).Distinct(StringComparer.OrdinalIgnoreCase));
        return new CalendarSpan(first.Resource, who, false, SpanKind.Past, first.From, run.Max(p => p.To), null, null, false) { Parts = run };
    }
}

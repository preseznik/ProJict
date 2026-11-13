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
}

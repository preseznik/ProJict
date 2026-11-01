namespace ProJict.Core.Wiki;

public enum DiffKind { Same, Added, Removed }

/// <summary>A line of a diff between two versions of a page.</summary>
public sealed record DiffLine(DiffKind Kind, string Text);

/// <summary>What a three-way merge made: the merged text, or (when both sides changed the same paragraphs) a clash.</summary>
public sealed record MergeResult(bool Clean, string Text, int Clashes);

/// <summary>
/// Diffs and merges of page text. Merging goes by paragraph (a fenced code block counts as one): two people who
/// changed different paragraphs of the same version both keep their changes; changes to the same paragraph clash.
/// </summary>
public static class TextMerge
{
    /// <summary>Beyond this many cells the line-up of two texts falls back to their common start and end.</summary>
    private const long MaxCells = 4_000_000;

    // ───────────── Diff ─────────────

    /// <summary>The lines of <paramref name="newText"/> against <paramref name="oldText"/>: kept, added and removed.</summary>
    public static List<DiffLine> Lines(string oldText, string newText)
    {
        var a = SplitLines(oldText);
        var b = SplitLines(newText);
        var result = new List<DiffLine>();
        var ai = 0;
        var bi = 0;
        foreach (var (i, j) in Match(a, b, (x, y) => x == y))
        {
            while (ai < i) result.Add(new DiffLine(DiffKind.Removed, a[ai++]));
            while (bi < j) result.Add(new DiffLine(DiffKind.Added, b[bi++]));
            result.Add(new DiffLine(DiffKind.Same, a[ai]));
            ai++;
            bi++;
        }
        while (ai < a.Length) result.Add(new DiffLine(DiffKind.Removed, a[ai++]));
        while (bi < b.Length) result.Add(new DiffLine(DiffKind.Added, b[bi++]));
        return result;
    }

    /// <summary>"+3 −1": how many lines a version added and removed.</summary>
    public static (int Added, int Removed) Count(string oldText, string newText)
    {
        var lines = Lines(oldText, newText);
        return (lines.Count(l => l.Kind == DiffKind.Added), lines.Count(l => l.Kind == DiffKind.Removed));
    }

    private static string[] SplitLines(string text) => text.Length == 0 ? [] : text.Replace("\r\n", "\n").Split('\n');

    // ───────────── Merge ─────────────

    /// <summary>
    /// Merges two edits of the same version, <paramref name="mine"/> and <paramref name="theirs"/>, paragraph by
    /// paragraph. Each side's changes are runs of paragraphs it replaced, removed or added; runs that don't overlap
    /// all count, even right next to each other. Both changing the same paragraphs differently (or adding different
    /// paragraphs at the same place) is a clash, and the result then isn't <see cref="MergeResult.Clean"/> (its text is
    /// <paramref name="theirs"/>).
    /// </summary>
    public static MergeResult Merge(string baseText, string mine, string theirs)
    {
        if (mine == theirs) return new MergeResult(true, mine, 0);
        if (mine == baseText) return new MergeResult(true, theirs, 0);
        if (theirs == baseText) return new MergeResult(true, mine, 0);

        var o = Paragraphs(baseText);
        var a = Paragraphs(mine);
        var b = Paragraphs(theirs);
        var inB = new int[o.Count];
        Array.Fill(inB, -1);
        var matchB = Match(o, b, Same);
        foreach (var (i, j) in matchB) inB[i] = j;
        var mineChanges = Changes(Match(o, a, Same), o.Count, a);
        var theirChanges = Changes(matchB, o.Count, b);

        var clashes = 0;
        var changes = new List<Hunk>(mineChanges);
        foreach (var other in theirChanges)
        {
            var overlapping = mineChanges.Where(m => Overlap(m, other)).ToList();
            if (overlapping.Count == 0) changes.Add(other);
            // The same change on both sides counts once.
            else if (overlapping.Count > 1 || overlapping[0].Start != other.Start || overlapping[0].End != other.End ||
                     !SameRun(overlapping[0].Text, other.Text)) clashes++;
        }
        if (clashes > 0) return new MergeResult(false, theirs, clashes);

        // Additions go before a change starting at the same place.
        changes.Sort((x, y) => x.Start != y.Start ? x.Start.CompareTo(y.Start) : (x.End - x.Start).CompareTo(y.End - y.Start));
        var merged = new List<string>();
        var at = 0;
        foreach (var change in changes)
        {
            // Untouched paragraphs as they are in theirs, which may differ only in spacing.
            for (; at < change.Start; at++) merged.Add(inB[at] >= 0 ? b[inB[at]] : o[at]);
            merged.AddRange(change.Text);
            at = Math.Max(at, change.End);
        }
        for (; at < o.Count; at++) merged.Add(inB[at] >= 0 ? b[inB[at]] : o[at]);
        return new MergeResult(true, Join(merged), 0);
    }

    private static bool Same(string x, string y) => x.TrimEnd() == y.TrimEnd();

    private static bool SameRun(List<string> x, List<string> y) => x.Count == y.Count && x.Zip(y).All(p => Same(p.First, p.Second));

    /// <summary>One side's change: base paragraphs [Start, End) became <see cref="Text"/> (Start = End: added there).</summary>
    private sealed record Hunk(int Start, int End, List<string> Text);

    /// <summary>A side's changes against the base, from how their paragraphs line up.</summary>
    private static List<Hunk> Changes(List<(int A, int B)> matches, int baseCount, List<string> side)
    {
        var hunks = new List<Hunk>();
        int o = 0, s = 0;
        foreach (var (i, j) in matches.Append((baseCount, side.Count)))
        {
            if (i > o || j > s) hunks.Add(new Hunk(o, i, side.GetRange(s, j - s)));
            o = i + 1;
            s = j + 1;
        }
        return hunks;
    }

    /// <summary>
    /// Whether two changes touch the same paragraphs: overlapping runs, two additions at the same place, or an addition
    /// inside a run the other replaced. Changes that only meet at an edge don't.
    /// </summary>
    private static bool Overlap(Hunk x, Hunk y)
    {
        var xAdds = x.Start == x.End;
        var yAdds = y.Start == y.End;
        if (xAdds && yAdds) return x.Start == y.Start;
        if (xAdds) return y.Start < x.Start && x.Start < y.End;
        if (yAdds) return x.Start < y.Start && y.Start < x.End;
        return x.Start < y.End && y.Start < x.End;
    }

    /// <summary>
    /// The text in paragraphs, each with the blank lines after it, so joining them gives the text back exactly. A
    /// fenced code block (``` or ~~~) is one paragraph, blank lines and all.
    /// </summary>
    public static List<string> Paragraphs(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var paragraphs = new List<string>();
        var current = new List<string>();
        string? fence = null;
        var inBlank = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var blank = line.Trim().Length == 0;
            if (fence == null && !blank && inBlank && current.Count > 0)
            {
                paragraphs.Add(string.Join('\n', current));
                current.Clear();
            }
            inBlank = fence == null && blank;
            current.Add(line);
            var trimmed = line.TrimStart();
            if (fence == null && (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal)))
                fence = trimmed[..3];
            else if (fence != null && trimmed.StartsWith(fence, StringComparison.Ordinal) && trimmed.Trim().All(c => c == fence[0]))
                fence = null;
        }
        if (current.Count > 0) paragraphs.Add(string.Join('\n', current));
        return paragraphs;
    }

    /// <summary>Joins paragraphs back up; one that came last on its side but no longer is gets the blank line it lacked.</summary>
    private static string Join(List<string> paragraphs)
    {
        var parts = new List<string>(paragraphs.Count);
        for (var i = 0; i < paragraphs.Count; i++)
        {
            var p = paragraphs[i];
            var lastLine = p.LastIndexOf('\n') is >= 0 and var at ? p[(at + 1)..] : null;
            if (i < paragraphs.Count - 1 && (lastLine == null || lastLine.Trim().Length > 0)) p += "\n";
            parts.Add(p);
        }
        return string.Join('\n', parts);
    }

    // ───────────── Lining up ─────────────

    /// <summary>
    /// The longest common subsequence of two lists, as index pairs in order. Their common start and end are matched
    /// first; what's left is lined up in full unless it's huge (then only the common start and end count).
    /// </summary>
    private static List<(int A, int B)> Match<T>(IReadOnlyList<T> a, IReadOnlyList<T> b, Func<T, T, bool> same)
    {
        var pairs = new List<(int, int)>();
        var start = 0;
        while (start < a.Count && start < b.Count && same(a[start], b[start]))
        {
            pairs.Add((start, start));
            start++;
        }
        var endA = a.Count;
        var endB = b.Count;
        var tail = new List<(int, int)>();
        while (endA > start && endB > start && same(a[endA - 1], b[endB - 1]))
        {
            endA--;
            endB--;
            tail.Add((endA, endB));
        }
        var n = endA - start;
        var m = endB - start;
        if (n > 0 && m > 0 && (long)n * m <= MaxCells)
        {
            var lengths = new int[n + 1, m + 1];
            for (var i = n - 1; i >= 0; i--)
                for (var j = m - 1; j >= 0; j--)
                    lengths[i, j] = same(a[start + i], b[start + j]) ? lengths[i + 1, j + 1] + 1 : Math.Max(lengths[i + 1, j], lengths[i, j + 1]);
            int x = 0, y = 0;
            while (x < n && y < m)
            {
                if (same(a[start + x], b[start + y]))
                {
                    pairs.Add((start + x, start + y));
                    x++;
                    y++;
                }
                else if (lengths[x + 1, y] >= lengths[x, y + 1]) x++;
                else y++;
            }
        }
        tail.Reverse();
        pairs.AddRange(tail);
        return pairs;
    }
}

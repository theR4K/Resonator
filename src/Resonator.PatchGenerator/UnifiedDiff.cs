using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Resonator.PatchGenerator;

/// <summary>
/// Unified diffs in exactly the shapes the pipeline's patch applier reads:
///   content   --- a/&lt;path&gt;  +++ b/&lt;path&gt;  @@ hunks with 3 lines of context (more on request)
///   deletion  --- a/&lt;path&gt;  +++ /dev/null   every line removed
/// LCS-based, so minimal and exact.
/// </summary>
internal static class UnifiedDiff
{
    public const int DefaultContext = 3;

    readonly record struct Edit(char Tag, string Line)   // ' ' keep, '-' remove, '+' add
    {
        public bool InOld => Tag != '+';
        public bool InNew => Tag != '-';
    }

    /// <summary>The diff of <paramref name="before"/> -> <paramref name="after"/>; empty when they are equal.</summary>
    public static string Content(string relativePath, List<string> before, List<string> after, int context = DefaultContext)
    {
        var edits = Edits(before, after);
        var hunks = HunkRanges(edits, context);
        if (hunks.Count == 0) return "";

        string path = relativePath.Replace('\\', '/');
        var sb = new StringBuilder($"--- a/{path}\n+++ b/{path}\n");
        foreach (var (start, end) in hunks)
        {
            var preceding = edits.Take(start).ToList();
            var hunk = edits.Skip(start).Take(end - start + 1).ToList();
            sb.Append($"@@ -{preceding.Count(e => e.InOld) + 1},{hunk.Count(e => e.InOld)} +{preceding.Count(e => e.InNew) + 1},{hunk.Count(e => e.InNew)} @@\n");
            foreach (var e in hunk) sb.Append(e.Tag).Append(e.Line).Append('\n');
        }
        return sb.ToString();
    }

    public static string Deletion(string relativePath, List<string> before)
    {
        var sb = new StringBuilder($"--- a/{relativePath.Replace('\\', '/')}\n+++ /dev/null\n@@ -1,{before.Count} @@\n");
        foreach (string line in before) sb.Append('-').Append(line).Append('\n');
        return sb.ToString();
    }

    /// <summary>Every change with <paramref name="context"/> edits around it; overlapping or touching ranges merge.</summary>
    static List<(int Start, int End)> HunkRanges(List<Edit> edits, int context)
    {
        var ranges = new List<(int Start, int End)>();
        for (int k = 0; k < edits.Count; k++)
        {
            if (edits[k].Tag == ' ') continue;
            int start = Math.Max(0, k - context), end = Math.Min(edits.Count - 1, k + context);
            if (ranges.Count > 0 && start <= ranges[^1].End + 1) ranges[^1] = (ranges[^1].Start, Math.Max(ranges[^1].End, end));
            else ranges.Add((start, end));
        }
        return ranges;
    }

    /// <summary>The edit script, in order, by backtracking a full LCS table.</summary>
    static List<Edit> Edits(List<string> a, List<string> b)
    {
        int n = a.Count, m = b.Count, w = m + 1;
        var lcs = new int[(n + 1) * w];
        for (int i = 1; i <= n; i++)
            for (int j = 1; j <= m; j++)
                lcs[i * w + j] = a[i - 1] == b[j - 1]
                    ? lcs[(i - 1) * w + j - 1] + 1
                    : Math.Max(lcs[(i - 1) * w + j], lcs[i * w + j - 1]);

        var edits = new List<Edit>(n + m);
        int x = n, y = m;
        while (x > 0 && y > 0)
        {
            if (a[x - 1] == b[y - 1]) { edits.Add(new(' ', a[--x])); y--; }
            else if (lcs[(x - 1) * w + y] >= lcs[x * w + y - 1]) edits.Add(new('-', a[--x]));
            else edits.Add(new('+', b[--y]));
        }
        while (x > 0) edits.Add(new('-', a[--x]));
        while (y > 0) edits.Add(new('+', b[--y]));
        edits.Reverse();
        return edits;
    }
}

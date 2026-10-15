using System;
using System.Collections.Generic;
using System.IO;

namespace Resonator.Pipeline.Patching;

public enum PatchResult { Applied, AlreadyApplied }

/// <summary>
/// Applies a <see cref="Patch"/> to a source tree. Hunk by hunk, located by content (nearest the
/// header's line first), so patches survive line drift and can grow incrementally - and applying
/// an applied patch is a no-op.
/// </summary>
public static class PatchApplier
{
    public static PatchResult Apply(string srcDir, Patch patch)
    {
        string root = Path.GetFullPath(srcDir);
        string file = Path.GetFullPath(Path.Combine(root, patch.FilePath));
        // trailing separator: src/FrooxEngineX must not pass for src/FrooxEngine
        if (!file.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new Exception("patch target escapes src dir: " + file);

        if (patch.IsDeletion)
        {
            if (!File.Exists(file)) return PatchResult.AlreadyApplied;
            File.Delete(file);
            return PatchResult.Applied;
        }
        if (!File.Exists(file)) throw new Exception("patch target missing: " + file);

        string text = File.ReadAllText(file);
        var lines = TextLines.Split(text);
        int applied = 0, unmatched = 0;
        foreach (var hunk in patch.Hunks)
        {
            switch (ApplyHunk(lines, hunk))
            {
                case true: applied++; break;
                case null: unmatched++; break;
            }
        }

        if (unmatched > 0) throw new Exception($"{unmatched} hunk(s) did not match (and were not already applied)");
        if (applied == 0) return PatchResult.AlreadyApplied;

        string eol = TextLines.DetectEol(text);
        bool finalNewline = text.EndsWith('\n') || text.EndsWith('\r');
        File.WriteAllText(file, string.Join(eol, lines) + (finalNewline ? eol : ""));
        return PatchResult.Applied;
    }

    /// <summary>true = applied, false = already applied, null = neither image found.</summary>
    static bool? ApplyHunk(List<string> lines, Patch.Hunk hunk)
    {
        int before = Find(lines, hunk.OldLines, hunk.OldStart - 1);
        // A deletion hunk's after-image is only context, found anywhere: the before image decides.
        if (hunk.IsDeletion)
        {
            if (before >= 0) return Replace(lines, hunk, before);
            return Find(lines, hunk.NewLines, hunk.NewStart - 1) >= 0 ? false : null;
        }
        int after = Find(lines, hunk.NewLines, hunk.NewStart - 1);
        if (after < 0) return before >= 0 ? Replace(lines, hunk, before) : null;
        if (before < 0) return false;
        // Both images occur. Decompiled code repeats (switch cases, property bodies), so either can
        // turn up elsewhere: an addition's context survives applying, an after-image may already
        // exist further away. The one nearer the hunk's own position is this hunk; a tie counts as
        // applied (re-applying would duplicate).
        return Math.Abs(before - (hunk.OldStart - 1)) < Math.Abs(after - (hunk.NewStart - 1))
            ? Replace(lines, hunk, before)
            : false;
    }

    static bool Replace(List<string> lines, Patch.Hunk hunk, int at)
    {
        lines.RemoveRange(at, hunk.OldLines.Count);
        lines.InsertRange(at, hunk.NewLines);
        return true;
    }

    /// <summary>
    /// Where <paramref name="block"/> occurs (exact, ordinal), searching outward from the hint line;
    /// -1 if nowhere. An empty block is never found (a deletion's empty after-image would read as
    /// "already applied").
    /// </summary>
    static int Find(List<string> lines, List<string> block, int hint)
    {
        int last = lines.Count - block.Count;
        if (block.Count == 0 || last < 0) return -1;
        hint = Math.Clamp(hint, 0, last);
        for (int distance = 0; distance <= last; distance++)
        {
            if (hint + distance <= last && MatchesAt(lines, block, hint + distance)) return hint + distance;
            if (distance > 0 && hint - distance >= 0 && MatchesAt(lines, block, hint - distance)) return hint - distance;
        }
        return -1;
    }

    static bool MatchesAt(List<string> lines, List<string> block, int at)
    {
        for (int j = 0; j < block.Count; j++)
            if (!string.Equals(lines[at + j], block[j], StringComparison.Ordinal)) return false;
        return true;
    }
}

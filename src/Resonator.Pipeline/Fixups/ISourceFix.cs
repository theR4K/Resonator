using System;
using System.Collections.Generic;
using System.IO;

namespace Resonator.Pipeline.Fixups;

/// <summary>
/// A mechanical repair of a decompiler artifact - C# the decompiler renders but the compiler
/// rejects. Applied to every decompiled file before the patches.
/// </summary>
public interface ISourceFix
{
    /// <summary>Short name for the fixup summary line.</summary>
    string Name { get; }

    /// <summary>Rewrite the files in place; returns the number of fixes made.</summary>
    int Apply(IReadOnlyList<string> files);
}

internal static class SourceFiles
{
    /// <summary>Apply <paramref name="rewrite"/> to every file; files whose text changed are written back. Returns how many.</summary>
    public static int Rewrite(IEnumerable<string> files, Func<string, string> rewrite)
    {
        int changed = 0;
        foreach (string file in files)
        {
            string text = File.ReadAllText(file);
            string fixedText = rewrite(text);
            if (fixedText == text) continue;
            File.WriteAllText(file, fixedText);
            changed++;
        }
        return changed;
    }
}

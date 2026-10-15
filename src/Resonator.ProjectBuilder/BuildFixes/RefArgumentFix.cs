using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Resonator.Pipeline.Infrastructure;
using Resonator.Pipeline.Targets;

namespace Resonator.ProjectBuilder.BuildFixes;

/// <summary>
/// 'ref' passed where the callee declares 'in' (the decompiler renders in-arguments as ref). The
/// compiler points at the bad 'ref': remove the nearest one at or before the reported column.
/// </summary>
internal sealed class RefArgumentFix : IBuildErrorFix
{
    static readonly Regex BadRef = new(
        @"([^\s:]+\.cs)\((\d+),(\d+)\): (?:error CS1525: Invalid expression term 'ref'|error CS1633: Argument \d+ may not be passed as a 'ref' argument)");
    static readonly Regex RefKeyword = new(@"\bref\b");

    public int Apply(Target target, string buildLog)
    {
        int fixedCount = 0;
        // the build log may report one error more than once (inline + summary): a repeat would find the
        // fixed line and remove the next, legitimate 'ref' before the stale column
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match error in BadRef.Matches(buildLog))
        {
            string? file = Locate(target, error.Groups[1].Value);
            if (file == null || !seen.Add($"{Path.GetFullPath(file)}({error.Groups[2].Value},{error.Groups[3].Value})")) continue;
            string[] lines = File.ReadAllLines(file);
            int line = int.Parse(error.Groups[2].Value) - 1;
            int column = int.Parse(error.Groups[3].Value) - 1;
            if (line < 0 || line >= lines.Length) continue;

            var keyword = RefKeyword.Matches(lines[line]).LastOrDefault(m => m.Index <= column);
            if (keyword == null) continue;
            string rest = lines[line][(keyword.Index + 3)..];
            lines[line] = lines[line][..keyword.Index] + (rest.StartsWith(' ') ? rest[1..] : rest);
            File.WriteAllLines(file, lines);
            fixedCount++;
            Log.Info($"    fixed ref-arg: {file} :{error.Groups[2].Value}");
        }
        return fixedCount;
    }

    // the compiler's path, else the source file of that name
    static string? Locate(Target target, string reported) =>
        File.Exists(reported) ? reported : SourceTree.CsFiles(target.SrcDir, Path.GetFileName(reported)).FirstOrDefault();
}

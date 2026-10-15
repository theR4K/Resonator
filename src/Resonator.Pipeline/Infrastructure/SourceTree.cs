using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Resonator.Pipeline.Infrastructure;

/// <summary>
/// The C# source tree of a project dir: every *.cs below it, minus build output. Build output =
/// any path segment (relative to the root) named bin or obj - tested per segment, so folders like
/// "Bindings" or an output dir under "C:\robin" are kept.
/// </summary>
public static class SourceTree
{
    static readonly char[] Separators = ['/', '\\'];

    public static IEnumerable<string> CsFiles(string root, string pattern = "*.cs") =>
        Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories).Where(f => !IsBuildOutput(root, f));

    public static bool IsBuildOutput(string root, string path) =>
        RelativeSegments(root, path).Any(s => s.Equals("bin", StringComparison.OrdinalIgnoreCase) || s.Equals("obj", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The files a regeneration owns: every source file except those under a keep dir (hand-written
    /// code next to the generated tree, e.g. Injected/). Full paths.
    /// </summary>
    public static HashSet<string> GeneratedFiles(string root, IEnumerable<string> keepDirs)
    {
        var generated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(root)) return generated;
        var keep = keepDirs.Select(d => d.TrimEnd(Separators)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string file in CsFiles(root))
            if (!keep.Contains(RelativeSegments(root, file)[0]))
                generated.Add(Path.GetFullPath(file));
        return generated;
    }

    /// <summary>Delete the files of a previous run that this run did not produce. Returns how many.</summary>
    public static int DeleteStale(IEnumerable<string> previous, IEnumerable<string> current)
    {
        var keep = current.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int deleted = 0;
        foreach (string file in previous.Where(f => !keep.Contains(Path.GetFullPath(f))))
        {
            try { File.Delete(file); deleted++; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return deleted;
    }

    static string[] RelativeSegments(string root, string path) => Path.GetRelativePath(root, path).Split(Separators);
}

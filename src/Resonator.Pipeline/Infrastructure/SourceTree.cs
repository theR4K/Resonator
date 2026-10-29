using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Resonator.Pipeline.Infrastructure;

/// <summary>
/// The source tree of a project dir: the files below it that match the target's file globs (every *.cs for a C# target),
/// minus build output and git metadata. Build output = any path segment (relative to the root) named bin or obj - tested
/// per segment, so folders like "Bindings" or an output dir under "C:\robin" are kept.
/// </summary>
public static class SourceTree
{
    static readonly char[] Separators = ['/', '\\'];

    public static IEnumerable<string> CsFiles(string root, string pattern = "*.cs") =>
        Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories).Where(f => !IsBuildOutput(root, f));

    /// <summary>
    /// The files below <paramref name="root"/> whose path relative to it matches one of the globs: "**" any number of
    /// folders, "*" and "?" within one path segment, '/' as the separator, case-insensitive.
    /// </summary>
    public static IEnumerable<string> Files(string root, IReadOnlyList<string> globs)
    {
        if (globs.Count == 1 && globs[0] == "**/*.cs") return CsFiles(root);   // the common case, as before
        var match = Glob(globs);
        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(f => !IsBuildOutput(root, f) && !IsGitMetadata(root, f))
            .Where(f => !Path.GetFileName(f).Equals(OutputMirror.ManifestName, StringComparison.OrdinalIgnoreCase))   // the mirror's own list
            .Where(f => match.IsMatch(Path.GetRelativePath(root, f).Replace('\\', '/')));
    }

    /// <summary>One regex for a list of globs (see <see cref="Files"/>).</summary>
    public static Regex Glob(IEnumerable<string> globs)
    {
        static string One(string glob)
        {
            var rx = new StringBuilder();
            string g = glob.Replace('\\', '/').TrimStart('/');
            for (int i = 0; i < g.Length; i++)
            {
                if (g[i] == '*' && i + 1 < g.Length && g[i + 1] == '*')
                {
                    bool folder = i + 2 < g.Length && g[i + 2] == '/';
                    rx.Append(folder ? "(?:.*/)?" : ".*");
                    i += folder ? 2 : 1;
                }
                else if (g[i] == '*') rx.Append("[^/]*");
                else if (g[i] == '?') rx.Append("[^/]");
                else rx.Append(Regex.Escape(g[i].ToString()));
            }
            return rx.ToString();
        }
        return new Regex("^(?:" + string.Join("|", globs.Select(One)) + ")$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    public static bool IsBuildOutput(string root, string path) =>
        RelativeSegments(root, path).Any(s => s.Equals("bin", StringComparison.OrdinalIgnoreCase) || s.Equals("obj", StringComparison.OrdinalIgnoreCase));

    /// <summary>A repo's or submodule's .git folder or file (a checkout's metadata, never source).</summary>
    static bool IsGitMetadata(string root, string path) =>
        RelativeSegments(root, path).Any(s => s.Equals(".git", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The files a regeneration owns: every source file (of <paramref name="globs"/>, default every .cs) except those under
    /// a keep dir (hand-written code next to the generated tree, e.g. Injected/). Full paths.
    /// </summary>
    public static HashSet<string> GeneratedFiles(string root, IEnumerable<string> keepDirs, IReadOnlyList<string>? globs = null)
    {
        var generated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(root)) return generated;
        var keep = keepDirs.Select(d => d.TrimEnd(Separators)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string file in globs == null ? CsFiles(root) : Files(root, globs))
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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Resonator.Pipeline.Patching;

/// <summary>
/// A unified diff of one file, as the target sets store them:
///   content patch   --- a/&lt;path&gt;  +++ b/&lt;path&gt;  then @@ hunks
///   deletion patch  --- a/&lt;path&gt;  +++ /dev/null   (the file is removed)
/// Paths are relative to the source root (as with -p1).
/// </summary>
public sealed class Patch
{
    /// <summary>From "--- a/&lt;path&gt;"; null = /dev/null.</summary>
    public string? OldPath { get; private set; }
    /// <summary>From "+++ b/&lt;path&gt;"; null = /dev/null.</summary>
    public string? NewPath { get; private set; }
    public List<Hunk> Hunks { get; } = new();

    public bool IsDeletion => NewPath == null;
    /// <summary>The file the patch applies to.</summary>
    public string FilePath => OldPath ?? NewPath!;

    public sealed class Hunk
    {
        public int OldStart;   // 1-based
        public int NewStart;
        /// <summary>Context and '-' lines: the text before.</summary>
        public List<string> OldLines { get; } = new();
        /// <summary>Context and '+' lines: the text after.</summary>
        public List<string> NewLines { get; } = new();

        /// <summary>Only removes lines: every line after is a line before.</summary>
        public bool IsDeletion => NewLines.Count <= OldLines.Count && NewLines.All(l => OldLines.Contains(l, StringComparer.Ordinal));
    }

    static readonly Regex HunkHeader = new(@"^@@ -(\d+)(?:,\d+)? (?:\+(\d+)(?:,\d+)? )?@@");
    static readonly string[] GitHeaders = ["diff --git ", "deleted file mode ", "new file mode ", "index ", "old mode", "new mode", "rename "];

    public static Patch Parse(string text)
    {
        var patch = new Patch();
        Hunk? hunk = null;
        foreach (string line in TextLines.Split(text))
        {
            if (GitHeaders.Any(line.StartsWith)) continue;
            if (line.StartsWith("--- "))
            {
                patch.OldPath = PathOf(line, "a/");
                hunk = null;
            }
            else if (line.StartsWith("+++ ")) patch.NewPath = PathOf(line, "b/");
            else if (line.StartsWith("@@")) patch.Hunks.Add(hunk = ParseHunkHeader(line));
            else if (hunk != null) AddLine(hunk, line);   // anything before the first hunk is noise
        }
        if (patch.OldPath == null && patch.NewPath == null) throw new FormatException("not a unified diff (no --- / +++ headers)");
        return patch;
    }

    static string? PathOf(string header, string prefix)
    {
        string path = header[4..].Trim();
        return path == "/dev/null" ? null : path.StartsWith(prefix) ? path[prefix.Length..] : path;
    }

    // @@ -oldStart,oldCount +newStart,newCount @@ - the + side is absent for a pure deletion: @@ -1,165 @@
    static Hunk ParseHunkHeader(string line)
    {
        var m = HunkHeader.Match(line);
        if (!m.Success) throw new FormatException("bad hunk header: " + line);
        return new Hunk
        {
            OldStart = int.Parse(m.Groups[1].Value),
            NewStart = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 0,
        };
    }

    static void AddLine(Hunk hunk, string line)
    {
        char tag = line.Length > 0 ? line[0] : ' ';
        string body = line.Length > 1 ? line[1..] : "";
        switch (tag)
        {
            case '-': hunk.OldLines.Add(body); break;
            case '+': hunk.NewLines.Add(body); break;
            case ' ': hunk.OldLines.Add(body); hunk.NewLines.Add(body); break;   // context belongs to both
            // '\': "\ No newline at end of file"
        }
    }
}

/// <summary>Line handling shared by applying and generating patches: LF / CRLF-insensitive.</summary>
public static class TextLines
{
    public static List<string> Split(string text)
    {
        var lines = text.Split('\n');
        if (text.EndsWith('\n')) lines = lines[..^1];   // the final newline ends a line, it does not start one
        return lines.Select(l => l.EndsWith('\r') ? l[..^1] : l).ToList();
    }

    /// <summary>CRLF if the text uses it anywhere, else LF.</summary>
    public static string DetectEol(string text) => text.Contains("\r\n") ? "\r\n" : "\n";
}

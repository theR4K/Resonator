using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Resonator.Pipeline.Infrastructure;

/// <summary>
/// Brings a project dir up to date with a freshly generated copy of it, touching only what changed: a file whose bytes are
/// the same keeps its timestamp, so the consumer's build (MSBuild, CMake / Ninja) recompiles only what a patch or a new
/// source revision actually changed - a regeneration used to rewrite every file, and every file then looked new.
///
///   new or different file   written
///   same bytes              left alone
///   generated before, gone  deleted - "generated before" is the manifest the previous mirror wrote
///                           (<see cref="ManifestName"/>); a dir without one falls back to <paramref name="fallbackOwned"/>
///
/// Nothing else in the dir is touched: keep dirs (Injected/), a consumer's csproj, bin/ and obj/ are not in the manifest.
/// </summary>
public static class OutputMirror
{
    public const string ManifestName = ".resonator-generated";

    public sealed record Result(int Written, int Unchanged, int Deleted);

    /// <param name="fallbackOwned">The files a regeneration owned before there was a manifest (full paths).</param>
    public static Result Run(string staged, string project, IEnumerable<string> fallbackOwned)
    {
        staged = Path.GetFullPath(staged);
        project = Path.GetFullPath(project);
        Directory.CreateDirectory(project);
        string manifest = Path.Combine(project, ManifestName);
        var owned = File.Exists(manifest)
            ? File.ReadAllLines(manifest).Where(l => l.Length > 0).Select(l => Path.GetFullPath(Path.Combine(project, l))).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : fallbackOwned.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var current = new List<string>();
        int written = 0, unchanged = 0;
        foreach (string file in Directory.EnumerateFiles(staged, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(staged, file);
            string dest = Path.Combine(project, rel);
            current.Add(rel.Replace('\\', '/'));
            owned.Remove(Path.GetFullPath(dest));
            if (Same(file, dest))
            {
                unchanged++;
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            if (File.Exists(dest)) File.SetAttributes(dest, FileAttributes.Normal);   // a read-only file from a git checkout
            File.Copy(file, dest, overwrite: true);
            written++;
        }

        int deleted = 0;
        var emptied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in owned.Where(File.Exists))
        {
            try
            {
                File.SetAttributes(file, FileAttributes.Normal);
                File.Delete(file);
                deleted++;
                emptied.Add(Path.GetDirectoryName(file)!);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        DeleteEmptied(project, emptied);

        current.Sort(StringComparer.Ordinal);
        string list = string.Join("\n", current) + "\n";
        if (!File.Exists(manifest) || File.ReadAllText(manifest) != list) File.WriteAllText(manifest, list);
        return new Result(written, unchanged, deleted);
    }

    static bool Same(string a, string b)
    {
        var fb = new FileInfo(b);
        if (!fb.Exists || fb.Length != new FileInfo(a).Length) return false;
        const int Chunk = 1 << 16;
        var ba = new byte[Chunk];
        var bb = new byte[Chunk];
        using var sa = File.OpenRead(a);
        using var sb = File.OpenRead(b);
        while (true)
        {
            int na = sa.ReadAtLeast(ba, Chunk, throwOnEndOfStream: false);
            int nb = sb.ReadAtLeast(bb, Chunk, throwOnEndOfStream: false);
            if (na != nb || !ba.AsSpan(0, na).SequenceEqual(bb.AsSpan(0, nb))) return false;
            if (na < Chunk) return true;
        }
    }

    /// <summary>The folders of deleted files, and their parents, that are empty now; the project dir itself stays.</summary>
    static void DeleteEmptied(string root, IEnumerable<string> dirs)
    {
        foreach (string start in dirs.OrderByDescending(d => d.Length))
        {
            for (string? dir = start; dir != null && dir.Length > root.Length && dir.StartsWith(root, StringComparison.OrdinalIgnoreCase);
                 dir = Path.GetDirectoryName(dir))
            {
                try
                {
                    if (!Directory.Exists(dir) || Directory.EnumerateFileSystemEntries(dir).Any()) break;
                    Directory.Delete(dir);
                }
                catch (IOException) { break; }
                catch (UnauthorizedAccessException) { break; }
            }
        }
    }
}

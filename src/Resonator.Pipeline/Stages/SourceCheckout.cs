using System;
using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Resonator.Pipeline.Infrastructure;
using Resonator.Pipeline.Targets;

namespace Resonator.Pipeline.Stages;

/// <summary>
/// A source target: the library's official source instead of a decompile of the shipped dll.
///   fetch     the spec's git repo at a pinned ref (shallow), or an archive (.zip / .tar.gz),
///             cached under &lt;workdir&gt;/sources/&lt;target&gt;-&lt;hash of url+ref&gt;/
///   checkout  every .cs under the spec's "dir" (minus "exclude") into the source root, relative
///             paths kept; keep dirs survive, stale files are dropped
/// Fixup then applies the layered patches as for a decompiled target, and sync diffs against a
/// fresh checkout - a source target is patched and synced like any other.
/// </summary>
internal static class SourceCheckout
{
    public static void Run(Target target)
    {
        var source = target.Spec.Source!;
        string root = Fetch(target, source);
        string from = Path.GetFullPath(Path.Combine(root, source.Dir.Replace('/', Path.DirectorySeparatorChar)));
        if (!Directory.Exists(from)) throw new Exception($"source dir '{source.Dir}' not found in {root}");

        var previous = SourceTree.GeneratedFiles(target.SrcDir, target.Spec.KeepDirs);
        Directory.CreateDirectory(target.SrcDir);

        var exclude = source.Exclude.Select(e => e.Replace('\\', '/').TrimStart('/')).ToArray();
        bool Excluded(string rel) => exclude.Any(e => e.EndsWith('/')
            ? rel.StartsWith(e, StringComparison.OrdinalIgnoreCase)
            : rel.Equals(e, StringComparison.OrdinalIgnoreCase));

        var copied = new List<string>();
        int skipped = 0;
        foreach (string file in SourceTree.CsFiles(from))
        {
            string rel = Path.GetRelativePath(from, file).Replace('\\', '/');
            if (Excluded(rel)) { skipped++; continue; }
            string dest = Path.Combine(target.SrcDir, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
            copied.Add(dest);
        }
        int stale = SourceTree.DeleteStale(previous, copied);

        Log.Info($"=== SOURCE {source}: {copied.Count} file(s) from {source.Dir}"
            + (skipped > 0 ? $", {skipped} excluded" : "")
            + (stale > 0 ? $", dropped {stale} stale" : "")
            + " -> " + target.SrcDir);
    }

    /// <summary>The fetched tree, from the cache when it is complete.</summary>
    static string Fetch(Target target, SourceSpec source)
    {
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes((source.Git ?? source.Archive) + "|" + source.Ref)))[..12].ToLowerInvariant();
        string dir = Path.Combine(target.Settings.WorkDir, "sources", target.Name + "-" + key);
        string complete = dir + ".done";
        if (Directory.Exists(dir) && File.Exists(complete)) return dir;
        if (Directory.Exists(dir)) DeleteCheckout(dir);
        Directory.CreateDirectory(dir);

        if (source.Git != null) FetchGit(dir, source.Git, source.Ref);
        else FetchArchive(dir, source.Archive!);
        File.WriteAllText(complete, source.ToString());
        return dir;
    }

    /// <summary>
    /// An incomplete fetch, deleted before the next one. Git leaves its object and pack files read-only, and on Windows
    /// Directory.Delete refuses those ("Access to the path '...' is denied"), so the attribute is cleared first.
    /// </summary>
    static void DeleteCheckout(string dir)
    {
        foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(dir, recursive: true);
    }

    /// <summary>A shallow fetch of exactly the pinned ref (a tag, branch or full commit hash).</summary>
    static void FetchGit(string dir, string url, string gitRef)
    {
        Git(dir, "init", "-q");
        Git(dir, "remote", "add", "origin", url);
        Git(dir, "fetch", "-q", "--depth", "1", "origin", gitRef);
        Git(dir, "-c", "advice.detachedHead=false", "checkout", "-q", "FETCH_HEAD");
    }

    static void Git(string dir, params string[] args)
    {
        var (exitCode, output) = Shell.Capture("git", ["-C", dir, .. args]);
        if (exitCode != 0) throw new Exception($"git {string.Join(' ', args)} failed:\n{output}");
    }

    static void FetchArchive(string dir, string url)
    {
        string file = dir + Path.GetExtension(new Uri(url).AbsolutePath);
        using (var http = new HttpClient())
        using (var response = http.GetAsync(url).GetAwaiter().GetResult())
        {
            response.EnsureSuccessStatusCode();
            using var output = File.Create(file);
            response.Content.CopyToAsync(output).GetAwaiter().GetResult();
        }
        if (file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            ZipFile.ExtractToDirectory(file, dir, overwriteFiles: true);
        else
        {
            using var gzip = new GZipStream(File.OpenRead(file), CompressionMode.Decompress);
            TarFile.ExtractToDirectory(gzip, dir, overwriteFiles: true);
        }
        File.Delete(file);
    }
}

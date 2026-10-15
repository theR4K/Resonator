using System;
using System.IO;
using System.Linq;
using System.Runtime.Loader;

namespace Resonator.Weaver;

/// <summary>
/// The weaving side of the pair: runs the real FrooxEngine.Weaver (shipped or rebuilt) over
/// assemblies - for round-trip tests of the deweaver.
/// </summary>
public static class Reweaver
{
    /// <summary>
    /// Weave <paramref name="assemblies"/> (relative to <paramref name="dir"/> or absolute) with the
    /// weaver at <paramref name="weaverPath"/>. Returns 0 on success, 1 if any Process() call failed.
    /// </summary>
    public static int Reweave(string dir, string[] assemblies, string weaverPath)
    {
        dir = Path.GetFullPath(dir);
        if (!Directory.Exists(dir)) throw new DirectoryNotFoundException("no such dir: " + dir);
        if (!File.Exists(weaverPath)) throw new FileNotFoundException("weaver dll not found", weaverPath);

        // safety: the weaver walks UP the directories looking for ReleaseNotes.txt (a game install)
        for (string? d = dir; d != null; d = Path.GetDirectoryName(d))
        {
            string releaseNotes = Path.Combine(d, "ReleaseNotes.txt");
            if (File.Exists(releaseNotes)) throw new Exception("ABORT: ReleaseNotes.txt found at " + releaseNotes);
        }

        // FrooxEngine.dll first: the others resolve their Worker types from it
        var files = assemblies
            .Select(a => Path.IsPathRooted(a) ? a : Path.Combine(dir, a))
            .OrderBy(f => Path.GetFileName(f) == "FrooxEngine.dll" ? 0 : 1)
            .ToList();

        // A dedicated load context: the default one resolves by identity and would hand back the
        // app-base (shipped) copy even when a different weaver file is requested.
        var weaver = new AssemblyLoadContext("Resonator.WeaverHost").LoadFromAssemblyPath(Path.GetFullPath(weaverPath));
        Console.Error.WriteLine($"[weaver] loaded {weaver.Location} ({weaver.FullName})");
        var process = weaver.GetType("FrooxEngine.Weaver.AssemblyPostProcessor", throwOnError: true)!
            .GetMethods().First(m => m.Name == "Process" && m.GetParameters().Length == 3);

        int exitCode = 0;
        foreach (string file in files)
        {
            Console.WriteLine("=== weaving " + Path.GetFileName(file));
            object?[] args = [file, null, dir];
            try
            {
                object? result = process.Invoke(null, args);
                Console.WriteLine("=== Process returned: " + result + "  version=" + args[1]);
            }
            catch (Exception ex)
            {
                Console.WriteLine("=== Process FAILED: " + (ex.InnerException ?? ex).Message);
                exitCode = 1;
            }
        }
        return exitCode;
    }
}

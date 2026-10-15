using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Resonator.Weaver.Cecil;

namespace Resonator.ProjectBuilder.PostBuild;

/// <summary>
/// Remove the default .ctor(s) from named types so a recompiled DLL matches the original, where a
/// weaver stripped the constructor (e.g. Fody marker types). Run by the build stage for the types
/// in the spec key <c>fix_ctor_types</c>.
/// </summary>
public static class FixCtor
{
    /// <summary>Strip every .ctor of the types named <paramref name="typeNames"/> (simple names, nested included). Returns the number removed.</summary>
    public static int Run(string dllPath, IEnumerable<string> typeNames, TextWriter log)
    {
        string path = Path.GetFullPath(dllPath);
        var names = typeNames.ToHashSet(StringComparer.Ordinal);
        var assembly = AssemblyFile.Read(path);

        int removed = 0;
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in assembly.MainModule.AllTypes().Where(t => names.Contains(t.Name)))
        {
            found.Add(type.Name);
            var ctors = type.Methods.Where(m => m.Name == ".ctor").ToList();
            foreach (var c in ctors) type.Methods.Remove(c);
            removed += ctors.Count;
            log.WriteLine($"{type.FullName}: removed {ctors.Count} ctor(s)");
        }

        var missing = names.Except(found).ToList();
        if (missing.Count > 0) log.WriteLine("NOTE: type(s) not found (skipped): " + string.Join(", ", missing));
        if (removed == 0)
        {
            log.WriteLine("nothing to fix - DLL not modified");
            assembly.Dispose();
            return 0;
        }

        AssemblyFile.ReplaceInPlace(assembly, path);
        // The compiler-generated PDB still references the removed ctor, so it no longer matches the
        // DLL. A matching one cannot be regenerated here: delete it rather than ship a broken pair.
        string stalePdb = Path.ChangeExtension(path, ".pdb");
        if (File.Exists(stalePdb))
        {
            File.Delete(stalePdb);
            log.WriteLine($"deleted stale PDB (no longer matches): {stalePdb}");
        }
        log.WriteLine($"WROTE {path} ({removed} ctor(s) removed)");
        return removed;
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Resonator.Weaver.Cecil;

namespace Resonator.Weaver.Checks;

/// <summary>
/// After a deweave: is anything of the weaver left? Clean = no FROOXENGINE_WEAVED marker, no
/// weaver-added [SyncMethod(Delegate, null)], and no sync field reverted to private that other
/// types still access (that would not compile). Also prints the version and the sync-field
/// visibility distribution for review.
/// </summary>
public static class ResidueCheck
{
    /// <summary>Returns 0 when clean, 1 otherwise.</summary>
    public static int Run(string dllPath, TextWriter log)
    {
        string path = Path.GetFullPath(dllPath);
        AssemblyDefinition assembly;
        try { assembly = AssemblyFile.Read(path, withSymbols: true); }
        catch (Exception ex) { log.WriteLine("INVALID: could not load: " + ex.Message); return 1; }

        using (assembly)
        {
            var module = assembly.MainModule;
            var types = module.AllTypes().ToList();
            bool weaved = WeaverSpec.IsWeaved(module);

            var syncMethods = types.SelectMany(t => t.Methods).SelectMany(m => m.CustomAttributes).Where(WeaverSpec.IsSyncMethodAttribute).ToList();
            int weaverSyncMethods = syncMethods.Count(WeaverSpec.IsWeaverSyncMethodAttribute);

            // readonly sync fields by access level (0x1 private, 0x4 protected, ...)
            var syncFields = types.SelectMany(t => t.Fields).Where(IsReadonlySyncField).ToList();
            var accessCounts = syncFields
                .GroupBy(f => $"0x{(ushort)(f.Attributes & FieldAttributes.FieldAccessMask):X}")
                .OrderBy(g => g.Key)
                .Select(g => $"{g.Key}={g.Count()}");
            var external = WeaverSpec.ComputeExternallyAccessed(module);
            bool IsExternal(FieldDefinition f) => external.Contains(WeaverSpec.FieldKey(f.DeclaringType.FullName, f.Name));
            var protectedFields = syncFields.Where(f => (f.Attributes & FieldAttributes.FieldAccessMask) == FieldAttributes.Family).ToList();
            // The dangerous direction is a reverted field that other types access (does not compile).
            // Keeping an originally-private field protected compiles fine; metadata cannot tell the two
            // apart, so the deweaver prefers the safe side.
            var brokenPrivate = syncFields
                .Where(f => (f.Attributes & FieldAttributes.FieldAccessMask) == FieldAttributes.Private && IsExternal(f))
                .Select(f => WeaverSpec.FieldKey(f.DeclaringType.FullName, f.Name))
                .ToList();

            string fileVersion = assembly.CustomAttributes
                .LastOrDefault(ca => ca.AttributeType.Name.EndsWith("AssemblyFileVersionAttribute") && ca.ConstructorArguments.Count > 0)
                ?.ConstructorArguments[0].Value?.ToString() ?? "?";

            log.WriteLine($"file            : {path}");
            log.WriteLine($"loads (valid)   : YES");
            log.WriteLine($"FROOXENGINE_WEAVED present : {weaved}   (want: False)");
            log.WriteLine($"SyncMethod(Delegate,null)  : {weaverSyncMethods}   (want: 0)");
            log.WriteLine($"SyncMethod(original)       : {syncMethods.Count - weaverSyncMethods}   (preserved, if any existed)");
            log.WriteLine($"AssemblyFileVersion        : {fileVersion}   (want: not a y.M.d.hhmm timestamp)");
            log.WriteLine($"Name.Version               : {assembly.Name.Version}");
            log.WriteLine($"sync field access dist   : {string.Join(", ", accessCounts)}" +
                $"   (protected=0x4: {protectedFields.Count(IsExternal)}/{protectedFields.Count} cross-type accessed; the rest may be original-protected, kept safe)");
            if (brokenPrivate.Count > 0)
            {
                log.WriteLine("BROKEN (externally accessed but private):");
                foreach (string f in brokenPrivate.Take(10)) log.WriteLine("  " + f);
            }

            bool clean = !weaved && weaverSyncMethods == 0 && brokenPrivate.Count == 0;
            log.WriteLine(clean ? "RESULT: CLEAN" : "RESULT: NOT CLEAN");
            return clean ? 0 : 1;
        }
    }

    // a readonly field whose concrete (non-interface) type implements ISyncMember
    static bool IsReadonlySyncField(FieldDefinition f)
    {
        if (f.Name.Contains("__BackingField") || !f.IsInitOnly) return false;
        try
        {
            return f.FieldType.Resolve() is { IsInterface: false } type
                && type.EnumerateAllInterfaces().Any(i => i.FullName == WeaverSpec.SyncMemberInterface);
        }
        catch { return false; }
    }
}

using System;
using System.IO;
using Mono.Cecil;
using Resonator.Weaver.Cecil;
using Resonator.Weaver.Deweaving.Operations;

namespace Resonator.Weaver.Deweaving;

/// <summary>
/// Reverts what FrooxEngine.Weaver.dll (a Fody weaver) did to an assembly, restoring the
/// pre-weave original. Orchestration only: read -> detect -> run the inverse operations ->
/// write. A PDB next to the input is carried over (rewritten over the new IL), so a later
/// decompile still gets real local names.
/// </summary>
public static class Deweaver
{
    /// <summary>One inverse per weaver modification. None depends on another's output.</summary>
    static readonly IRevertOperation[] Operations =
    [
        new RemoveProcessedAttribute(),
        new RemoveWorkerMethods(),
        new RevertFieldVisibility(),
        new RemoveSyncMethodAttributes(),
        new BlankCoderAotDummy(),
        new ResetVersion(),
    ];

    public static DeweaveReport Deweave(string path, DeweaveOptions options)
    {
        var report = new DeweaveReport { Path = path };
        path = Path.GetFullPath(path);
        bool hasPdb = AssemblyFile.HasPdb(path);

        AssemblyDefinition assembly;
        try
        {
            assembly = AssemblyFile.Read(path, withSymbols: true, readWrite: true);
        }
        catch (Exception ex)
        {
            if (ex.Message.Contains("Format of the executable")) report.NotDotNet = true;
            else if (ex.Message.Contains("being used by another process")) report.Locked = true;
            else report.Error = "read: " + ex.Message;
            return report;
        }

        string writeError;
        using (assembly)
        {
            report.WasWeaved = WeaverSpec.IsWeaved(assembly.MainModule);
            if (!report.WasWeaved) return report;

            var context = new RevertContext(assembly, options, report);
            foreach (var operation in Operations) operation.Apply(context);
            if (options.DryRun || !report.Modified) return report;

            try
            {
                Write(assembly, path, options, hasPdb);
                return report;
            }
            catch (Exception ex) { writeError = ex.Message; }
        }

        if (options.MethodMode == MethodMode.Empty)
        {
            report.Error = "write: " + writeError;
            return report;
        }
        // Removing the Worker methods can leave dangling cross-module refs Cecil cannot patch:
        // retry from a fresh read, emptying their bodies instead.
        var retry = Deweave(path, options with { MethodMode = MethodMode.Empty });
        if (retry.Error == null) retry.UsedEmptyFallback = true;
        else retry.Error = "Remove failed: " + writeError + " | Empty also failed: " + retry.Error;
        return retry;
    }

    static void Write(AssemblyDefinition assembly, string path, DeweaveOptions options, bool hasPdb)
    {
        string outPath = path;
        if (options.OutputDir != null)
        {
            Directory.CreateDirectory(options.OutputDir);
            outPath = Path.Combine(options.OutputDir, Path.GetFileName(path));
        }
        if (options.KeepBackup && outPath == path)
            File.Copy(path, path + ".weaved.bak", overwrite: true);
        AssemblyFile.Write(assembly, outPath, withSymbols: hasPdb);
    }
}

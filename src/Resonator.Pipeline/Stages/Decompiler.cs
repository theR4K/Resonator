using System;
using System.IO;
using System.Linq;
using System.Text;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.ProjectDecompiler;
using ICSharpCode.Decompiler.DebugInfo;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.TypeSystem;
using ICSharpCode.ILSpyX.PdbProvider;
using Resonator.Pipeline.Infrastructure;
using Resonator.Pipeline.Targets;

namespace Resonator.Pipeline.Stages;

/// <summary>
/// ICSharpCode.Decompiler (the ILSpy engine), in process. References resolve from the Resonite
/// install; the PDB next to the source dll is attached when it matches (real local names).
/// </summary>
internal static class Decompiler
{
    /// <summary>The whole assembly as a project tree: one file per type, namespace folders.</summary>
    public static void WholeProject(Target target)
    {
        Log.Banner("DECOMPILE (in-process ICSharpCode.Decompiler, refs from Resonite folder)");
        var (pe, resolver, pdb) = Open(target);
        var settings = new DecompilerSettings(LanguageVersion.Latest) { ThrowOnAssemblyResolveErrors = false, UseSdkStyleProjectFormat = true };
        var decompiler = new WholeProjectDecompiler(settings, resolver, projectWriter: null, assemblyReferenceClassifier: resolver, debugInfoProvider: pdb);

        // the decompiler also writes a csproj: discarded - the reference set comes from the assembly
        // itself (references.targets) and the csproj from the template
        using var discardedCsproj = new StreamWriter(new MemoryStream(), Encoding.UTF8);
        decompiler.DecompileProject(pe, target.SrcDir, discardedCsproj);
        NestDottedFolders(target.SrcDir);

        if (decompiler.Errors.Count > 0)
            Log.Warn($"  {decompiler.Errors.Count} decompilation error(s):\n" + string.Join("\n", decompiler.Errors.Take(10).Select(e => "    " + e.Message)));
        Log.Info("decompiled to " + target.SrcDir);
    }

    /// <summary>
    /// Only the spec's select_types, one file each, in the same namespace-folder layout as the
    /// whole-project decompile (so patches written for one apply to the other). This is how a
    /// split-out assembly (e.g. FrooxEngine.Ext.Discord) gets its source from the shared shipped dll.
    /// </summary>
    public static void Types(Target target)
    {
        var types = target.Spec.SelectTypes;
        Log.Banner($"DECOMPILE (type subset: {types.Length} type(s))");
        var (_, resolver, pdb) = Open(target);
        var settings = new DecompilerSettings(LanguageVersion.Latest) { ThrowOnAssemblyResolveErrors = false };
        var decompiler = new CSharpDecompiler(target.SourceDll, resolver, settings) { DebugInfoProvider = pdb };

        foreach (string type in types)
        {
            string code;
            try { code = decompiler.DecompileTypeAsString(new FullTypeName(type)); }
            catch (Exception ex) { throw new Exception($"decompiling type '{type}' failed: {ex.Message}"); }
            string file = Path.Combine(target.SrcDir, type.Replace('.', Path.DirectorySeparatorChar) + ".cs");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, code);
            Log.Info($"  type: {type} -> {Path.GetRelativePath(target.SrcDir, file)}");
        }
        Log.Info($"decompiled {types.Length} type(s) to {target.SrcDir}");
    }

    static (PEFile Pe, UniversalAssemblyResolver Resolver, IDebugInfoProvider? Pdb) Open(Target target)
    {
        var pe = new PEFile(target.SourceDll);
        var resolver = new UniversalAssemblyResolver(target.SourceDll, throwOnError: false, targetFramework: pe.DetectTargetFrameworkId());
        resolver.AddSearchDirectory(target.Settings.ResoniteDir);
        return (pe, resolver, target.Settings.UsePdb ? TryLoadPdb(target.SourceDll) : null);
    }

    /// <summary>
    /// The PDB next to the source dll. A deweaved dll's PDB was rewritten over the new IL, so it
    /// matches. None, or a mismatch: warn and decompile with generated local names.
    /// </summary>
    static IDebugInfoProvider? TryLoadPdb(string dll)
    {
        string pdb = Path.ChangeExtension(dll, ".pdb");
        if (!File.Exists(pdb))
        {
            Log.Warn("  pdb: none next to the source dll; decompiling without PDB (fallback local names)");
            return null;
        }
        try
        {
            var provider = DebugInfoUtils.FromFile(new PEFile(dll), pdb) ?? throw new InvalidDataException("no debug info provider for " + pdb);
            Log.Info("  PDB attached: " + provider.Description);
            return provider;
        }
        catch (Exception ex)
        {
            Log.Warn($"  pdb: does not match the source IL ({ex.Message})\n      decompiling WITHOUT PDB (fallback local names)");
            return null;
        }
    }

    /// <summary>
    /// The decompiler can write a namespace as one dotted folder (FrooxEngine.Weaver/); patch paths
    /// expect nested folders (FrooxEngine/Weaver/). Stale trees at the nested path are replaced.
    /// Parents move before their children (ordinal order): enumeration order is the filesystem's, and
    /// on Linux FrooxEngine.ProtoFlux.CoreNodes/ can come first - moving FrooxEngine.ProtoFlux/ after
    /// it would then delete the freshly nested FrooxEngine/ProtoFlux/CoreNodes/ as "stale".
    /// </summary>
    static void NestDottedFolders(string srcDir)
    {
        foreach (string dir in Directory.GetDirectories(srcDir).OrderBy(d => d, StringComparer.Ordinal))
        {
            string name = Path.GetFileName(dir);
            if (!name.Contains('.') || !Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories).Any()) continue;
            string nested = Path.Combine(srcDir, name.Replace('.', Path.DirectorySeparatorChar));
            if (Directory.Exists(nested)) Directory.Delete(nested, recursive: true);
            Directory.CreateDirectory(Path.GetDirectoryName(nested)!);
            Directory.Move(dir, nested);
            Log.Info($"  layout: {name}/ -> {name.Replace('.', '/')}/");
        }
    }
}

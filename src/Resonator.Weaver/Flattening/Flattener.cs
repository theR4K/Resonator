using System;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Resonator.Weaver.Cecil;
using Resonator.Weaver.Deweaving.Operations;

namespace Resonator.Weaver.Flattening;

/// <summary>
/// Prepares a WEAVED assembly for decompile + recompile WITHOUT reverting the weave (the Flatweave
/// target set). The weaver's output - InitializeSyncMembers / GetSyncMember / __New, SyncMethod
/// attributes, the FROOXENGINE_WEAVED marker, the version stamp - stays and becomes ordinary
/// source, so the rebuilt dll needs no reweave. Only what C# cannot express is changed:
///
///   [1] readonly sync-member fields lose InitOnly: the generated InitializeSyncMembers() stores
///       into them outside a ctor (CS0191). Runtime behaviour is identical (InitOnly is not
///       enforced on stfld), but the engine reads InitOnly by reflection (WorkerInitializer,
///       AssemblyTypeRegistry), so each cleared field gets [Resonator.FlattenedInitOnly] and the
///       Flatweave patches make those checks read "InitOnly or marked".
///   [2] the private -> protected flip is undone where the field type is less accessible than the
///       field (CS0052): such a field cannot have been protected in the original source.
///   [3] CoderAOT.Dummy() is emptied when the weaver rewrote it: the generated body has no C# form and
///       never runs under the JIT, and the original was empty. A weaver that does not rewrite it
///       (Resonite 2026.10.27 on) leaves the shipped body, which is source: kept.
/// </summary>
public static class Flattener
{
    /// <summary>Bump when the flattened output changes: part of the pipeline's prepare cache key.</summary>
    public const int Version = 4;

    /// <summary>
    /// Full name of the shared marker attribute, defined once in Flatweave.Adapter.
    /// </summary>
    public const string InitOnlyMarker = "Resonator.FlattenedInitOnlyAttribute";
    public const string AdapterAssembly = "Flatweave.Adapter";

    public static FlattenReport Flatten(string path, string outputDir, bool blankAotDummy = true)
    {
        path = Path.GetFullPath(path);
        var report = new FlattenReport { Path = path };
        bool hasPdb = AssemblyFile.HasPdb(path);

        using var assembly = AssemblyFile.Read(path, withSymbols: true);
        var module = assembly.MainModule;
        report.WasWeaved = WeaverSpec.IsWeaved(module);
        if (!report.WasWeaved) return report;

        var marker = module.ImportReference(typeof(Resonator.FlattenedInitOnlyAttribute).GetConstructor(Type.EmptyTypes)!);
        foreach (var field in WeaverSpec.CollectWorkers(module).SelectMany(w => w.Fields).Where(WeaverSpec.IsSyncMemberField))
        {
            if ((field.Attributes & FieldAttributes.FieldAccessMask) == FieldAttributes.Family && !IsPubliclyNameable(field.FieldType))
            {
                field.Attributes = (field.Attributes & ~FieldAttributes.FieldAccessMask) | FieldAttributes.Private;
                report.RevertedInaccessible++;
            }
            field.IsInitOnly = false;
            field.CustomAttributes.Add(new CustomAttribute(marker));
            report.ClearedInitOnly++;
        }

        report.BlankAotDummy = blankAotDummy && BlankCoderAotDummy.Blank(module);

        Directory.CreateDirectory(outputDir);
        try { AssemblyFile.Write(assembly, Path.Combine(outputDir, Path.GetFileName(path)), withSymbols: hasPdb); }
        catch (Exception ex) { report.Error = "write: " + ex.Message; }
        return report;
    }

    /// <summary>True if every type the reference names is visible outside its assembly (CS0052 for a protected member otherwise).</summary>
    static bool IsPubliclyNameable(TypeReference type)
    {
        switch (type)
        {
            case GenericParameter: return true;
            case GenericInstanceType gi: return IsPubliclyNameable(gi.ElementType) && gi.GenericArguments.All(IsPubliclyNameable);
            case TypeSpecification spec: return IsPubliclyNameable(spec.ElementType);
        }
        TypeDefinition? def;
        try { def = type.Resolve(); } catch { return true; }
        for (; def != null; def = def.DeclaringType)
            if (!(def.IsPublic || def.IsNestedPublic || def.IsNestedFamily || def.IsNestedFamilyOrAssembly)) return false;
        return true;
    }
}

public sealed class FlattenReport
{
    public string Path = "";
    public bool WasWeaved;
    public int ClearedInitOnly;
    public int RevertedInaccessible;
    public bool BlankAotDummy;
    public string? Error;

    public override string ToString() =>
        Error != null ? $"{Path}: ERROR {Error}"
        : !WasWeaved ? $"{Path}: not weaved, skipped"
        : $"{Path}, weaved=yes, -initonly={ClearedInitOnly}, protected->private(inaccessible type)={RevertedInaccessible}, aotDummyBlanked={BlankAotDummy}";
}

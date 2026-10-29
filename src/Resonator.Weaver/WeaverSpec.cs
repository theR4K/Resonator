using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Resonator.Weaver.Cecil;

namespace Resonator.Weaver;

/// <summary>
/// What FrooxEngine.Weaver.dll does to an assembly - the shared facts every deweave operation,
/// the flattener and the checks are built on. The predicates mirror the weaver's own checks
/// (verified against its decompiled source):
///
///   weaver modification                        inverse (Deweaving/Operations)
///   ------------------------------------------  ---------------------------
///   add [Description("FROOXENGINE_WEAVED")]     RemoveProcessedAttribute
///   add 3 methods to every Worker subclass      RemoveWorkerMethods
///   sync field private -> protected             RevertFieldVisibility
///   add [SyncMethod(typeof(Delegate), null)]    RemoveSyncMethodAttributes
///   rewrite the CoderAOT.Dummy body             BlankCoderAotDummy (weavers before Resonite 2026.10.27)
///   stamp a timestamp version                   ResetVersion
///
/// The weaver adds no fields.
/// </summary>
public static class WeaverSpec
{
    public const string ProcessedLabel = "FROOXENGINE_WEAVED";
    public const string WorkerType = "FrooxEngine.Worker";
    public const string SyncMemberInterface = "FrooxEngine.ISyncMember";

    // ---------------------------------------------------------------- processed marker

    public static bool IsProcessedAttribute(CustomAttribute ca) =>
        ca.AttributeType.Name.EndsWith("DescriptionAttribute") &&
        ca.ConstructorArguments.Count > 0 &&
        ca.ConstructorArguments[0].Value as string == ProcessedLabel;

    public static bool IsWeaved(ModuleDefinition module) => module.CustomAttributes.Any(IsProcessedAttribute);

    // ---------------------------------------------------------------- workers and their added methods

    /// <summary>
    /// Every Worker subclass of the module. The weaver's check is InheritsFrom(includeSelf: false):
    /// FrooxEngine.Worker itself is not a worker - its own InitializeSyncMembers / GetSyncMember
    /// come from source and must be preserved.
    /// </summary>
    public static List<TypeDefinition> CollectWorkers(ModuleDefinition module) =>
        module.AllTypes().Where(t => t.FullName != WorkerType && t.InheritsFrom(WorkerType)).ToList();

    public static bool IsInitializeSyncMembers(MethodDefinition m) =>
        m.Name == "InitializeSyncMembers" && !m.HasParameters && m.IsVirtual;

    public static bool IsGetSyncMember(MethodDefinition m) =>
        m.Name == "GetSyncMember" && m.IsVirtual && m.Parameters.Count == 1 && m.Parameters[0].ParameterType.FullName == "System.Int32";

    public static bool IsNewFactory(MethodDefinition m) =>
        m.Name == "__New" && m.IsStatic && !m.HasParameters;

    public static bool IsWeaverAddedMethod(MethodDefinition m) =>
        IsInitializeSyncMembers(m) || IsGetSyncMember(m) || IsNewFactory(m);

    // ---------------------------------------------------------------- SyncMethod attributes

    public static bool IsSyncMethodAttribute(CustomAttribute ca) => ca.AttributeType.Name.EndsWith("SyncMethod");

    /// <summary>The weaver's own <c>[SyncMethod(typeof(Delegate), null)]</c>; original ones name a specific delegate type.</summary>
    public static bool IsWeaverSyncMethodAttribute(CustomAttribute ca) =>
        IsSyncMethodAttribute(ca) &&
        ca.ConstructorArguments.Count == 2 &&
        ca.ConstructorArguments[0].Value is TypeReference { FullName: "System.Delegate" } &&
        ca.ConstructorArguments[1].Value == null;

    // ---------------------------------------------------------------- sync-member fields

    /// <summary>
    /// The weaver's "ShouldProcessField": a readonly sync-member field (a type implementing
    /// ISyncMember, or a generic parameter constrained to one), not a compiler backing field. A field
    /// of an interface or abstract type is not one (the weaver cannot construct it).
    /// </summary>
    public static bool IsSyncMemberField(FieldDefinition field)
    {
        if (field.Name.Contains("__BackingField") || !field.IsInitOnly) return false;
        if (field.FieldType is GenericParameter gp)
            return gp.HasConstraints && gp.Constraints.Any(c => ImplementsISyncMember(c.ConstraintType));
        TypeDefinition? type;
        try { type = field.FieldType.Resolve(); }
        catch { return false; }
        return type is { IsInterface: false, IsAbstract: false } && ImplementsISyncMember(field.FieldType);
    }

    public static bool ImplementsISyncMember(TypeReference type)
    {
        try
        {
            return type.Resolve() is { } def && def.EnumerateAllInterfaces().Any(i => i.FullName == SyncMemberInterface);
        }
        catch { return false; }
    }

    public static string FieldKey(string declaringType, string field) => declaringType + "::" + field;

    /// <summary>
    /// The sound discriminator for the private -> protected flip: the sync fields read or written
    /// from outside their type's family (the type plus its transitive nested types - C# lets nested
    /// classes, lambda display classes included, access the outer type's privates). Such a field
    /// could never have been private, so it was protected in the original source.
    /// Keys: <see cref="FieldKey"/>.
    /// </summary>
    public static HashSet<string> ComputeExternallyAccessed(ModuleDefinition module)
    {
        var families = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var root in module.Types)
        {
            if (!families.TryGetValue(root.FullName, out var family))
                families[root.FullName] = family = new HashSet<string>(StringComparer.Ordinal);
            family.UnionWith(root.SelfAndNestedTypes().Select(t => t.FullName));
        }

        var external = new HashSet<string>(StringComparer.Ordinal);
        foreach (var method in module.AllTypes().SelectMany(t => t.Methods).Where(m => m.HasBody))
            foreach (var ins in method.Body.Instructions)
            {
                if (!(ins.OpCode == OpCodes.Ldfld || ins.OpCode == OpCodes.Stfld || ins.OpCode == OpCodes.Ldflda)) continue;
                if (ins.Operand is not FieldReference { DeclaringType: { } declaring } field) continue;
                string owner = GenericDefinitionName(declaring);
                if (families.TryGetValue(owner, out var family) && !family.Contains(method.DeclaringType.FullName))
                    external.Add(FieldKey(owner, field.Name));
            }
        return external;
    }

    // field refs often carry an instantiated declaring type (ComponentBase`1<Component>): use the definition's name
    static string GenericDefinitionName(TypeReference type)
    {
        while (type is GenericInstanceType gi) type = gi.ElementType;
        return type.FullName;
    }

    // ---------------------------------------------------------------- CoderAOT.Dummy

    /// <summary>
    /// The weaver's target, mirrored exactly: the LAST top-level type whose name contains
    /// "CoderAOT" (in FrooxEngine.dll the non-generic static class FrooxEngine.CoderAOT).
    /// </summary>
    public static TypeDefinition? FindCoderAot(ModuleDefinition module) =>
        module.Types.LastOrDefault(t => t.Name.Contains("CoderAOT"));

    // ---------------------------------------------------------------- version stamp

    /// <summary>The weaver stamps "yyyy.M.d.hhmm": the major component is a 4-digit year.</summary>
    public static bool LooksLikeWeaverTimestamp(string? version)
    {
        var parts = (version ?? "").Split('.');
        return parts.Length >= 2 && int.TryParse(parts[0], out int major) && major is >= 1980 and <= 2100;
    }
}

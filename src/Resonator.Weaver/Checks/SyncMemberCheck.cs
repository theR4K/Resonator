using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Collections.Generic;
using Resonator.Weaver.Cecil;

namespace Resonator.Weaver.Checks;

/// <summary>
/// Consistency of the weaver-generated sync-member methods in an assembly that is NOT reweaved
/// (Flatweave and its consumers). The generated GetSyncMember(int) is a flat switch over the whole
/// hierarchy with no base call, so:
///
///   [missing]    a concrete Worker declares sync fields but inherits an ANCESTOR's generated
///                GetSyncMember (hand-written code, or a patch added a subclass): its own members
///                are unreachable by index. Fix: override with the base Worker's reflection lookup.
///   [stale]      a type's own GetSyncMember does not reach every field its InitializeSyncMembers
///                creates, or every member its parent's reaches (a patch added a sync field
///                without extending the switch).
///   [syncmethod] a method assigned as a sync delegate inside a Worker has no [SyncMethod] - the
///                weaver would have added one; without it the engine rejects the delegate.
///                Mirrors the weaver's AssemblyPostProcessor.IsDelegateAssignment.
///
/// InitializeSyncMembers needs no check: the chain always ends in the base Worker's reflection
/// loop, which creates every field.
/// </summary>
public static class SyncMemberCheck
{
    public const string GetSyncMemberFix = "public override ISyncMember GetSyncMember(int index) => GetSyncMemberFieldInfo(index).GetValue(this) as ISyncMember;";
    public const string SyncMethodFix = "[SyncMethod(typeof(Delegate), null)]";

    /// <summary>Returns 0 when consistent, 1 otherwise. <paramref name="searchDirs"/>: where referenced assemblies resolve from.</summary>
    public static int Run(string dllPath, TextWriter log, IEnumerable<string>? searchDirs = null)
    {
        using var assembly = AssemblyFile.Read(dllPath, searchDirs: searchDirs);
        var workers = WeaverSpec.CollectWorkers(assembly.MainModule);

        int missing = 0, stale = 0, checkedSwitches = 0;
        foreach (var type in workers)
        {
            var own = type.Methods.FirstOrDefault(WeaverSpec.IsGetSyncMember);
            var inherited = InheritedGetSyncMember(type);
            if (own == null)
            {
                // no inherited generated switch: it resolves to Worker's reflection lookup - fine
                if (type.IsAbstract || !type.Fields.Any(IsSyncField) || inherited == null) continue;
                missing++;
                log.WriteLine($"[missing] {type.FullName}: declares sync fields but inherits the generated GetSyncMember of {inherited.DeclaringType.FullName}");
                log.WriteLine("          add: " + GetSyncMemberFix);
                continue;
            }
            if (!IsGenerated(own)) continue;   // hand-written override: trust it

            checkedSwitches++;
            var needed = FieldsAccessed(type.Methods.FirstOrDefault(WeaverSpec.IsInitializeSyncMembers), OpCodes.Stfld);
            if (inherited != null && IsGenerated(inherited)) needed.UnionWith(FieldsAccessed(inherited, OpCodes.Ldfld));
            var gap = needed.Except(FieldsAccessed(own, OpCodes.Ldfld)).OrderBy(x => x, StringComparer.Ordinal).ToList();
            if (gap.Count == 0) continue;
            stale++;
            log.WriteLine($"[stale]   {type.FullName}: GetSyncMember does not reach {string.Join(", ", gap)}");
        }

        int syncMethod = 0;
        var reported = new HashSet<MethodDefinition>();
        foreach (var (site, target) in workers.SelectMany(DelegateAssignments))
        {
            if (target.Module != assembly.MainModule || !reported.Add(target)) continue;
            if (target.CustomAttributes.Any(a => AttributeInherits(a, "FrooxEngine.SyncMethod") || AttributeInherits(a, "FrooxEngine.DataModelBlock"))) continue;
            syncMethod++;
            log.WriteLine($"[syncmethod] {target.DeclaringType.FullName}::{target.Name}: assigned as a sync delegate in {site.DeclaringType.Name}.{site.Name} but has no [SyncMethod]");
            log.WriteLine("          add: " + SyncMethodFix);
        }

        int failures = missing + stale + syncMethod;
        log.WriteLine($"syncmembers: {checkedSwitches} generated switch(es) checked, {missing} missing, {stale} stale, {syncMethod} syncmethod -> {(failures == 0 ? "OK" : "FAIL")}");
        return failures == 0 ? 0 : 1;
    }

    // ---------------------------------------------------------------- generated switches

    /// <summary>The nearest ancestor's GetSyncMember below the base Worker (null = Worker's own reflection lookup).</summary>
    static MethodDefinition? InheritedGetSyncMember(TypeDefinition type)
    {
        for (var b = type.BaseType.TryResolve(); b != null && b.FullName != WeaverSpec.WorkerType; b = b.BaseType.TryResolve())
            if (b.Methods.FirstOrDefault(WeaverSpec.IsGetSyncMember) is { } m) return m;
        return null;
    }

    /// <summary>
    /// The weaver's shape: field loads, no base call, not the reflection lookup. (Not "has a switch
    /// opcode": recompiled, Roslyn lowers small switches to compares.)
    /// </summary>
    static bool IsGenerated(MethodDefinition m) => m.HasBody
        && m.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldfld)
        && !m.Body.Instructions.Any(i => (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt)
                                         && i.Operand is MethodReference { Name: "GetSyncMember" or "GetSyncMemberFieldInfo" });

    static HashSet<string> FieldsAccessed(MethodDefinition? m, OpCode op) =>
        m is not { HasBody: true } ? new(StringComparer.Ordinal)
        : m.Body.Instructions.Where(i => i.OpCode == op).Select(i => i.Operand).OfType<FieldReference>()
            .Select(f => f.Name).ToHashSet(StringComparer.Ordinal);

    static bool IsSyncField(FieldDefinition f) =>
        !f.IsStatic && !f.Name.Contains("__BackingField") && WeaverSpec.ImplementsISyncMember(f.FieldType);

    // ---------------------------------------------------------------- delegate assignments

    /// <summary>The weaver's scan: every method of the worker and of its nested types.</summary>
    static IEnumerable<(MethodDefinition Site, MethodDefinition Target)> DelegateAssignments(TypeDefinition worker)
    {
        foreach (var method in worker.SelfAndNestedTypes().SelectMany(t => t.Methods).Where(m => m.HasBody))
        {
            var ins = method.Body.Instructions;
            for (int i = 0; i < ins.Count; i++)
            {
                int offset = DelegateAssignmentOffset(ins, i);
                if (offset == 0 || i + offset < 0) continue;
                var fn = ins[i + offset];
                if ((fn.OpCode == OpCodes.Ldftn || fn.OpCode == OpCodes.Ldvirtftn)
                    && fn.Operand is MethodReference r && r.TryResolve() is { } target)
                    yield return (method, target);
            }
        }
    }

    /// <summary>AssemblyPostProcessor.IsDelegateAssignment: offset of the ldftn feeding this call; 0 = not one.</summary>
    static int DelegateAssignmentOffset(Collection<Instruction> ins, int pos)
    {
        var x = ins[pos];
        if (!((x.OpCode == OpCodes.Callvirt || x.OpCode == OpCodes.Newobj) && x.Operand is MethodReference m)) return 0;
        if (m.Name == "set_Target" && m.DeclaringType.TryResolve() is { } declaring
            && declaring.InheritsFrom("FrooxEngine.SyncDelegate", includeSelf: true, partialMatch: true))
            return pos - 4 > 0 && ins[pos - 4].OpCode == OpCodes.Ldftn ? -4 : -2;
        if (m.Name == ".ctor" && m.DeclaringType.FullName.Contains("ButtonEventHandler"))
            return PassedToLocalMethod(ins, pos) ? 0 : -1;
        if (m.Name == "Add" && m.DeclaringType.FullName.Contains("SyncDelegateList"))
            return -2;
        return 0;
    }

    /// <summary>AssemblyPostProcessor.IsButtonEventHandlerPassedToLocalMethod.</summary>
    static bool PassedToLocalMethod(Collection<Instruction> ins, int pos)
    {
        for (int i = pos; i < ins.Count; i++)
            if ((ins[i].OpCode == OpCodes.Call || ins[i].OpCode == OpCodes.Callvirt) && ins[i].Operand is MethodReference r
                && r.HasParameters && r.Parameters.Any(p => p.ParameterType.FullName.Contains("ButtonEventHandler")))
                return r.Name.Contains("Local");
        return false;
    }

    static bool AttributeInherits(CustomAttribute a, string baseName) =>
        a.AttributeType.TryResolve() is { } t && t.InheritsFrom(baseName, includeSelf: true, partialMatch: false);
}

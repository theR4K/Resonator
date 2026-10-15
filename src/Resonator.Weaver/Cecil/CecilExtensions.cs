using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;

namespace Resonator.Weaver.Cecil;

/// <summary>
/// Type-hierarchy helpers shared by the Resonator tools. They mirror the helpers the shipped
/// FrooxEngine.Weaver carries (its decompiled CecilExtensions), so the tools and the weaver
/// reason about types the same way - with one deliberate difference: resolve failures answer
/// "no" instead of throwing. When mirroring the weaver's decisions that is the conservative
/// choice: it only ever declines to touch a member, it never rewrites the wrong one.
/// </summary>
public static class CecilExtensions
{
    /// <summary>Every type of the module, nested types included (each type before its nested ones).</summary>
    public static IEnumerable<TypeDefinition> AllTypes(this ModuleDefinition module) => Flatten(module.Types);

    /// <summary>The type and its (transitive) nested types.</summary>
    public static IEnumerable<TypeDefinition> SelfAndNestedTypes(this TypeDefinition type) => Flatten([type]);

    static IEnumerable<TypeDefinition> Flatten(IEnumerable<TypeDefinition> types) =>
        types.SelectMany(t => t.NestedTypes.Count == 0 ? [t] : Flatten(t.NestedTypes).Prepend(t));

    /// <summary>True if <paramref name="type"/> is or inherits from <paramref name="baseTypeName"/> (full name).</summary>
    public static bool InheritsFrom(this TypeDefinition type, string baseTypeName) =>
        type.InheritsFrom(baseTypeName, includeSelf: true, partialMatch: false);

    /// <summary>
    /// Weaver-compatible check: walks the base-type chain by full name.
    /// <paramref name="includeSelf"/> = also match the type itself;
    /// <paramref name="partialMatch"/> = prefix match (the weaver's "SyncDelegate" family test).
    /// </summary>
    public static bool InheritsFrom(this TypeDefinition type, string baseTypeName, bool includeSelf, bool partialMatch)
    {
        ArgumentNullException.ThrowIfNull(type);
        bool Matches(TypeReference t) => partialMatch ? t.FullName.StartsWith(baseTypeName) : t.FullName == baseTypeName;
        try
        {
            if (includeSelf && Matches(type)) return true;
            if (type.BaseType == null) return false;
            if (Matches(type.BaseType)) return true;
            return type.BaseType.Resolve().InheritsFrom(baseTypeName, includeSelf: false, partialMatch);
        }
        catch
        {
            return false;   // unknown base => "not a worker", never a false positive
        }
    }

    /// <summary>All interfaces implemented by <paramref name="type"/> or any base type.</summary>
    public static IEnumerable<TypeReference> EnumerateAllInterfaces(this TypeDefinition type)
    {
        foreach (var i in type.Interfaces) yield return i.InterfaceType;
        if (type.BaseType == null) yield break;
        foreach (var i in type.BaseType.Resolve().EnumerateAllInterfaces()) yield return i;
    }

    /// <summary>The type's definition, or null when it cannot be resolved.</summary>
    public static TypeDefinition? TryResolve(this TypeReference? type)
    {
        try { return type?.Resolve(); } catch { return null; }
    }

    /// <summary>The method's definition, or null when it cannot be resolved.</summary>
    public static MethodDefinition? TryResolve(this MethodReference method)
    {
        try { return method.Resolve(); } catch { return null; }
    }
}

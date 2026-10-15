using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Mono.Cecil;
using Resonator.Weaver.Cecil;

namespace Resonator.Deweaver.Commands;

/// <summary>
/// <c>diff &lt;a.dll&gt; &lt;b.dll&gt;</c> - structural diff: types, fields, methods, attributes and IL bodies.
/// The round-trip proof: weaver(deweave(shipped)) vs shipped.
///
/// <c>diff --api &lt;a.dll&gt; &lt;b.dll&gt; [--allowlist &lt;file.json&gt;]</c> - the API surface only, for a
/// RECOMPILED dll vs the original: IL is not compared (locals and sequence points legitimately
/// differ) and compiler-generated types and members (unstable numbering) are ignored. The
/// allowlist ({"types": [...]}) names intentional seam types whose differences are expected.
/// </summary>
internal static class AssemblyDiff
{
    const int MaxPrinted = 200;

    public static int Run(string[] args)
    {
        bool api = args.Contains("--api");
        int at = Array.IndexOf(args, "--allowlist");
        string? allowlist = at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
        var paths = args.Where((a, i) => a != "--api" && a != "--allowlist" && (at < 0 || i != at + 1)).ToList();
        if (paths.Count != 2) throw new UsageException("usage: Resonator.Deweaver diff [--api] <a.dll> <b.dll> [--allowlist <file.json>]");

        using var a = AssemblyFile.Read(paths[0], searchDirs: Program.ResoniteDirs());
        using var b = AssemblyFile.Read(paths[1], searchDirs: Program.ResoniteDirs());
        var diff = new Comparison(api, LoadAllowlist(allowlist));
        if (!api) diff.CompareAssemblies(a, b);
        diff.CompareTypes(a.MainModule, b.MainModule);
        return diff.Report();
    }

    static HashSet<string> LoadAllowlist(string? file)
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal);
        if (file == null) return allowed;
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        foreach (var type in doc.RootElement.GetProperty("types").EnumerateArray()) allowed.Add(type.GetString()!);
        return allowed;
    }

    sealed class Comparison(bool api, HashSet<string> allowed)
    {
        readonly List<string> diffs = new();
        int allowedCount;

        public void CompareAssemblies(AssemblyDefinition a, AssemblyDefinition b)
        {
            if (a.Name.Name != b.Name.Name) diffs.Add($"assembly name: {a.Name.Name} vs {b.Name.Name}");
            if (a.Name.Version?.ToString() != b.Name.Version?.ToString())
                diffs.Add($"[version] {a.Name.Version} vs {b.Name.Version}  (weaver timestamp - expected if both weaved)");
            CompareAttributes("assembly", a.CustomAttributes, b.CustomAttributes);
            CompareAttributes("module", a.MainModule.CustomAttributes, b.MainModule.CustomAttributes);
            CompareSets("", "reference",
                a.MainModule.AssemblyReferences.Select(r => r.FullName),
                b.MainModule.AssemblyReferences.Select(r => r.FullName));
        }

        public void CompareTypes(ModuleDefinition a, ModuleDefinition b)
        {
            var typesA = Types(a);
            var typesB = Types(b);
            foreach (var (only, other, side) in new[] { (typesA, typesB, "A"), (typesB, typesA, "B") })
                foreach (string name in only.Keys.Where(n => !other.ContainsKey(n)).OrderBy(n => n, StringComparer.Ordinal))
                {
                    if (allowed.Contains(name)) allowedCount++;
                    else diffs.Add($"type only in {side}: {name}");
                }

            foreach (var (name, typeA) in typesA.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                if (typesB.TryGetValue(name, out var typeB) && !allowed.Contains(name))
                    CompareType(name, typeA, typeB);
        }

        void CompareType(string name, TypeDefinition a, TypeDefinition b)
        {
            string baseA = a.BaseType?.FullName ?? "(null)", baseB = b.BaseType?.FullName ?? "(null)";
            if (baseA != baseB) diffs.Add($"{name}: base {baseA} vs {baseB}");
            CompareSets(name + ": ", "interface", a.Interfaces.Select(i => i.InterfaceType.FullName), b.Interfaces.Select(i => i.InterfaceType.FullName));
            CompareAttributes(name, a.CustomAttributes.Where(IsApiAttribute), b.CustomAttributes.Where(IsApiAttribute));

            // fields: by name + type; the access level (and, full diff, every attribute flag) must match
            var fieldsA = a.Fields.ToDictionary(FieldKey, StringComparer.Ordinal);
            var fieldsB = b.Fields.ToDictionary(FieldKey, StringComparer.Ordinal);
            CompareSets(name + ": ", "field", fieldsA.Keys, fieldsB.Keys);
            foreach (var (key, fa) in fieldsA)
                if (fieldsB.TryGetValue(key, out var fb) && FieldFlags(fa) != FieldFlags(fb))
                    diffs.Add($"{name}: field {fa.Name} access {Access(fa)} vs {Access(fb)}");

            // methods: by signature; full diff also compares attributes and IL
            var methodsA = a.Methods.Where(IsApiMember).ToDictionary(Signature, StringComparer.Ordinal);
            var methodsB = b.Methods.Where(IsApiMember).ToDictionary(Signature, StringComparer.Ordinal);
            CompareSets(name + ": ", "method", methodsA.Keys, methodsB.Keys);
            if (api) return;
            foreach (var (sig, ma) in methodsA.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                if (!methodsB.TryGetValue(sig, out var mb)) continue;
                CompareAttributes($"{name}.{sig}", ma.CustomAttributes, mb.CustomAttributes);
                if (ma.HasBody != mb.HasBody) diffs.Add($"{name}: method body presence differs: {ma.Name}");
                else if (ma.HasBody && Body(ma) != Body(mb)) diffs.Add($"{name}: method body differs: {ma.Name}");
            }
        }

        public int Report()
        {
            if (diffs.Count == 0)
            {
                Console.WriteLine(allowedCount > 0
                    ? $"STRUCTURALLY IDENTICAL ({allowedCount} allowed seam type(s))"
                    : api ? "STRUCTURALLY IDENTICAL (API surface matches)" : "IDENTICAL (structurally)");
                return 0;
            }
            foreach (string d in diffs.Take(MaxPrinted)) Console.WriteLine("DIFF: " + d);
            if (diffs.Count > MaxPrinted) Console.WriteLine($"... and {diffs.Count - MaxPrinted} more");
            Console.WriteLine($"{diffs.Count} difference(s)" + (allowedCount > 0 ? $", {allowedCount} allowed seam diff(s) ignored" : ""));
            return 1;
        }

        // ------------------------------------------------------------ what counts (API mode drops compiler noise)

        Dictionary<string, TypeDefinition> Types(ModuleDefinition module)
        {
            var types = new Dictionary<string, TypeDefinition>(StringComparer.Ordinal);
            void Add(TypeDefinition t)
            {
                if (api && IsCompilerNoise(t)) return;   // with its nested types
                types[t.FullName] = t;
                foreach (var nested in t.NestedTypes) Add(nested);
            }
            foreach (var t in module.Types) Add(t);
            return types;
        }

        static bool IsCompilerNoise(TypeDefinition t) =>
            t.Name.Contains("PrivateImplementationDetails") || (t.Namespace ?? "").Contains("PrivateImplementationDetails")
            || t.FullName == "<Module>" || t.Name is "AssemblyInfo" or "ModuleInfo"
            || t.Name.Contains('<');   // display classes, state machines, lambdas: numbering is not stable

        bool IsApiMember(MethodDefinition m) => !api || !m.Name.Contains('<');

        // nullable annotations are compiler bookkeeping, not API
        bool IsApiAttribute(CustomAttribute ca) => !api || !(
            ca.AttributeType.FullName.EndsWith("NullableAttribute") ||
            ca.AttributeType.FullName.EndsWith("NullableContextAttribute") ||
            ca.AttributeType.FullName.EndsWith("CompilerGeneratedAttribute"));

        ushort FieldFlags(FieldDefinition f) =>
            (ushort)(api ? f.Attributes & (FieldAttributes.FieldAccessMask | FieldAttributes.InitOnly) : f.Attributes);

        // ------------------------------------------------------------ set comparison

        void CompareSets(string prefix, string what, IEnumerable<string> a, IEnumerable<string> b)
        {
            var setA = a.ToHashSet(StringComparer.Ordinal);
            var setB = b.ToHashSet(StringComparer.Ordinal);
            foreach (string x in setA.Except(setB).OrderBy(x => x, StringComparer.Ordinal)) diffs.Add($"{prefix}{what} only in A: {x}");
            foreach (string x in setB.Except(setA).OrderBy(x => x, StringComparer.Ordinal)) diffs.Add($"{prefix}{what} only in B: {x}");
        }

        void CompareAttributes(string owner, IEnumerable<CustomAttribute> a, IEnumerable<CustomAttribute> b) =>
            CompareSets(owner + ": ", "attr", a.Select(Describe), b.Select(Describe));
    }

    // ---------------------------------------------------------------- canonical text forms

    static string FieldKey(FieldDefinition f) => f.Name + "|" + f.FieldType.FullName;

    static string Access(FieldDefinition f) =>
        "0x" + ((ushort)(f.Attributes & FieldAttributes.FieldAccessMask)).ToString("X") + (f.IsInitOnly ? "+initonly" : "");

    /// <summary>Name, generic arity, parameters (with in/out - they make a different signature) and return type.</summary>
    static string Signature(MethodDefinition m)
    {
        string parameters = string.Join(",", m.Parameters.Select(p => (p.IsIn ? "in " : p.IsOut ? "out " : "") + p.ParameterType.FullName));
        string generics = m.GenericParameters.Count > 0 ? "<g" + m.GenericParameters.Count + ">" : "";
        return $"{m.Name}{generics}({parameters}):{m.ReturnType.FullName}";
    }

    static string Body(MethodDefinition m)
    {
        var sb = new StringBuilder(m.Body.InitLocals ? "L" : "-");
        foreach (var i in m.Body.Instructions)
        {
            sb.Append('|').Append(i.OpCode.Code);
            if (i.Operand != null) sb.Append('#').Append(Operand(i.Operand));
        }
        foreach (var eh in m.Body.ExceptionHandlers)
            sb.Append($"[{eh.HandlerType}>{eh.TryStart}<{eh.HandlerStart}({eh.CatchType})]");
        return sb.ToString();
    }

    static string Operand(object o) => o switch
    {
        MethodReference m => "M:" + m.FullName,
        FieldReference f => "F:" + f.FullName,
        TypeReference t => "T:" + t.FullName,
        string s => "S:" + s,
        float f => "f" + f,
        int i => "i" + i,
        uint u => "u" + u,
        long l => "l" + l,
        sbyte b => "b" + b,
        _ => o.GetType().Name + ":" + o,
    };

    static string Describe(CustomAttribute ca) =>
        ca.AttributeType.FullName + string.Concat(ca.ConstructorArguments.Select(a => "(" + Argument(a.Value) + ")"));

    static string? Argument(object? v) => v switch
    {
        null => "null",
        TypeReference t => "type:" + t.FullName,
        ICollection items =>"{" + string.Join(",", items.Cast<object>().Select(Argument)) + "}",
        _ => v.ToString(),
    };
}

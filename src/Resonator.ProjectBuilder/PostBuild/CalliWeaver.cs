using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Resonator.Weaver.Cecil;

namespace Resonator.ProjectBuilder.PostBuild;

/// <summary>
/// CSCore's COM interop is written as stub methods marked [CSCalli] whose bodies throw: upstream's
/// post-build tool (CSCli) rewrites every call to such a stub into a stdcall <c>calli</c> (the stub's
/// last parameter is the function pointer) and drops the helper types marked with the remove
/// attribute. A source target of CSCore needs the same step after compiling (spec "calli").
/// Idempotent: a woven assembly has no such calls left.
/// </summary>
public static class CalliWeaver
{
    /// <summary>Weave <paramref name="dllPath"/> in place. Returns the number of call sites rewritten.</summary>
    public static int Run(string dllPath, string calliAttribute, string? removeTypeAttribute, TextWriter log)
    {
        string path = Path.GetFullPath(dllPath);
        var assembly = AssemblyFile.Read(path);
        var module = assembly.MainModule;
        var types = module.AllTypes().ToList();

        int replaced = 0;
        foreach (var method in types.SelectMany(t => t.Methods).Where(m => m.HasBody))
        {
            var il = method.Body.GetILProcessor();
            for (int i = 0; i < il.Body.Instructions.Count; i++)
            {
                var ins = il.Body.Instructions[i];
                // the stubs live in this assembly: plain method definitions
                if (ins.OpCode.Code != Code.Call || ins.Operand is not MethodDefinition stub || !HasAttribute(stub, calliAttribute)) continue;
                var site = new CallSite(stub.ReturnType) { CallingConvention = MethodCallingConvention.StdCall };
                // copies: adding the stub's own ParameterDefinitions would re-own them to the call site,
                // and the stub (still in the assembly) would be written with their sequence/owner wrong
                foreach (var p in stub.Parameters.SkipLast(1)) site.Parameters.Add(new ParameterDefinition(p.ParameterType));
                il.Replace(ins, il.Create(OpCodes.Calli, site));
                replaced++;
            }
        }

        foreach (var method in types.SelectMany(t => t.Methods))
            foreach (var attr in method.CustomAttributes.Where(c => c.AttributeType.Name == calliAttribute).ToList())
                method.CustomAttributes.Remove(attr);

        var helpers = removeTypeAttribute == null ? [] : types.Where(t => HasAttribute(t, removeTypeAttribute)).ToList();
        foreach (var t in helpers)
        {
            if (t.DeclaringType != null) t.DeclaringType.NestedTypes.Remove(t);
            else module.Types.Remove(t);
        }

        AssemblyFile.ReplaceInPlace(assembly, path);
        log.WriteLine($"calli: {replaced} call(s) -> calli, {helpers.Count} helper type(s) removed ({Path.GetFileName(path)})");
        return replaced;
    }

    static bool HasAttribute(ICustomAttributeProvider p, string name) =>
        p.HasCustomAttributes && p.CustomAttributes.Any(c => c.AttributeType.Name == name);
}

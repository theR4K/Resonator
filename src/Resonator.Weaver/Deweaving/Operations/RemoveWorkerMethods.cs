using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Resonator.Weaver.Deweaving.Operations;

/// <summary>
/// Inverse of: the weaver adds InitializeSyncMembers() / GetSyncMember(int) / __New() to every
/// Worker subclass. They are removed, or - in <see cref="MethodMode.Empty"/> - kept with a
/// <c>throw new NotSupportedException()</c> body.
/// </summary>
public sealed class RemoveWorkerMethods : IRevertOperation
{
    public void Apply(RevertContext context)
    {
        foreach (var worker in context.Workers)
            foreach (var method in worker.Methods.Where(WeaverSpec.IsWeaverAddedMethod).ToList())
            {
                if (context.Options.MethodMode == MethodMode.Remove) worker.Methods.Remove(method);
                else if (method.HasBody) ReplaceWithThrow(method, context.Module);
                Count(method, context.Report);
            }
    }

    static void ReplaceWithThrow(MethodDefinition method, ModuleDefinition module)
    {
        var ctor = module.ImportReference(typeof(NotSupportedException)).Resolve()
            .Methods.First(c => c.Name == ".ctor" && !c.HasParameters);
        var body = method.Body;
        body.Instructions.Clear();
        body.ExceptionHandlers.Clear();
        body.InitLocals = false;
        var il = body.GetILProcessor();
        il.Emit(OpCodes.Newobj, module.ImportReference(ctor, method.DeclaringType));
        il.Emit(OpCodes.Throw);
    }

    static void Count(MethodDefinition method, DeweaveReport report)
    {
        if (WeaverSpec.IsInitializeSyncMembers(method)) report.RemovedInitSyncMembers++;
        else if (WeaverSpec.IsGetSyncMember(method)) report.RemovedGetSyncMember++;
        else report.RemovedNew++;
    }
}

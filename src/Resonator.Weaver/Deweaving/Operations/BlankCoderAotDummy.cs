using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Resonator.Weaver.Deweaving.Operations;

/// <summary>
/// Inverse of: the weaver rewrites the body of the non-generic CoderAOT.Dummy() to reference every
/// sync-member type (an AOT aid). The original body was empty, so it is emptied again.
/// </summary>
public sealed class BlankCoderAotDummy : IRevertOperation
{
    public void Apply(RevertContext context)
    {
        if (context.Options.BlankAotDummy && Blank(context.Module))
            context.Report.BlankAotDummy = true;
    }

    /// <summary>Empty CoderAOT.Dummy() (also used by the flattener). Returns false if there is none.</summary>
    public static bool Blank(ModuleDefinition module)
    {
        var dummy = WeaverSpec.FindCoderAot(module)?.Methods.FirstOrDefault(m => m.Name == "Dummy" && m.HasBody);
        if (dummy == null) return false;

        dummy.Body.Instructions.Clear();
        dummy.Body.ExceptionHandlers.Clear();
        dummy.Body.InitLocals = false;
        dummy.Body.GetILProcessor().Emit(OpCodes.Ret);
        return true;
    }
}

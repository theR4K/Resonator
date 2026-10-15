using System.Linq;
using Resonator.Weaver.Cecil;

namespace Resonator.Weaver.Deweaving.Operations;

/// <summary>
/// Inverse of: the weaver adds [SyncMethod(typeof(Delegate), null)] to methods passed as delegates
/// inside Worker methods. Original SyncMethod attributes (a specific delegate type) are kept.
/// </summary>
public sealed class RemoveSyncMethodAttributes : IRevertOperation
{
    public void Apply(RevertContext context)
    {
        foreach (var method in context.Module.AllTypes().SelectMany(t => t.Methods))
            foreach (var attribute in method.CustomAttributes.Where(WeaverSpec.IsSyncMethodAttribute).ToList())
            {
                if (WeaverSpec.IsWeaverSyncMethodAttribute(attribute))
                {
                    method.CustomAttributes.Remove(attribute);
                    context.Report.RemovedSyncMethodDelegateNull++;
                }
                else context.Report.KeptSyncMethodOriginal++;
            }
    }
}

using System.Linq;

namespace Resonator.Weaver.Deweaving.Operations;

/// <summary>Inverse of: the weaver marks every processed module with [Description("FROOXENGINE_WEAVED")].</summary>
public sealed class RemoveProcessedAttribute : IRevertOperation
{
    public void Apply(RevertContext context)
    {
        var attributes = context.Module.CustomAttributes;
        foreach (var marker in attributes.Where(WeaverSpec.IsProcessedAttribute).ToList())
        {
            attributes.Remove(marker);
            context.Report.RemovedProcessedAttr++;
        }
    }
}

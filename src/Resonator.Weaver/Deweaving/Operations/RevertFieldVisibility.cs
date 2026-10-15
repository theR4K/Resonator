using Mono.Cecil;

namespace Resonator.Weaver.Deweaving.Operations;

/// <summary>
/// Inverse of: the weaver flips readonly sync-member fields private -> protected. Only fields that
/// are exactly protected (Family) are reverted - public was a no-op for the weaver. Fields accessed
/// from outside their type family were protected in the original source (private would not have
/// compiled) and stay (<see cref="WeaverSpec.ComputeExternallyAccessed"/>).
/// </summary>
public sealed class RevertFieldVisibility : IRevertOperation
{
    public void Apply(RevertContext context)
    {
        foreach (var worker in context.Workers)
            foreach (var field in worker.Fields)
            {
                if (!WeaverSpec.IsSyncMemberField(field)) continue;
                if ((field.Attributes & FieldAttributes.FieldAccessMask) != FieldAttributes.Family) continue;
                if (context.ExternallyAccessed.Contains(WeaverSpec.FieldKey(field.DeclaringType.FullName, field.Name))) continue;

                field.Attributes = (field.Attributes & ~FieldAttributes.FieldAccessMask) | FieldAttributes.Private;
                context.Report.RevertedFieldVisibility++;
            }
    }
}

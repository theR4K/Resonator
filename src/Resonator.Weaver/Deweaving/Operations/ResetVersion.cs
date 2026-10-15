using System;
using Mono.Cecil;

namespace Resonator.Weaver.Deweaving.Operations;

/// <summary>
/// Inverse of: the weaver stamps a timestamp version ("yyyy.M.d.hhmm") onto the
/// AssemblyFileVersion / AssemblyVersion attributes and the assembly name. Reset to a stable
/// value (default 1.0.0.0) so rebuilds are comparable.
/// </summary>
public sealed class ResetVersion : IRevertOperation
{
    public void Apply(RevertContext context)
    {
        if (!context.Options.ResetVersion) return;
        string reset = context.Options.VersionReset;
        var assembly = context.Assembly;
        bool changed = false;

        foreach (var attribute in assembly.CustomAttributes)
        {
            string name = attribute.AttributeType.Name;
            if (!name.EndsWith("AssemblyFileVersionAttribute") && !name.EndsWith("AssemblyVersionAttribute")) continue;
            if (attribute.ConstructorArguments.Count == 0) continue;
            if (attribute.ConstructorArguments[0].Value is not string stamp || !WeaverSpec.LooksLikeWeaverTimestamp(stamp)) continue;

            attribute.ConstructorArguments[0] = new CustomAttributeArgument(context.Module.ImportReference(typeof(string)), reset);
            changed = true;
        }

        if (WeaverSpec.LooksLikeWeaverTimestamp(assembly.Name.Version?.ToString()))
        {
            assembly.Name.Version = Version.TryParse(reset, out var clean) ? clean : new Version(1, 0, 0, 0);
            changed = true;
        }
        context.Report.ResetVersion = changed;
    }
}

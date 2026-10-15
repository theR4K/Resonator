using Resonator.Pipeline.Targets;

namespace Resonator.ProjectBuilder.BuildFixes;

/// <summary>A repair driven by the compiler's errors - for artifacts only the compiler can locate.</summary>
internal interface IBuildErrorFix
{
    /// <summary>Fix what <paramref name="buildLog"/> reports; returns the number of fixes made (0 = nothing to do).</summary>
    int Apply(Target target, string buildLog);
}

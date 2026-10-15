using System;
using System.IO;
using Resonator.Pipeline.Infrastructure;
using Resonator.Pipeline.Targets;

namespace Resonator.ProjectBuilder;

/// <summary>
/// The project file: template/template.csproj with the target's TFM and assembly name filled in.
/// Deployed when the project dir has no csproj. Marker-guarded: a file this tool deployed is
/// re-deployed (so TFM / name changes propagate); a consumer's own csproj is never touched.
/// </summary>
internal static class CsprojTemplate
{
    const string Marker = "Resonator template csproj";

    public static void Deploy(Target target, string csproj)
    {
        if (File.Exists(csproj) && !File.ReadAllText(csproj).Contains(Marker)) return;

        string template = Path.Combine(AppContext.BaseDirectory, "template", "template.csproj");
        if (!File.Exists(template)) throw new Exception("template csproj missing: " + template);
        string text = File.ReadAllText(template)
            .Replace("__TFM__", target.Spec.Tfm)
            .Replace("__NAME__", target.Spec.OutName);
        Directory.CreateDirectory(Path.GetDirectoryName(csproj)!);
        File.WriteAllText(csproj, text);
        Log.Info($"csproj: deployed template -> {csproj} (tfm={target.Spec.Tfm}, name={target.Spec.OutName})");
    }
}

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Resonator.Pipeline.Infrastructure;
using Resonator.Pipeline.Targets;

namespace Resonator.ProjectBuilder.BuildFixes;

/// <summary>
/// CS0012 "the type is defined in an assembly that is not referenced": add the reference when the
/// install has that dll. It goes into references.targets - the home of generated references, never
/// a hand-written csproj.
/// </summary>
internal sealed class MissingReferenceFix : IBuildErrorFix
{
    static readonly Regex MissingAssembly = new(@"assembly '([^']+)'");

    public int Apply(Target target, string buildLog)
    {
        var missing = new Dictionary<string, string>();
        foreach (Match m in MissingAssembly.Matches(buildLog))
        {
            string name = m.Groups[1].Value.Split(',')[0].Trim();
            string dll = Path.Combine(target.Settings.ResoniteDir, name + ".dll");
            if (File.Exists(dll)) missing[name] = dll;
        }
        if (missing.Count == 0) return 0;

        string file = File.Exists(target.ReferencesTargets) ? target.ReferencesTargets : target.Csproj!;
        var doc = XDocument.Load(file);
        int added = 0;
        foreach (var (name, dll) in missing)
        {
            if (doc.Descendants("Reference").Any(r => (string?)r.Attribute("Include") == name)) continue;
            var group = doc.Descendants("ItemGroup").FirstOrDefault();
            if (group == null) doc.Root!.Add(group = new XElement("ItemGroup"));
            group.Add(new XElement("Reference", new XAttribute("Include", name), new XElement("HintPath", dll)));
            Log.Info($"    added missing reference: {name} -> {Path.GetFileName(file)}");
            added++;
        }
        if (added > 0) doc.Save(file);
        return added;
    }
}

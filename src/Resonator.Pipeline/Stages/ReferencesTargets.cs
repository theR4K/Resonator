using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Resonator.Pipeline.Infrastructure;
using Resonator.Pipeline.Targets;
using Resonator.Weaver.Cecil;

namespace Resonator.Pipeline.Stages;

/// <summary>
/// The machine-generated half of a project: &lt;project&gt;/references.targets, imported by the csproj
/// (the deployed template, or a consumer's own). It holds:
///   - the compiler settings decompiled source needs
///   - the spec's properties, defines and extra NuGet packages (copied to bin)
///   - the references: the shipped assembly's declared references, closed transitively (the
///     compiler must resolve the API surface of every referenced assembly), plus spec "refs".
///     Each is the install's dll - or, when a package / source target of the set provides it,
///     that package (PackageReference) or project (ProjectReference).
///   - the post-compile calli weaving, for a spec with "calli"
/// Not referenced: the target's own assembly, the spec's "exclude_refs", anything not in the install.
/// </summary>
public static class ReferencesTargets
{
    public static void Write(Target target)
    {
        var spec = target.Spec;
        string InstallDll(string name) => Path.Combine(target.Settings.ResoniteDir, name + ".dll");

        var (closure, candidates) = Candidates(target);
        var providers = target.Settings.Set.Providers;
        var provided = candidates.Where(providers.ContainsKey).Select(n => providers[n]).ToList();
        var installed = candidates.Where(n => !providers.ContainsKey(n) && File.Exists(InstallDll(n))).ToList();

        var packages = new SortedDictionary<string, string>(spec.Packages.ToDictionary(), StringComparer.OrdinalIgnoreCase);
        foreach (var p in provided.Where(p => p.Package != null)) packages[p.Package!.Id] = p.Package.Version;
        var sources = provided.Where(p => p.Kind == TargetKind.Source).ToList();

        var project = new XElement("Project", CompilerSettings());
        if (spec.Kind == TargetKind.Source && File.Exists(target.ShippedDll)) project.Add(ShippedVersion(target.ShippedDll));
        if (spec.Properties.Count > 0)
            project.Add(new XElement("PropertyGroup", spec.Properties.Select(kv => new XElement(kv.Key, kv.Value))));
        if (spec.Defines.Length > 0)
            project.Add(new XElement("PropertyGroup", new XElement("DefineConstants", "$(DefineConstants);" + string.Join(';', spec.Defines))));
        // a class library leaves its packages out of bin; the deployed build must carry them
        // (Flatweave/FrooxEngine -> Concentus, which the install does not ship)
        if (packages.Count > 0)
        {
            project.Add(new XElement("PropertyGroup", new XElement("CopyLocalLockFileAssemblies", "True")));
            project.Add(new XElement("ItemGroup", packages.Select(kv =>
                new XElement("PackageReference", new XAttribute("Include", kv.Key), new XAttribute("Version", kv.Value)))));
        }
        if (spec.Calli != null) project.Add(CalliStep(spec.Calli));
        foreach (var source in sources) project.Add(SourceProjectReference(target, source));
        foreach (string name in installed)
            project.Add(new XElement("ItemGroup", new XElement("Reference", new XAttribute("Include", name),
                // an absolute path: the output may live outside any repo that defines $(ResoniteDir)
                new XElement("HintPath", InstallDll(name)))));

        new XDocument(new XDeclaration("1.0", "utf-8", null), project).Save(target.ReferencesTargets);
        Log.Info($"references: {installed.Count}/{closure.Count} assembly(ies) from the install, {packages.Count} package(s), " +
                 $"{sources.Count} source target(s) -> {target.ReferencesTargets}");
    }

    /// <summary>
    /// The targets of a run: the selected ones plus the source targets they reference (transitively),
    /// sources first. A dependent's csproj references their projects as siblings, so they are built in
    /// the same run - and the run's size sets the layout (Target.InRun), for every tool alike.
    /// A --no-csproj consumer builds the trees itself and supplies those references.
    /// </summary>
    public static List<string> WithSourceTargets(PipelineSettings settings, List<string> selected)
    {
        if (settings.NoCsproj) return selected;
        var all = new HashSet<string>(selected, StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>(selected);
        while (queue.Count > 0)
            foreach (string source in SourceTargets(Target.InRun(settings, queue.Dequeue(), 1)))
                if (all.Add(source)) queue.Enqueue(source);
        if (all.Count == selected.Count) return selected;
        var sources = all.Except(selected, StringComparer.OrdinalIgnoreCase).OrderBy(t => t, StringComparer.Ordinal);
        return sources.Concat(selected).ToList();
    }

    /// <summary>The source targets of the set that <paramref name="target"/> references (built as its siblings).</summary>
    public static List<string> SourceTargets(Target target)
    {
        var providers = target.Settings.Set.Providers;
        return Candidates(target).Candidates.Where(providers.ContainsKey).Select(n => providers[n])
            .Where(p => p.Kind == TargetKind.Source).Select(p => p.Target).ToList();
    }

    /// <summary>The reference closure, and what of it the target references: minus its own name and "exclude_refs".</summary>
    static (HashSet<string> Closure, List<string> Candidates) Candidates(Target target)
    {
        var spec = target.Spec;
        var closure = ReferenceClosure(target.ShippedDll, name => Path.Combine(target.Settings.ResoniteDir, name + ".dll"));
        closure.UnionWith(spec.Refs);
        var dropped = new HashSet<string>(spec.ExcludeRefs, StringComparer.OrdinalIgnoreCase) { spec.BaseName, spec.OutName };
        return (closure, closure.Where(n => !dropped.Contains(n)).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList());
    }

    /// <summary>
    /// The declared references of the shipped dll, closed over the install. (Declared, not a
    /// per-type analysis: that would resolve type forwarders away, e.g. ResoniteLink -> Elements.Assets.)
    /// A source target of a library the install does not ship has none.
    /// </summary>
    static HashSet<string> ReferenceClosure(string shippedDll, Func<string, string> installDll)
    {
        var closure = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(shippedDll)) return closure;
        var queue = new Queue<string>(AssemblyFile.ReferencedAssemblies(shippedDll));
        closure.UnionWith(queue);
        while (queue.Count > 0)
        {
            string dll = installDll(queue.Dequeue());
            if (!File.Exists(dll)) continue;   // framework / not shipped: nothing to close over
            foreach (string reference in AssemblyFile.ReferencedAssemblies(dll))
                if (closure.Add(reference)) queue.Enqueue(reference);
        }
        return closure;
    }

    static XElement CompilerSettings() => new("PropertyGroup",
        new XElement("LangVersion", "13.0"),
        new XElement("AllowUnsafeBlocks", "True"),
        new XElement("GenerateAssemblyInfo", "False"),
        new XElement("TreatWarningsAsErrors", "False"),
        // [LibraryImport] methods decompile with the generated stub as their body; the generator
        // then rejects them (not partial) - skipping them keeps the shipped IL
        new XElement("NoWarn", "$(NoWarn);SYSLIB1050"),
        new XElement("Version", "1.0.0.0"),
        new XElement("FileVersion", "1.0.0.0"),
        new XElement("AssemblyVersion", "1.0.0.0"));

    /// <summary>
    /// A source target replaces a shipped dll that other shipped dlls bind to by version (Elements.Assets
    /// -> FreeImageNET 3.15.1.0); the runtime refuses a lower one. Its Properties/AssemblyInfo is
    /// excluded, so the SDK generates the assembly info, stamped with the shipped version.
    /// </summary>
    static XElement ShippedVersion(string shippedDll)
    {
        string version = System.Reflection.AssemblyName.GetAssemblyName(shippedDll).Version!.ToString();
        return new XElement("PropertyGroup",
            new XElement("GenerateAssemblyInfo", "True"),
            new XElement("Version", version),
            new XElement("FileVersion", version),
            new XElement("AssemblyVersion", version));
    }

    /// <summary>
    /// CSCore-style [CSCalli] stubs, woven by Resonator.ProjectBuilder right after compiling (on the
    /// intermediate assembly, before it is copied to the output) - so any build of the tree gets
    /// it, a consumer's own csproj included.
    /// </summary>
    static XElement CalliStep(CalliSpec calli)
    {
        string exe = Path.Combine(AppContext.BaseDirectory, "Resonator.ProjectBuilder.exe");
        if (!File.Exists(exe)) exe = Path.Combine(AppContext.BaseDirectory, "Resonator.ProjectBuilder");
        string command = $"\"{exe}\" calli \"@(IntermediateAssembly->'%(FullPath)')\" {calli.Attribute}"
                         + (calli.RemoveTypeAttribute != null ? " " + calli.RemoveTypeAttribute : "");
        return new XElement("Target",
            new XAttribute("Name", "ResonatorCalli"),
            new XAttribute("AfterTargets", "CoreCompile"),
            new XAttribute("Condition", "'@(IntermediateAssembly)' != ''"),
            new XElement("Exec", new XAttribute("Command", command)));
    }

    /// <summary>
    /// A source target's project, as a sibling in the output (N-target layout, csproj mode). A
    /// consumer that builds the trees itself (--no-csproj) supplies its own reference.
    /// </summary>
    static XNode SourceProjectReference(Target target, TargetSet.Provider source)
    {
        if (target.Settings.NoCsproj)
            return new XComment($" {source.OutName}: source target '{source.Target}' - the consumer references its own build ");
        string siblingPath = Path.Combine(target.Settings.OutputDir, source.Target, source.OutName + ".csproj");
        if (!File.Exists(siblingPath))
            Log.Warn($"references: source target '{source.Target}' is not built at {siblingPath} - the build will miss " +
                     $"its types (a run of --stage build/fixup only? run all stages so it is built first)");
        string sibling = Path.GetRelativePath(target.ProjectDir, siblingPath);
        return new XElement("ItemGroup", new XElement("ProjectReference",
            new XAttribute("Include", sibling),
            new XAttribute("Condition", $"Exists('{sibling}')")));
    }
}

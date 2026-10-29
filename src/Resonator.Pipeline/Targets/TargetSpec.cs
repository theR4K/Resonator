using System;
using System.Collections.Generic;
using System.IO;

namespace Resonator.Pipeline.Targets;

/// <summary>Where a target's source comes from.</summary>
public enum TargetKind
{
    /// <summary>A decompile of the shipped dll (the default).</summary>
    Decompile,
    /// <summary>An official NuGet package: nothing is built, dependents get a PackageReference.</summary>
    Package,
    /// <summary>An official git repo / archive at a pinned ref, checked out instead of decompiled, then patched.</summary>
    Source,
}

/// <summary>What the prepare stage does to the shipped dll before it is decompiled (spec "weave").</summary>
public enum WeaveMode
{
    /// <summary>Revert the weave exactly: the pre-weave original.</summary>
    Deweave,
    /// <summary>Keep the weave, fix only what C# cannot express: no reweave needed.</summary>
    Flatten,
    /// <summary>Not weaver-processed: decompile the shipped dll as is.</summary>
    None,
}

public sealed record PackageSpec(string Id, string Version);

/// <param name="Files">The files of "dir" a checkout takes, as globs relative to it ("**" any folders, "*" any name part):
/// <c>**/*.cs</c> unless the spec says otherwise (a tree target takes headers, CMake files, ...).</param>
/// <param name="Submodules">git submodules of the repo fetched with it (shallow, recursive), paths relative to the repo.</param>
public sealed record SourceSpec(string? Git, string? Archive, string Ref, string Dir, string[] Exclude, string[] Files, string[] Submodules)
{
    public static readonly string[] CsFiles = ["**/*.cs"];

    public override string ToString() => Git != null ? $"{Git} @ {Ref}" : Archive!;
}

/// <summary>Post-compile [CSCalli] stubs -> calli (CSCore).</summary>
public sealed record CalliSpec(string Attribute, string? RemoveTypeAttribute);

/// <summary>
/// One target: its target.json merged down the "extends" chain (root ancestor first, see
/// <see cref="TargetSpecLoader"/>). Pure data - where the project goes is <see cref="Target"/>.
/// </summary>
public sealed record TargetSpec
{
    public required string Name { get; init; }
    /// <summary>The leaf spec's directory (the generated patches of a sync go to its patches/).</summary>
    public required string Dir { get; init; }
    /// <summary>The shipped assembly, e.g. FrooxEngine.dll.</summary>
    public required string Assembly { get; init; }
    public string? OutAssembly { get; init; }
    public string Tfm { get; init; } = "net10.0";

    public WeaveMode Weave { get; init; } = WeaveMode.Deweave;
    /// <summary>The weaver dll (in the install) that must be the known one before deweave / flatten; null = no check.</summary>
    public string? WeaverCheck { get; init; }

    public PackageSpec? Package { get; init; }
    public SourceSpec? Source { get; init; }
    public CalliSpec? Calli { get; init; }

    /// <summary>Decompile only these types (a split-out assembly of one shipped dll).</summary>
    public string[] SelectTypes { get; init; } = [];
    /// <summary>Decompile everything except these (the types the split-out targets claim).</summary>
    public string[] ExcludeTypes { get; init; } = [];
    public string[] Refs { get; init; } = [];
    public string[] ExcludeRefs { get; init; } = [];
    public string[] FixCtorTypes { get; init; } = [];
    /// <summary>Hand-written code in the project dir that regeneration must not wipe.</summary>
    public string[] KeepDirs { get; init; } = ["Injected"];
    public string[] Defines { get; init; } = [];
    /// <summary>Extra NuGet references: id -> version.</summary>
    public IReadOnlyDictionary<string, string> Packages { get; init; } = new Dictionary<string, string>();
    /// <summary>MSBuild properties, e.g. ImplicitUsings of an official source.</summary>
    public IReadOnlyDictionary<string, string> Properties { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// A source tree for a build that is not a C# project of Resonator's (spec "tree": native code built with CMake, a
    /// repo's own projects): checked out and patched only - no references.targets, csproj or build, no assembly it
    /// provides to the set.
    /// </summary>
    public bool Tree { get; init; }

    /// <summary>The patch layers, root ancestor first, this target's own last.</summary>
    public IReadOnlyList<string> PatchDirs { get; init; } = [];

    public TargetKind Kind => Package != null ? TargetKind.Package : Source != null ? TargetKind.Source : TargetKind.Decompile;
    public string BaseName => Path.GetFileNameWithoutExtension(Assembly);
    /// <summary>The files the target's source tree is made of (globs relative to it): a source's "files", else every .cs.</summary>
    public string[] FilePatterns => Source?.Files ?? SourceSpec.CsFiles;
    public string OutName => OutAssembly ?? BaseName;
    /// <summary>This target's own patch layer.</summary>
    public string PatchDir => Path.Combine(Dir, "patches");

    public bool IsAssembly(string assembly) => Assembly.Equals(assembly, StringComparison.OrdinalIgnoreCase);
}

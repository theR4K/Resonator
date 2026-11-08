using System;
using System.IO;
using System.Text.RegularExpressions;
using Resonator.Pipeline.Infrastructure;

namespace Resonator.Pipeline.Stages;

/// <summary>
/// The official .NET Framework reference assemblies (NuGet Microsoft.NETFramework.ReferenceAssemblies.netXY)
/// for decompiling a shipped .NET Framework dll. Windows resolves its mscorlib, System, System.Drawing, ...
/// from the GAC; Linux has none, so they stayed unresolved and the decompiler wrote different code: a nested
/// type lost its declaring type (FreeImageNET's <c>Image.GetThumbnailImageAbort</c> became
/// <c>GetThumbnailImageAbort</c>, which does not compile). With these on the search path the output is the
/// same on every OS, and identical to the GAC-resolved one on Windows.
/// The package is the one the SDK restores to compile a .NET Framework TFM anyway; when the NuGet global
/// packages folder does not have it yet, a restore of a throwaway project fetches it.
/// </summary>
internal static class FrameworkReferences
{
    const string PackageVersion = "1.0.3";

    /// <summary>The reference assembly directory for a ".NETFramework,Version=vX.Y[.Z]" id; null for other frameworks.</summary>
    public static string? DirectoryFor(string? targetFrameworkId, string workDir)
    {
        var m = Regex.Match(targetFrameworkId ?? "", @"^\.NETFramework,Version=v(\d)\.(\d)(?:\.(\d))?$");
        if (!m.Success)
            return null;
        string version = $"v{m.Groups[1]}.{m.Groups[2]}" + (m.Groups[3].Success ? "." + m.Groups[3] : "");
        string moniker = $"net{m.Groups[1]}{m.Groups[2]}{m.Groups[3]}";   // net20, net35, net40, net472
        string dir = Path.Combine(PackagesFolder(), "microsoft.netframework.referenceassemblies." + moniker,
                                  PackageVersion, "build", ".NETFramework", version);
        if (!Directory.Exists(dir))
            Restore(moniker, workDir);
        if (!Directory.Exists(dir))
            throw new Exception($"no .NET Framework reference assemblies for {targetFrameworkId} at {dir} after a restore");
        return dir;
    }

    static string PackagesFolder() =>
        Environment.GetEnvironmentVariable("NUGET_PACKAGES") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");

    static void Restore(string moniker, string workDir)
    {
        string project = Path.Combine(workDir, "framework-references", moniker);
        Directory.CreateDirectory(project);
        string csproj = Path.Combine(project, "references.csproj");
        File.WriteAllText(csproj,
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n" +
            $"  <PropertyGroup><TargetFramework>{moniker}</TargetFramework></PropertyGroup>\n" +
            $"  <ItemGroup><PackageReference Include=\"Microsoft.NETFramework.ReferenceAssemblies\" Version=\"{PackageVersion}\" PrivateAssets=\"all\" /></ItemGroup>\n" +
            "</Project>\n");
        Log.Info($"  framework references: restoring Microsoft.NETFramework.ReferenceAssemblies.{moniker} {PackageVersion}");
        var (exitCode, output) = Shell.Capture("dotnet", "restore", csproj, "--nologo", "-v", "quiet");
        if (exitCode != 0)
            throw new Exception($"restoring the {moniker} reference assemblies failed (exit {exitCode}):\n{output}");
    }
}

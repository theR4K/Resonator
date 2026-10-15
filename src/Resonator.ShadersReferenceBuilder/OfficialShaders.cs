using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Resonator.ShadersReferenceBuilder;

/// <summary>One shader version listed in OfficialAssets.Shaders.</summary>
/// <param name="Version">"_" = current (direct members of Shaders), otherwise the dated class name ("_30_07_2023")</param>
public sealed record OfficialShader(string Version, string Shader, Uri Url)
{
    /// <summary>The asset signature: the resdb file name without extension (the file's SHA-256, hex).</summary>
    public string Signature => Path.GetFileNameWithoutExtension(Url.AbsolutePath.TrimStart('/'));
    public string FileName => Path.GetFileName(Url.AbsolutePath);
}

/// <summary>
/// The shader list of the shipped engine: FrooxEngine.dll's generated OfficialAssets.Shaders (every
/// property getter returns new Uri("resdb:///&lt;hash&gt;.unityshader")). Read with Cecil, so the list
/// always matches the installed engine build and the engine itself is never loaded.
/// </summary>
public static class OfficialShaders
{
    public const string CurrentVersion = "_";

    public static List<OfficialShader> Read(string frooxEngineDll)
    {
        using var assembly = AssemblyDefinition.ReadAssembly(frooxEngineDll);
        var root = assembly.MainModule.GetType("OfficialAssets") ?? throw new Exception("OfficialAssets not found in " + frooxEngineDll);
        var shaders = root.NestedTypes.FirstOrDefault(t => t.Name == "Shaders") ?? throw new Exception("OfficialAssets.Shaders not found in " + frooxEngineDll);

        var list = Collect(shaders, CurrentVersion).Concat(shaders.NestedTypes.SelectMany(v => Collect(v, v.Name)));
        return list.OrderBy(s => s.Version, StringComparer.Ordinal).ThenBy(s => s.Shader, StringComparer.Ordinal).ToList();
    }

    static IEnumerable<OfficialShader> Collect(TypeDefinition type, string version)
    {
        foreach (var property in type.Properties)
        {
            if (property.GetMethod is not { HasBody: true } getter || property.PropertyType.FullName != "System.Uri") continue;
            string? url = getter.Body.Instructions.Where(i => i.OpCode == OpCodes.Ldstr).Select(i => (string)i.Operand)
                .FirstOrDefault(s => s.Contains("://"));
            if (url != null) yield return new OfficialShader(version, property.Name, new Uri(url));
        }
    }
}

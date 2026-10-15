using System;
using System.IO;
using System.IO.Compression;

namespace Resonator.ShadersReferenceBuilder;

/// <summary>
/// A downloaded .unityshader asset is a zip (a ShaderUpload bundle):
///   metadata.json                         sourceFile {filename, hash}, includeFiles, variantGroups, uniqueKeywords
///   Sources/&lt;Shader&gt;.shader (+ includes)  the original ShaderLab/HLSL source
///   Variants/&lt;platform&gt;/&lt;variant&gt;         compiled Unity shader programs (left packed)
/// Unpacking writes metadata.json and Sources/** into a shader dir of the reference tree.
/// </summary>
public static class ShaderBundle
{
    public static bool IsBundle(string path) => path.EndsWith(".unityshader", StringComparison.OrdinalIgnoreCase);

    /// <summary>Returns the number of files written.</summary>
    public static int Unpack(string bundle, string destDir)
    {
        using var zip = ZipFile.OpenRead(bundle);
        string root = Path.GetFullPath(destDir) + Path.DirectorySeparatorChar;
        int written = 0;
        foreach (var entry in zip.Entries)
        {
            bool wanted = !entry.FullName.EndsWith('/')   // a directory entry
                && (entry.FullName == "metadata.json" || entry.FullName.StartsWith("Sources/", StringComparison.Ordinal));
            if (!wanted) continue;
            string dest = Path.GetFullPath(Path.Combine(destDir, entry.FullName));
            if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"{bundle}: entry escapes the shader dir: {entry.FullName}");
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            entry.ExtractToFile(dest, overwrite: true);
            written++;
        }
        if (!File.Exists(Path.Combine(destDir, "metadata.json")))
            throw new InvalidDataException($"{bundle}: no metadata.json - not a shader bundle");
        return written;
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace Resonator.ShadersReferenceBuilder;

/// <summary>
/// Downloads the official shader assets with the game's own cloud client (see <see cref="CloudGatherer"/>):
///
///   &lt;root&gt;/&lt;version&gt;/&lt;Shader&gt;/&lt;signature&gt;.unityshader
///
/// Anonymous: public assets need no login. Files already present with the right SHA-256 are
/// skipped, so a re-run fetches only what is missing; every download is verified against its
/// signature (the resdb name is the file's SHA-256).
/// </summary>
public static class ShaderDownloader
{
    public sealed record Result(int Present, int Downloaded, List<string> Failed);

    public static string PathFor(string root, OfficialShader s) => Path.Combine(root, s.Version, s.Shader, s.FileName);

    public static Result Run(string resoniteDir, IReadOnlyList<OfficialShader> shaders, string root, int parallelism, TextWriter log)
    {
        var missing = new List<OfficialShader>();
        foreach (var s in shaders)
        {
            string path = PathFor(root, s);
            if (!File.Exists(path) || Sha256(path) != s.Signature) missing.Add(s);
        }
        int present = shaders.Count - missing.Count;
        log.WriteLine($"shaders: {shaders.Count} listed, {present} present, {missing.Count} to download");
        if (missing.Count == 0) return new Result(present, 0, []);

        ResoniteAssemblies.ResolveFrom(resoniteDir);
        var (downloaded, failed) = CloudGatherer.Run(missing, root, parallelism, log).GetAwaiter().GetResult();
        return new Result(present, downloaded, failed);
    }

    internal static string Sha256(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
    }
}

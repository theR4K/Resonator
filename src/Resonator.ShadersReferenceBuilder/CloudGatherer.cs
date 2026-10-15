using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using SkyFrost.Base;

namespace Resonator.ShadersReferenceBuilder;

/// <summary>
/// The game's SkyFrost.Base AssetGatherer: resdb -> CDN resolution, retries, parallelism.
/// Kept apart from <see cref="ShaderDownloader"/> so no SkyFrost type is touched (JIT-resolved)
/// before <see cref="ResoniteAssemblies"/> can resolve it from the install.
/// </summary>
internal static class CloudGatherer
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static async Task<(int Downloaded, List<string> Failed)> Run(List<OfficialShader> shaders, string root, int parallelism, TextWriter log)
    {
        string tmp = Path.Combine(Path.GetTempPath(), "resonator", "gather");
        Directory.CreateDirectory(tmp);
        string version = typeof(CloudGatherer).Assembly.GetName().Version?.ToString() ?? "1.0";
        var cloud = new AssetOnlyCloud("Resonator-" + Guid.NewGuid().ToString("N"), SkyFrostConfig.DEFAULT_PRODUCTION.WithUserAgent("Resonator", version));
        var gatherer = new AssetGatherer<GatherJob>(cloud) { TemporaryPath = tmp };
        gatherer.SetCategoryParallelism(GatherJob.DEFAULT_CATEGORY, parallelism);

        var jobs = shaders.Select(s => (Shader: s, Job: gatherer.Gather(s.Url))).ToList();
        await Task.WhenAll(jobs.Select(j => j.Job.Task)).ConfigureAwait(false);

        int downloaded = 0;
        var failed = new List<string>();
        foreach (var (shader, job) in jobs)
        {
            string label = shader.Version + "/" + shader.Shader;
            if (job.State != GatherJobState.Finished || !File.Exists(job.FilePath))
            {
                failed.Add($"{label}: {job.StatusCode} {job.Error?.Split('\n')[0]}");
                continue;
            }
            string hash = ShaderDownloader.Sha256(job.FilePath);
            if (hash != shader.Signature)
            {
                File.Delete(job.FilePath);
                failed.Add($"{label}: hash mismatch ({hash})");
                continue;
            }
            string dest = ShaderDownloader.PathFor(root, shader);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Move(job.FilePath, dest, overwrite: true);
            downloaded++;
            log.WriteLine($"  downloaded {label} ({new FileInfo(dest).Length} B)");
        }
        return (downloaded, failed);
    }

    /// <summary>
    /// The cloud client for asset downloads only: the base constructor otherwise opens an anonymous
    /// SignalR hub connection ("Initial Startup") that a file download has no use for.
    /// </summary>
    sealed class AssetOnlyCloud(string uid, SkyFrostConfig config) : SkyFrostInterface(uid, null, config)
    {
        protected override Task<bool> RunInitialAnonymousHubConnection() => Task.FromResult(false);
    }
}

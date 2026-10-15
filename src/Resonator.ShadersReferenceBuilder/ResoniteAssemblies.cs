using System.IO;
using System.Runtime.Loader;

namespace Resonator.ShadersReferenceBuilder;

/// <summary>
/// The linked Resonite assemblies (SkyFrost.Base and its dependencies) are not shipped with this
/// tool: they resolve from the install at run time.
/// </summary>
internal static class ResoniteAssemblies
{
    static string? installDir;

    public static void ResolveFrom(string resoniteDir)
    {
        if (installDir != null) return;
        installDir = resoniteDir;
        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            string path = Path.Combine(installDir, name.Name + ".dll");
            return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
        };
    }
}

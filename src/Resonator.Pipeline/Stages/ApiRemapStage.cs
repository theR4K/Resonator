using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Resonator.Pipeline.Infrastructure;
using Resonator.Pipeline.Remapping;
using Resonator.Pipeline.Targets;
using Resonator.Weaver.Cecil;

namespace Resonator.Pipeline.Stages;

/// <summary>
/// Remaps original API symbols after automatic source fixes, before patch layers. Build and patch
/// baselines both enter here through FixupStage. Profiles are inherited independently of patches.
/// Binding uses the shipped API, not replacement packages; helper source is copied only after binding.
/// </summary>
public static class ApiRemapStage
{
    public static void Run(Target target)
    {
        if (target.Spec.Tree && target.Spec.ApiRemaps.Count > 0)
            throw new ArgumentException("API remapping requires a C# target: " + target.Name);
        foreach (string profile in target.Spec.ApiRemaps) Apply(target, profile);
    }

    static void Apply(Target target, string profile)
    {
        var map = ApiMap.Load(profile);
        var files = SourceTree.GeneratedFiles(target.SrcDir, target.Spec.KeepDirs, map.Files).OrderBy(p => p, StringComparer.Ordinal).ToArray();
        if (files.Length == 0) return;   // a split target may inherit a map for files it does not own
        Log.Banner("API REMAP " + Path.GetFileName(profile));
        var parse = new CSharpParseOptions(LanguageVersion.Preview, preprocessorSymbols: target.Spec.Defines);
        var supportFiles = map.SupportFiles.ToDictionary(s => SupportDestination(target, s.Destination),
            s => File.ReadAllText(Within(Path.GetDirectoryName(profile)!, s.Source)), StringComparer.OrdinalIgnoreCase);
        var trees = SourceTree.GeneratedFiles(target.SrcDir, target.Spec.KeepDirs).OrderBy(p => p, StringComparer.Ordinal)
            .Where(f => !supportFiles.ContainsKey(f))
            .Select(f => CSharpSyntaxTree.ParseText(File.ReadAllText(f), parse, f))
            .Concat(supportFiles.Select(kv => CSharpSyntaxTree.ParseText(kv.Value, parse, kv.Key))).ToArray();
        var selected = files.Where(f => !supportFiles.ContainsKey(f)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var compilation = CSharpCompilation.Create(target.Spec.OutName + ".ApiRemap", trees, References(target),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var first = new ApiRewriter(compilation.GetSemanticModel(trees[0]), map, target.Spec.OutName);
        first.ValidateRules();
        first.ValidateSignatures(trees.Where(t => selected.Contains(t.FilePath)));
        // Prepare every edit before writing, so a binding/collision error cannot leave half a profile applied.
        var writes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int changes = 0;
        foreach (var tree in trees)
        {
            if (!selected.Contains(tree.FilePath)) continue;
            var rewriter = new ApiRewriter(compilation.GetSemanticModel(tree), map, target.Spec.OutName);
            var root = tree.GetRoot();
            var rewritten = rewriter.Visit(root)!;
            if (rewriter.Changes == 0) continue;
            writes.Add(tree.FilePath, rewritten.ToFullString());
            changes += rewriter.Changes;
        }
        foreach (var (to, content) in supportFiles)
        {
            if (writes.ContainsKey(to)) throw new ArgumentException("API remap support file overwrites remapped input: " + to);
            writes.Add(to, content);
        }
        foreach (var (file, text) in writes)
        {
            if (File.Exists(file) && File.ReadAllText(file) == text) continue;
            bool bom = File.Exists(file) && File.ReadAllBytes(file).AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf });
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, text, new UTF8Encoding(bom));
        }
        Log.Info($"api-remap: {changes} symbol edit(s), {selected.Count} input file(s), {map.SupportFiles.Length} support file(s)");
    }

    static string Within(string root, string relative)
    {
        string path = Path.GetFullPath(Path.Combine(root, relative));
        if (Path.IsPathRooted(relative) || !path.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("API map path escapes its root: " + relative);
        return path;
    }

    static string SupportDestination(Target target, string relative)
    {
        string path = Within(target.SrcDir, relative);
        string firstDirectory = Path.GetRelativePath(target.SrcDir, path).Split('/', '\\')[0];
        if (target.Spec.KeepDirs.Any(d => d.TrimEnd('/', '\\').Equals(firstDirectory, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("API map support file targets a protected keep directory: " + relative);
        if (SourceTree.IsBuildOutput(target.SrcDir, path) || !Path.GetExtension(path).Equals(".cs", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("API map support destination must be a generated C# source file: " + relative);
        return path;
    }

    static IEnumerable<MetadataReference> References(Target target)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            files.TryAdd(Path.GetFileNameWithoutExtension(file), file);
        // Follow original assembly references to avoid loading both an original wrapper and its replacement
        // (AssimpNet and AssimpNetter expose identical type names). The target itself is bound from source:
        // referencing it as well would introduce duplicate extension methods.
        var queue = new Queue<string>();
        if (File.Exists(target.ShippedDll)) queue.Enqueue(target.ShippedDll);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (queue.Count > 0)
        {
            string file = queue.Dequeue();
            if (!visited.Add(file)) continue;
            if (!file.Equals(target.ShippedDll, StringComparison.OrdinalIgnoreCase))
                files.TryAdd(Path.GetFileNameWithoutExtension(file), file);
            foreach (string name in AssemblyFile.ReferencedAssemblies(file))
            {
                if (files.ContainsKey(name)) continue;
                string dependency = Path.Combine(target.Settings.ResoniteDir, name + ".dll");
                if (File.Exists(dependency)) queue.Enqueue(dependency);
            }
        }
        return files.Values.Select(p => MetadataReference.CreateFromFile(p));
    }
}

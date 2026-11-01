using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Resonator.Pipeline;
using Resonator.Pipeline.Remapping;
using Resonator.Pipeline.Stages;
using Resonator.Pipeline.Targets;

/// <summary>Reject mappings that discard receivers or overwrite consumer-owned source.</summary>
internal static class RemapReviewChecks
{
    public static void Run()
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(p => MetadataReference.CreateFromFile(p)).ToArray();
        using var image = new MemoryStream();
        var oldTree = CSharpSyntaxTree.ParseText("""
        namespace Legacy {
          public class Value { public int Touch() => 1; }
          public static class Functions { public static int Make() => 1; }
        }
        """);
        var emitted = CSharpCompilation.Create("ReviewLegacy", [oldTree], references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)).Emit(image);
        Assert(emitted.Success, "fixture did not compile");
        var metadata = MetadataReference.CreateFromImage(image.ToArray());
        string destinations = "namespace Destination { public static class Functions { public static int Make()=>1; public static int Touch()=>1; } }";
        var tree = CSharpSyntaxTree.ParseText("using static Legacy.Functions; class C { System.Func<int> group = Make; int Call()=> Make(); }" + destinations);
        var compilation = CSharpCompilation.Create("ReviewFixture", [tree], [..references, metadata], new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var callMap = new ApiMap { Members = [new("ReviewLegacy", "M:Legacy.Functions.Make", "global::Destination.Functions.Make") { Kind = "call" }] };
        Fails(() => new ApiRewriter(compilation.GetSemanticModel(tree), callMap, "ReviewFixture").Visit(tree.GetRoot()), "method group");
        var droppedReceiver = new ApiMap { Members = [new("ReviewLegacy", "M:Legacy.Value.Touch", "global::Destination.Functions.Touch") { Kind = "call" }] };
        Fails(() => new ApiRewriter(compilation.GetSemanticModel(tree), droppedReceiver, "ReviewFixture").ValidateRules(), "receiver rule");
        var staticReceiver = new ApiMap { Members = [callMap.Members[0] with { Receiver = "value" }] };
        Fails(() => new ApiRewriter(compilation.GetSemanticModel(tree), staticReceiver, "ReviewFixture").ValidateRules(), "receiver rule");
        Console.WriteLine("PASS call adapters reject discarded receivers and unqualified method groups");

        string temp = Path.Combine(Path.GetTempPath(), "remap-owned-source-" + Guid.NewGuid().ToString("N"));
        string targetDirectory = Path.Combine(temp, "targets", "Demo");
        string output = Path.Combine(temp, "output");
        Directory.CreateDirectory(targetDirectory);
        Directory.CreateDirectory(Path.Combine(output, "Injected", "Nested"));
        File.WriteAllText(Path.Combine(targetDirectory, "target.json"), """{"assembly":"Demo.dll","weave":"none"}""");
        File.WriteAllText(Path.Combine(targetDirectory, "Helper.cs"), "public class Support {}\n");
        File.WriteAllText(Path.Combine(output, "Input.cs"), "public class Input {}\n");
        string owned = Path.Combine(output, "Injected", "Nested", "Owned.cs");
        File.WriteAllText(owned, "// authored consumer code\n");
        string profile = Path.Combine(targetDirectory, "map.json");
        var settings = new PipelineSettings { Set = new TargetSet(Path.Combine(temp, "targets")), OutputDir = output, ResoniteDir = Path.Combine(temp, "no-game"), NoCsproj = true };
        var target = Target.InRun(settings, "Demo", 1);
        target = target with { Spec = target.Spec with { ApiRemaps = [profile] } };
        foreach (string destination in new[] { "Injected/Nested/Owned.cs", "obj/Generated.cs", "references.targets", "../Escape.cs" })
        {
            WriteProfile(destination);
            Fails(() => ApiRemapStage.Run(target), null);
            Assert(File.ReadAllText(owned) == "// authored consumer code\n", "consumer source overwritten");
        }
        WriteProfile("Support.cs");
        ApiRemapStage.Run(target);
        string support = Path.Combine(output, "Support.cs");
        DateTime before = File.GetLastWriteTimeUtc(support);
        ApiRemapStage.Run(target);
        Assert(File.GetLastWriteTimeUtc(support) == before, "repeat remap rewrote unchanged support file");
        Console.WriteLine("PASS support files preserve keep directories, build metadata and repeat-run timestamps");

        void WriteProfile(string destination) => File.WriteAllText(profile, JsonSerializer.Serialize(new
        {
            support_files = new[] { new { source = "Helper.cs", destination } }
        }));
    }

    static void Fails(Action action, string? expected)
    {
        try { action(); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        { if (expected == null || ex.Message.Contains(expected)) return; throw; }
        throw new Exception("Expected rejected mapping: " + expected);
    }
    static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
}

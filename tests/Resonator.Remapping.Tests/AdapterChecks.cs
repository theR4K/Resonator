using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Concentus;
using Concentus.Enums;
using Flatweave.Adapters;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Resonator;
using Resonator.Pipeline;
using Resonator.Pipeline.Stages;
using Resonator.Pipeline.Targets;
using Resonator.Weaver.Flattening;

/// <summary>Runtime adapter contracts, shared marker identity, local sources and dependency ordering.</summary>
internal static class AdapterChecks
{
    public static void Run()
    {
        string temp = Path.Combine(Path.GetTempPath(), "flatweave-adapter-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        Marker(temp);
        Codec();
        LocalAndDependencies(temp);
        Assert(typeof(FlattenedInitOnlyAttribute).Assembly.GetName().Name == "Flatweave.Adapter", "marker not in shared assembly");
        Assert(!typeof(AssimpMath).Assembly.GetReferencedAssemblies().Any(a => a.Name!.StartsWith("FrooxEngine") || a.Name.StartsWith("Elements.") || a.Name == "POpusCodec"), "adapter depends on game assemblies");
        Console.WriteLine("PASS standalone adapter dependency boundary");
    }

    static void Marker(string temp)
    {
        string original = Path.Combine(temp, "MarkerFixture.dll");
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Select(p => MetadataReference.CreateFromFile(p));
        var source = CSharpSyntaxTree.ParseText("""
        [module: System.ComponentModel.Description("FROOXENGINE_WEAVED")]
        namespace FrooxEngine {
          public interface ISyncMember {}
          public class Sync : ISyncMember {}
          public class Worker {}
          public class Fixture : Worker { public readonly Sync Value = new(); public readonly int Constant=1; public int Ordinary; }
        }
        """);
        using (var stream = File.Create(original))
        {
            var result = CSharpCompilation.Create("MarkerFixture", [source], references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)).Emit(stream);
            Assert(result.Success, string.Join("\n", result.Diagnostics));
        }
        string output = Path.Combine(temp, "flattened");
        var report = Flattener.Flatten(original, output, blankAotDummy: false);
        Assert(report.Error == null && report.ClearedInitOnly == 1, "fixture flatten failed: " + report);
        var assembly = Assembly.Load(File.ReadAllBytes(Path.Combine(output, "MarkerFixture.dll")));
        Assert(assembly.GetType(Flattener.InitOnlyMarker) == null, "flattener embedded its own marker");
        var fixture = assembly.GetType("FrooxEngine.Fixture")!;
        var marked = fixture.GetField("Value")!;
        Assert(!marked.IsInitOnly && marked.GetCustomAttribute<FlattenedInitOnlyAttribute>() != null && marked.IsInitOnlyOrFlattened(), "external marker/reflection mismatch");
        Assert(fixture.GetField("Constant")!.IsInitOnlyOrFlattened(), "ordinary readonly field not recognized");
        Assert(!fixture.GetField("Ordinary")!.IsInitOnlyOrFlattened(), "ordinary writable field recognized");
        Console.WriteLine("PASS flattener references shared marker and reflection preserves readonly semantics");
    }

    static void Codec()
    {
        OpusCodecFactory.AttemptToUseNativeLibrary = false;
        foreach (int channels in new[] { 1, 2 })
        foreach (int frames in new[] { 480, 960 })
        {
            using var adapter = new Flatweave.Adapters.OpusEncoder(48000, channels, 25000, (int)OpusApplication.OPUS_APPLICATION_VOIP);
            using var direct = OpusCodecFactory.CreateEncoder(48000, channels, OpusApplication.OPUS_APPLICATION_VOIP);
            direct.Bitrate = 25000;
            adapter.Complexity = direct.Complexity = 2;
            adapter.SignalHint = (int)(direct.SignalType = OpusSignal.OPUS_SIGNAL_VOICE);
            adapter.EncoderDelay = 1; // this setting must not change the supplied float frame size
            var pcm = Enumerable.Range(0, frames * channels).Select(i => (float)(.2 * Math.Sin(i * .07))).ToArray();
            var actualPacket = new byte[4000]; var expectedPacket = new byte[4000];
            int size = adapter.Encode(pcm, frames, actualPacket), expectedSize = direct.Encode(pcm, frames, expectedPacket, expectedPacket.Length);
            Assert(size == (expectedSize <= 1 ? 0 : expectedSize) && actualPacket.AsSpan(0, expectedSize).SequenceEqual(expectedPacket.AsSpan(0, expectedSize)), "encoder contract changed");
            using var decoder = new Flatweave.Adapters.OpusDecoder(48000, channels);
            using var reference = OpusCodecFactory.CreateDecoder(48000, channels);
            var actual = new float[frames * channels]; var expected = new float[frames * channels];
            Assert(decoder.DecodePacketFloat(actualPacket, size, actual) == reference.Decode(actualPacket.AsSpan(0, size), expected, frames), "decoder count differs");
            Samples(actual, expected);
            Assert(decoder.DecodePacketLostFloat(actual, frames * channels) == reference.Decode(ReadOnlySpan<byte>.Empty, expected, frames), "loss count is not interleaved");
            Samples(actual, expected);
            Assert(decoder.DecodePacketFloat(actualPacket, size, actual) == reference.Decode(actualPacket.AsSpan(0, size), expected, frames, true), "post-loss FEC differs");
            Samples(actual, expected);
            byte[] invalid = [255];
            int expectedLost;
            try { expectedLost = reference.Decode(invalid, expected, frames); }
            catch (OpusException) { expectedLost = reference.Decode(ReadOnlySpan<byte>.Empty, expected, frames); }
            Assert(decoder.DecodePacketFloat(invalid, invalid.Length, actual) == expectedLost, "invalid packet concealment differs");
            Samples(actual, expected);
        }
        Console.WriteLine("PASS Opus mono/stereo encode, decode, settings, loss/FEC and invalid packets");
    }

    static void LocalAndDependencies(string temp)
    {
        string set = Path.Combine(temp, "targets");
        string local = Path.Combine(temp, "local");
        Directory.CreateDirectory(local);
        File.WriteAllText(Path.Combine(local, "Helper.cs"), "public class Helper {}\n");
        Directory.CreateDirectory(Path.Combine(local, "obj"));
        File.WriteAllText(Path.Combine(local, "obj", "Ignore.cs"), "invalid");
        void Spec(string name, string text) { Directory.CreateDirectory(Path.Combine(set, name)); File.WriteAllText(Path.Combine(set, name, "target.json"), text); }
        Spec("Engine", """{"assembly":"Engine.dll","refs":["A"]}""");
        Spec("A", """{"assembly":"A.dll","source":{"local":"../../local"},"refs":["Z"]}""");
        Spec("Z", """{"assembly":"Z.dll","source":{"local":"../../local"}}""");
        var settings = new PipelineSettings { Set = new TargetSet(set), OutputDir = Path.Combine(temp, "generated"), ResoniteDir = Path.Combine(temp, "no-game") };
        Assert(ReferencesTargets.WithSourceTargets(settings, ["Engine"]).SequenceEqual(new[] { "Z", "A", "Engine" }), "source dependencies not topologically ordered");
        var target = Target.InRun(settings, "A", 1);
        DecompileStage.Run(target);
        Assert(File.Exists(Path.Combine(target.SrcDir, "Helper.cs")) && !Directory.Exists(Path.Combine(target.SrcDir, "obj")), "local source includes build output");
        File.WriteAllText(Path.Combine(local, "Helper.cs"), "public class EditedHelper {}\n");
        DecompileStage.Run(target);
        Assert(File.ReadAllText(Path.Combine(target.SrcDir, "Helper.cs")).Contains("EditedHelper"), "local edits hidden by source cache");
        bool rejected = false;
        try { DecompileStage.Run(target with { ProjectDir = Path.Combine(local, "nested") }); } catch (ArgumentException) { rejected = true; }
        Assert(rejected, "overlapping local output accepted");
        Spec("Z", """{"assembly":"Z.dll","source":{"local":"../../local"},"refs":["A"]}""");
        rejected = false;
        try { ReferencesTargets.WithSourceTargets(settings with { Set = new TargetSet(set) }, ["Engine"]); } catch (ArgumentException ex) when (ex.Message.Contains("cycle")) { rejected = true; }
        Assert(rejected, "dependency cycle accepted");
        Console.WriteLine("PASS local source edits/exclusions/overlap and dependency order/cycles");
    }

    static void Samples(float[] actual, float[] expected)
    {
        for (int i=0;i<actual.Length;i++) Assert(float.IsFinite(actual[i]) && actual[i] == expected[i], "decoded PCM differs");
    }
    static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
}

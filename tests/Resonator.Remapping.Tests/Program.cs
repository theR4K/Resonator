// Regression checks for symbol identity, evaluation order, collisions, adapters and baseline inheritance.
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Resonator.Pipeline.Remapping;
using Resonator.Pipeline.Targets;

var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
    .Select(p => MetadataReference.CreateFromFile(p)).ToArray();
var legacyTree = CSharpSyntaxTree.ParseText("""
namespace Legacy {
 public struct Vec { public float X; public static Vec operator *(Vec a,Vec b) => new Vec { X=a.X*10+b.X }; }
 public struct Color { public float R; }
 public struct Rotation { public float X,Y,Z,W; public Rotation(float w,float x,float y,float z) { X=x;Y=y;Z=z;W=w; } }
 public static class Math { public static Vec Scale(Vec v) => v; }
}
""");
using var image = new MemoryStream();
var emitted = CSharpCompilation.Create("LegacyApi", [legacyTree], references,
    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)).Emit(image);
Assert(emitted.Success, string.Join("\n", emitted.Diagnostics));
var legacy = MetadataReference.CreateFromImage(image.ToArray());
var map = new ApiMap
{
    Types = [new("LegacyApi", "Legacy.Vec", "Modern.Vec"), new("LegacyApi", "Legacy.Color", "Modern.Vec"), new("LegacyApi", "Legacy.Rotation", "Modern.Rotation")],
    Members = [
        new("LegacyApi", "F:Legacy.Color.R", "X"),
        new("LegacyApi", "M:Legacy.Rotation.#ctor(System.Single,System.Single,System.Single,System.Single)", "global::Modern.Rotation") { Kind = "constructor", ArgumentNames = ["w", "x", "y", "z"] },
        new("LegacyApi", "M:Legacy.Math.Scale(Legacy.Vec)", "global::Modern.Math.Scale") { Kind = "call" },
        new("LegacyApi", "M:Legacy.Vec.op_Multiply(Legacy.Vec,Legacy.Vec)", "global::Modern.Math.Multiply") { Kind = "operator" },
        new("$target", "M:Demo.F(Legacy.Color)", "FromColor") { Kind = "rename" },
    ],
};
string source = """
using Alias = Legacy.Vec;
namespace Modern {
 public struct Vec { public float X; }
 public struct Rotation { public float X,Y,Z,W; public Rotation(float x,float y,float z,float w) { X=x;Y=y;Z=z;W=w; } }
 public static class Math { public static Vec Scale(Vec v) => v; public static Vec Multiply(Vec a,Vec b) => new Vec { X=a.X*10+b.X }; }
}
namespace Other { public struct Vec { public int X; } }
public class ConversionOverloads {
 public static implicit operator int(ConversionOverloads value) => 1;
 public static implicit operator float(ConversionOverloads value) => 1;
}
public static class Demo {
 static int counter;
 static float Next() => ++counter;
 static float F(Legacy.Vec v) => v.X;
 static float F(Legacy.Color v) => v.R;
 // Legacy.Vec must stay in this comment and string.
 public static string Text = "Legacy.Vec";
 public static System.Collections.Generic.List<Alias[]> Values;
 public static Other.Vec Untouched;
 public static float Check() {
   counter=0;
   Legacy.Rotation q = new(Next(),Next(),Next(),Next());
   var c = new Legacy.Color { R=7 };
   var s = Legacy.Math.Scale(new Legacy.Vec { X=2 });
   var a = new Legacy.Vec { X=2 }; var b = new Legacy.Vec { X=3 }; a *= b; var product = a*b;
   return q.W*1000 + q.X*100 + q.Y*10 + q.Z + F(c) + s.X + product.X;
 }
}
""";
var tree = CSharpSyntaxTree.ParseText(source);
var compilation = CSharpCompilation.Create("Fixture", [tree], [..references, legacy],
    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
Assert(!compilation.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error), string.Join("\n", compilation.GetDiagnostics()));
var rewriter = new ApiRewriter(compilation.GetSemanticModel(tree), map, "Fixture");
rewriter.ValidateRules();
rewriter.ValidateSignatures([tree]);
string mapped = rewriter.Visit(tree.GetRoot())!.ToFullString();
Assert(mapped.Contains("Other.Vec Untouched"), "unrelated type was changed");
Assert(mapped.Contains("\"Legacy.Vec\""), "string was changed");
Assert(mapped.Contains("FromColor(c)"), "unqualified source call was not renamed");
Assert(mapped.Contains("global::Modern.Math.Scale"), "static call not mapped");
Assert(mapped.Contains("using Alias = global::Modern.Vec"), "alias not mapped");
using var mappedImage = new MemoryStream();
var mappedCompilation = CSharpCompilation.Create("MappedFixture", [CSharpSyntaxTree.ParseText(mapped)], references,
    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
var result = mappedCompilation.Emit(mappedImage);
Assert(result.Success, string.Join("\n", result.Diagnostics) + "\n" + mapped);
var loaded = Assembly.Load(mappedImage.ToArray());
Assert((float)loaded.GetType("Demo")!.GetMethod("Check")!.Invoke(null, null)! == 1476, "constructor/operator values or expression evaluation order changed");
var againTree = mappedCompilation.SyntaxTrees.Single();
var again = new ApiRewriter(mappedCompilation.GetSemanticModel(againTree), map, "Fixture");
Assert(again.Visit(againTree.GetRoot())!.ToFullString() == mapped && again.Changes == 0, "mapping is not idempotent");
Console.WriteLine("PASS symbols, aliases, arrays/generics, field/call/rename mappings, constructor evaluation order, idempotence");

var collisionMap = map with { Members = map.Members.Where(m => m.Kind != "rename").ToArray() };
ExpectFailure(() => new ApiRewriter(compilation.GetSemanticModel(tree), collisionMap, "Fixture").ValidateSignatures([tree]), "signature collision");
var typo = map with { Members = [new("LegacyApi", "F:Legacy.Color.NotAField", "X")] };
ExpectFailure(() => new ApiRewriter(compilation.GetSemanticModel(tree), typo, "Fixture").ValidateRules(), "source member missing");
// The same full type name in a different assembly must not match a configured type.
var wrongAssembly = map with { Types = map.Types.Select(t => t with { Assembly = "UnrelatedApi" }).ToArray(), Members = [] };
var untouched = new ApiRewriter(compilation.GetSemanticModel(tree), wrongAssembly, "Fixture");
Assert(untouched.Visit(tree.GetRoot())!.ToFullString() == source, "assembly identity ignored");
Console.WriteLine("PASS collisions and assembly identity");

var unsafeTree = CSharpSyntaxTree.ParseText("public class UnsafeReceiver { Legacy.Vec[] data = new Legacy.Vec[2]; int i; void Bad() { data[i++] *= data[0]; } }");
var unsafeCompilation = CSharpCompilation.Create("UnsafeFixture", [unsafeTree], [..references, legacy], new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
ExpectFailure(() => new ApiRewriter(unsafeCompilation.GetSemanticModel(unsafeTree), map, "UnsafeFixture").Visit(unsafeTree.GetRoot()), "local or parameter lvalue");
Console.WriteLine("PASS compound operator rejects lvalues requiring temporaries");

string temp = Path.Combine(Path.GetTempPath(), "resonator-remap-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(Path.Combine(temp, "Base", "Demo"));
Directory.CreateDirectory(Path.Combine(temp, "Child", "Demo"));
File.WriteAllText(Path.Combine(temp, "Base", "Demo", "target.json"), """{"assembly":"Demo.dll","api_remaps":["base.json"]}""");
File.WriteAllText(Path.Combine(temp, "Child", "Demo", "target.json"), """{"extends":"Base/Demo","api_remaps":["child.json"]}""");
var spec = TargetSpecLoader.Load(Path.Combine(temp, "Child", "Demo"));
Assert(spec.ApiRemaps.Count == 2 && spec.ApiRemaps[0].EndsWith("base.json") && spec.ApiRemaps[1].EndsWith("child.json"), "mapping inheritance");
Assert((spec with { PatchDirs = spec.PatchDirs.SkipLast(1).ToArray() }).ApiRemaps.Count == 2, "patch baseline lost own mapping");
Console.WriteLine("PASS target inheritance and patch baseline mapping retention");
RemapReviewChecks.Run();
AdapterChecks.Run();
#if HAS_ASSIMP
AssimpCompatibilityTests.Run();
#else
Console.WriteLine("SKIP Assimp differential tests: install not supplied via ResoniteDir");
#endif

static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
static void ExpectFailure(Action action, string text)
{
    try { action(); } catch (InvalidOperationException ex) when (ex.Message.Contains(text)) { return; }
    throw new Exception("Expected failure: " + text);
}

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Resonator.Pipeline.Fixups;
using Resonator.Pipeline.Targets;

static class UpgradeFixupChecks
{
    public static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "resonator-upgrade-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "Fixture.cs");
            const string source = """"
                using System;
                using System.Runtime.ExceptionServices;
                public static class Fixture
                {
                    public const string Literal = "(value as Exception) ?? throw value";
                    public const string Raw = """(value as Exception) ?? throw value""";
                    // (value as Exception) ?? throw value
                    /* (value as Exception) ?? throw value */
                    public static Exception Capture(object value) =>
                        ExceptionDispatchInfo.Capture((value as Exception) ?? throw /* keep */ value).SourceException;
                    public static Exception Escaped(object @event) =>
                        (@event as Exception) ?? throw @event;
                    public static Exception Different(object value, Exception valueOther) =>
                        (value as Exception) ?? throw valueOther;
                    public static InvalidOperationException OtherType(InvalidOperationException value) =>
                        (value as InvalidOperationException) ?? throw value;
                }
                """";
            File.WriteAllText(path, source);
            var fix = new ThrowExprCastFix();
            Check(fix.Apply([path]) == 2, "throw fix must match only the two rethrow sites");
            string expected = source.Replace("throw /* keep */ value", "throw /* keep */ (Exception)value")
                .Replace("?? throw @event;", "?? throw (Exception)@event;");
            string actual = File.ReadAllText(path);
            Check(actual == expected, "throw fix changed a literal, comment, unrelated operand or formatting");
            Check(fix.Apply([path]) == 0 && File.ReadAllText(path) == actual, "throw fix is not idempotent");

            var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                .Select(p => MetadataReference.CreateFromFile(p));
            var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary);
            var broken = CSharpCompilation.Create("BrokenThrowFixture", [CSharpSyntaxTree.ParseText(source)], references, options);
            Check(broken.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error), "fixture no longer reproduces the compiler failure");
            using var image = new MemoryStream();
            var result = CSharpCompilation.Create("FixedThrowFixture", [CSharpSyntaxTree.ParseText(actual)], references, options).Emit(image);
            Check(result.Success, "fixed rethrow does not compile: " + string.Join("\n", result.Diagnostics));
            var fixture = Assembly.Load(image.ToArray()).GetType("Fixture")!;
            var exception = new InvalidOperationException("original exception");
            Check(ReferenceEquals(exception, fixture.GetMethod("Capture")!.Invoke(null, [exception])), "exception identity changed");
            Check((string)fixture.GetField("Literal")!.GetRawConstantValue()! == "(value as Exception) ?? throw value", "literal value changed");
            Check((string)fixture.GetField("Raw")!.GetRawConstantValue()! == "(value as Exception) ?? throw value", "raw literal value changed");
            Console.WriteLine("PASS throw fix compilation, exception identity, trivia/literal preservation and idempotence");

            string target = Path.Combine(directory, "Target");
            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(target, "target.json"), """{"assembly":"Demo.dll"}""");
            Check(new TargetSpec { Name = "Demo", Dir = target, Assembly = "Demo.dll" }.Tfm == "net11.0" && TargetSpecLoader.Load(target).Tfm == "net11.0", "default target framework must be .NET 11");
            File.WriteAllText(Path.Combine(target, "target.json"), """{"assembly":"Demo.dll","tfm":"net10.0"}""");
            Check(TargetSpecLoader.Load(target).Tfm == "net10.0", "explicit framework override was lost");
            Console.WriteLine("PASS .NET 11 target defaults and explicit framework override");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}

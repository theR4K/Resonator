using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Resonator.Pipeline;
using Resonator.Pipeline.Stages;
using Resonator.Pipeline.Targets;
using Resonator.Weaver.Checks;

static class RuntimeAsyncChecks
{
    public static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "resonator-runtime-async-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "set", "Fixture"));
        File.WriteAllText(Path.Combine(root, "set", "Fixture", "target.json"), """{"assembly":"Fixture.dll"}""");
        File.WriteAllText(Path.Combine(root, "Directory.Build.props"), "<Project />");
        File.WriteAllText(Path.Combine(root, "Directory.Build.targets"), "<Project />");
        try
        {
            string baseline = Build("baseline", generatedSettings: false);
            string corrected = Build("corrected", generatedSettings: true);
            using var baselineCheck = new StringWriter();
            Check(RuntimeAsyncCheck.Run(baseline, baselineCheck) == 1 && baselineCheck.ToString().Contains("MoveNext"), "checker must reject helpers inside state-machine methods");
            using var correctedCheck = new StringWriter();
            Check(RuntimeAsyncCheck.Run(corrected, correctedCheck) == 0 && !correctedCheck.ToString().Contains("0 intrinsic"), "checker must accept the runtime-async fixture and inspect its calls");
            var failed = Execute("dotnet", baseline);
            Check(failed.Code == 1 && failed.Output.Contains("NullReferenceException"), "control must reproduce the game's runtime exception: " + failed.Output);
            var passed = Execute("dotnet", corrected);
            Check(passed.Code == 0 && passed.Output.Contains("PASS runtime await behavior"), "generated compiler settings broke custom awaits: " + passed.Output);
            Console.WriteLine("PASS runtime-async generated settings, invalid-caller detection, delayed/synchronous struct awaits, value results, exception identity and cancellation");

            string Build(string name, bool generatedSettings)
            {
                string dir = Path.Combine(root, name);
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "Fixture.cs"), Fixture);
                File.WriteAllText(Path.Combine(dir, "Fixture.csproj"), """
                    <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>
                    <TargetFramework>net11.0</TargetFramework><OutputType>Exe</OutputType><LangVersion>13.0</LangVersion>
                    <GenerateAssemblyInfo>false</GenerateAssemblyInfo><UseAppHost>false</UseAppHost>
                    </PropertyGroup><Import Project="references.targets" Condition="Exists('references.targets')" /></Project>
                    """);
                if (generatedSettings)
                {
                    var set = new TargetSet(Path.Combine(root, "set"));
                    var settings = new PipelineSettings { OutputDir = dir, Set = set, ResoniteDir = root };
                    ReferencesTargets.Write(new Target(settings, set.Spec("Fixture"), dir));
                    var features = XDocument.Load(Path.Combine(dir, "references.targets")).Descendants("Features").Single();
                    Check(features.Value == "$(Features);runtime-async=on" && (string?)features.Attribute("Condition") == "'$(TargetFramework)' == 'net11.0'", "preserve existing features and exclude older target frameworks");
                }
                var result = Execute("dotnet", "build", Path.Combine(dir, "Fixture.csproj"), "-c", "Release", "-nodeReuse:false", "-v:quiet");
                Check(result.Code == 0, result.Output);
                return Path.Combine(dir, "bin", "Release", "net11.0", "Fixture.dll");
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    static (int Code, string Output) Execute(string executable, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60000)) { process.Kill(entireProcessTree: true); throw new Exception("runtime-async fixture timed out"); }
        return (process.ExitCode, stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
    }

    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    const string Fixture = """
        using System;
        using System.Runtime.CompilerServices;
        using System.Threading;
        using System.Threading.Tasks;
        public sealed class State { public bool Complete; public int Registrations; public int Results; public Exception Error; }
        public readonly struct Awaiter : INotifyCompletion {
            readonly State state;
            public Awaiter(State state) { this.state = state; }
            public bool IsCompleted => state.Complete;
            public void OnCompleted(Action continuation) {
                var current = state;
                current.Registrations++;
                ThreadPool.QueueUserWorkItem(_ => { Thread.Sleep(30); current.Complete = true; continuation(); });
            }
            public int GetResult() {
                state.Results++;
                if (!state.Complete) throw new InvalidOperationException("resumed before completion");
                if (state.Error != null) throw state.Error;
                return 42;
            }
        }
        public static class Program {
            public static async Task<int> Raw(State state) {
                var awaiter = new Awaiter(state);
                if (!awaiter.IsCompleted) AsyncHelpers.AwaitAwaiter(awaiter);
                return awaiter.GetResult();
            }
            public static async Task VoidRaw(State state) {
                var awaiter = new Awaiter(state);
                if (!awaiter.IsCompleted) AsyncHelpers.AwaitAwaiter(awaiter);
                awaiter.GetResult();
            }
            public static async Task<int> Main() {
                try {
                    var delayed = new State();
                    int result = await Raw(delayed);
                    if (result != 42 || delayed.Registrations != 1 || delayed.Results != 1) throw new Exception("delayed await");
                    var complete = new State { Complete = true };
                    if (await Raw(complete) != 42 || complete.Registrations != 0 || complete.Results != 1) throw new Exception("completed await");
                    var empty = new State();
                    await VoidRaw(empty);
                    if (empty.Registrations != 1 || empty.Results != 1) throw new Exception("void await");
                    var original = new InvalidOperationException("original error");
                    try { await Raw(new State { Error = original }); throw new Exception("missing exception"); }
                    catch (InvalidOperationException error) when (ReferenceEquals(error, original)) { }
                    using var cancellation = new CancellationTokenSource();
                    cancellation.Cancel();
                    Task<int> canceled = Raw(new State { Error = new OperationCanceledException(cancellation.Token) });
                    try { await canceled; throw new Exception("missing cancellation"); }
                    catch (OperationCanceledException error) when (error.CancellationToken == cancellation.Token) { }
                    if (!canceled.IsCanceled) throw new Exception("task cancellation state");
                    if (((int)typeof(Program).GetMethod("Raw").GetMethodImplementationFlags() & 0x2000) == 0) throw new Exception("missing runtime-async metadata");
                    Console.WriteLine("PASS runtime await behavior");
                    return 0;
                } catch (Exception error) { Console.WriteLine(error); return 1; }
            }
        }
        """;
}

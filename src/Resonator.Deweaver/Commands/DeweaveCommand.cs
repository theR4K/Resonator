using System;
using System.Collections.Generic;
using System.IO;
using Resonator.Weaver.Deweaving;

namespace Resonator.Deweaver.Commands;

/// <summary><c>Resonator.Deweaver [options] &lt;file-or-dir&gt;...</c> - revert every weaved dll given (directories: their *.dll).</summary>
internal static class DeweaveCommand
{
    public static int Run(string[] args)
    {
        var (options, inputs) = Parse(args);
        var files = new List<string>();
        foreach (string input in inputs)
        {
            if (Directory.Exists(input)) files.AddRange(Directory.EnumerateFiles(input, "*.dll", SearchOption.TopDirectoryOnly));
            else if (File.Exists(input)) files.Add(input);
            else Console.Error.WriteLine("not found: " + input);
        }

        int errors = 0, reverted = 0, notWeaved = 0, notDotNet = 0, locked = 0;
        foreach (string file in files)
        {
            var report = Weaver.Deweaving.Deweaver.Deweave(file, options);
            Console.WriteLine(report);
            if (report.Error != null) errors++;
            else if (report.NotDotNet) notDotNet++;
            else if (report.Locked) locked++;
            else if (report.WasWeaved && report.Modified) reverted++;
            else notWeaved++;
        }

        Console.WriteLine();
        Console.WriteLine($"summary: {files.Count} file(s), {reverted} reverted, {notWeaved} not-weaved, " +
                          $"{notDotNet} non-.NET, {locked} locked, {errors} error(s)" +
                          (options.DryRun ? "  [DRY RUN - nothing written]" : ""));
        return errors == 0 ? 0 : 1;
    }

    static (DeweaveOptions Options, List<string> Inputs) Parse(string[] args)
    {
        var options = new DeweaveOptions();
        var inputs = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            string Value() => ++i < args.Length ? args[i] : throw new UsageException(args[i - 1] + " needs a value");
            switch (args[i])
            {
                case "--out": options = options with { OutputDir = Value() }; break;
                case "--dry-run": options = options with { DryRun = true }; break;
                case "--no-blank-aot": options = options with { BlankAotDummy = false }; break;
                case "--empty-methods": options = options with { MethodMode = MethodMode.Empty }; break;
                case "--keep-version": options = options with { ResetVersion = false }; break;
                case "--version": options = options with { VersionReset = Value() }; break;
                case "--backup": options = options with { KeepBackup = true }; break;
                case var option when option.StartsWith('-'): throw new UsageException("unknown option " + option);
                default: inputs.Add(args[i]); break;
            }
        }
        if (inputs.Count == 0) throw new UsageException("no input files/directories given");
        return (options, inputs);
    }
}

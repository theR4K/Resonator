// Resonator.Deweaver - the command line over the Resonator.Weaver library:
// revert the weave, check the result, and the round-trip tools that prove it exact.

using System;
using System.IO;
using System.Linq;
using Resonator.Deweaver.Commands;
using Resonator.Weaver;
using Resonator.Weaver.Checks;

namespace Resonator.Deweaver;

internal static class Program
{
    const string Usage = """
        Resonator.Deweaver [options] <file-or-dir>...    revert the weave (see options below)
        Resonator.Deweaver <command> ...

        commands:
          verify <dll>                   residue check: no weaver traces left
          known <weaver.dll>             is this a KNOWN FrooxEngine.Weaver?
          syncmembers <dll>              generated GetSyncMember consistency (Flatweave builds, not reweaved)
          reweave <dir> [<dll>...] [--weaver <dll>]
                                         run the real weaver over <dir> (default: the three weaved dlls)
          diff <a.dll> <b.dll>           structural diff: types, fields, methods, attributes, IL
          diff --api <a.dll> <b.dll> [--allowlist <file.json>]
                                         API-surface diff (recompiled vs original): no IL, no compiler noise
          pdbcheck <dll>                 PDB <-> IL consistency (sequence points, locals)

        deweave options:
          --out <dir>        write reverted copies into <dir> (default: in place)
          --dry-run          report what would change, write nothing
          --no-blank-aot     keep the generated CoderAOT.Dummy() body (default: blank it)
          --empty-methods    empty the weaver-added methods instead of removing them
          --keep-version     keep the weaver's timestamp version
          --version <v>      the version to reset to (default 1.0.0.0)
          --backup           write <name>.dll.weaved.bak before overwriting in place
        """;

    static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            Console.WriteLine(Usage);
            return args.Length == 0 ? 2 : 0;
        }
        try
        {
            string[] rest = args[1..];
            return args[0] switch
            {
                "verify" => ResidueCheck.Run(Single(rest, "verify <dll>"), Console.Out),
                "known" => Known(Single(rest, "known <weaver.dll>")),
                "syncmembers" => SyncMemberCheck.Run(Single(rest, "syncmembers <dll>"), Console.Out, ResoniteDirs()),
                "reweave" => Reweave(rest),
                "diff" => AssemblyDiff.Run(rest),
                "pdbcheck" => PdbCheck.Run(Single(rest, "pdbcheck <dll>")),
                _ => DeweaveCommand.Run(args),
            };
        }
        catch (UsageException e)
        {
            Console.Error.WriteLine(e.Message + "\n\n" + Usage);
            return 2;
        }
        catch (Exception e) when (e is IOException or BadImageFormatException)
        {
            Console.Error.WriteLine("FAIL: " + e.Message);
            return 1;
        }
    }

    static string Single(string[] args, string usage) =>
        args.Length >= 1 ? args[0] : throw new UsageException("usage: Resonator.Deweaver " + usage);

    static int Known(string weaverDll)
    {
        var report = KnownWeaverCheck.Run(weaverDll);
        Console.WriteLine(report);
        return report.Known ? 0 : 1;
    }

    static int Reweave(string[] args)
    {
        if (args.Length == 0) throw new UsageException("usage: Resonator.Deweaver reweave <dir> [<dll>...] [--weaver <dll>]");
        string weaver = Path.Combine(AppContext.BaseDirectory, "FrooxEngine.Weaver.dll");
        var assemblies = args[1..].ToList();
        int at = assemblies.IndexOf("--weaver");
        if (at >= 0)
        {
            if (at + 1 >= assemblies.Count) throw new UsageException("--weaver needs a path");
            weaver = Path.GetFullPath(assemblies[at + 1]);
            assemblies.RemoveRange(at, 2);
        }
        if (assemblies.Count == 0) assemblies = ["FrooxEngine.dll", "ProtoFlux.Nodes.FrooxEngine.dll", "ProtoFluxBindings.dll"];
        try
        {
            return Reweaver.Reweave(args[0], assemblies.ToArray(), weaver);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 3;
        }
    }

    /// <summary>Where a checked assembly's references resolve from, besides its own directory: %RESONITE_DIR%.</summary>
    internal static string[] ResoniteDirs() =>
        (Environment.GetEnvironmentVariable("RESONITE_DIR") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries);
}

/// <summary>Bad command line: reported with the usage text, exit code 2.</summary>
internal sealed class UsageException(string message) : Exception(message);

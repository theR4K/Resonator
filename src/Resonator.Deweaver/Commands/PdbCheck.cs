using System;
using System.IO;
using System.Linq;
using Resonator.Weaver.Cecil;

namespace Resonator.Deweaver.Commands;

/// <summary>
/// <c>pdbcheck &lt;dll&gt;</c> - does the PDB next to the dll still match its IL? Loads the symbols and
/// counts the methods with sequence points and locals (a mismatched PDB fails to load or has none).
/// </summary>
internal static class PdbCheck
{
    public static int Run(string dllPath)
    {
        string path = Path.GetFullPath(dllPath);
        if (!AssemblyFile.HasPdb(path))
        {
            Console.WriteLine($"{path}: PDB MISSING ({Path.ChangeExtension(path, ".pdb")})");
            return 1;
        }
        try
        {
            using var assembly = AssemblyFile.Read(path, withSymbols: true);
            var methods = assembly.MainModule.AllTypes().SelectMany(t => t.Methods).ToList();
            var withBody = methods.Where(m => m.HasBody).ToList();
            int withSequencePoints = withBody.Count(m => m.DebugInformation is { HasSequencePoints: true });
            int withLocals = withBody.Count(m => m.Body.HasVariables);

            bool ok = withSequencePoints > 0;
            Console.WriteLine($"{path}: PDB {(ok ? "OK" : "BROKEN (no sequence points)")}");
            Console.WriteLine($"  methods total={methods.Count} withBody={withBody.Count} withSeqPoints={withSequencePoints} withLocalVars={withLocals}");
            return ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{path}: PDB BROKEN — {ex.Message}");
            return 1;
        }
    }
}

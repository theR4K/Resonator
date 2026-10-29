using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Resonator.Weaver.Cecil;

namespace Resonator.Weaver.Checks;

/// <summary>
/// Verifies that a shipped weaver DLL is a KNOWN FrooxEngine.Weaver before the pipeline trusts its
/// inverse on it. Deweaving (and flattening) is the exact inverse of THIS weaver's modifications;
/// if the weaver changed shape (new modification, renamed entry point, different marker) the
/// pipeline must refuse rather than silently produce a wrong assembly.
///
///   [1] Fody entry point: FrooxEngine.Weaver.AssemblyPostProcessor.Process(string, object, string)
///   [2] the "FROOXENGINE_WEAVED" marker label is embedded       (RemoveProcessedAttribute)
///   [3] the added method names are embedded                     (RemoveWorkerMethods, ...)
///   [4] the "CoderAOT" / "Dummy" rewrite strings: whether this weaver rewrites CoderAOT.Dummy
///       (BlankCoderAotDummy). Not a requirement: the weaver of Resonite 2026.10.27 no longer does (its
///       Process does the other five modifications only), and FrooxEngine ships the Dummy body as source.
///       <see cref="KnownWeaverReport.RewritesCoderAot"/> tells the flattener and the deweaver whether to blank it.
///
/// [2]-[4] are byte scans: a weaver that performs a modification carries its names in IL or metadata.
/// </summary>
public static class KnownWeaverCheck
{
    public static KnownWeaverReport Run(string weaverDll)
    {
        var report = new KnownWeaverReport { Path = Path.GetFullPath(weaverDll) };
        if (!File.Exists(report.Path))
        {
            report.Checks.Add("[FAIL] weaver dll not found");
            return report;
        }

        try
        {
            if (!report.Check(HasFodyEntryPoint(report.Path), "Fody entry point (AssemblyPostProcessor.Process(string,object,string))"))
                return report;
        }
        catch (Exception ex)
        {
            report.Checks.Add("[FAIL] could not load weaver as .NET assembly: " + ex.Message);
            return report;
        }

        byte[] raw = File.ReadAllBytes(report.Path);
        bool Embeds(params string[] names) => names.All(name => Contains(raw, name));
        report.Check(Embeds(WeaverSpec.ProcessedLabel), "processed marker label (RemoveProcessedAttribute)");
        report.Check(Embeds("InitializeSyncMembers", "GetSyncMember", "__New"),
            "worker machinery names InitializeSyncMembers/GetSyncMember/__New (RemoveWorkerMethods)");
        report.RewritesCoderAot = Embeds("CoderAOT", "Dummy");
        report.Checks.Add(report.RewritesCoderAot
            ? "[ok]  CoderAOT.Dummy rewrite strings (BlankCoderAotDummy)"
            : "[ok]  no CoderAOT.Dummy rewrite (the shipped Dummy body is source and is kept)");

        report.Known = report.Checks.All(c => c.StartsWith("[ok]"));
        return report;
    }

    static bool HasFodyEntryPoint(string path)
    {
        using var assembly = AssemblyFile.Read(path);
        var processor = assembly.MainModule.AllTypes().FirstOrDefault(t => t.FullName == "FrooxEngine.Weaver.AssemblyPostProcessor");
        return processor != null && processor.Methods.Any(m =>
            m.Name == "Process" && m.IsStatic && m.Parameters.Count == 3 &&
            m.Parameters[0].ParameterType.FullName == "System.String" &&
            m.Parameters[2].ParameterType.FullName == "System.String");
    }

    /// <summary>
    /// UTF-8 and UTF-16LE: the metadata string heap is UTF-8, but const string values live in the
    /// blob heap as UTF-16 (as the shipped weaver's PROCESSED_LABEL does).
    /// </summary>
    static bool Contains(byte[] raw, string text) =>
        raw.AsSpan().IndexOf(Encoding.UTF8.GetBytes(text)) >= 0 ||
        raw.AsSpan().IndexOf(Encoding.Unicode.GetBytes(text)) >= 0;
}

public sealed class KnownWeaverReport
{
    public string Path = "";
    public bool Known;
    /// <summary>The weaver rewrites CoderAOT.Dummy's body, so the prepared dll gets the original empty one back.</summary>
    public bool RewritesCoderAot;
    /// <summary>Human-readable, one line per check.</summary>
    public List<string> Checks = new();

    internal bool Check(bool ok, string what)
    {
        Checks.Add((ok ? "[ok]  " : "[FAIL] ") + what + (ok ? "" : " — MISSING"));
        return ok;
    }

    public override string ToString() =>
        string.Join("\n", Checks.Prepend((Known ? "WEAVER KNOWN  : " : "WEAVER UNKNOWN: ") + Path));
}

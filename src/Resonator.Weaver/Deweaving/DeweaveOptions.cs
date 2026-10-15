using System.Linq;

namespace Resonator.Weaver.Deweaving;

/// <summary>What happens to the weaver-added Worker methods.</summary>
public enum MethodMode
{
    /// <summary>Delete them (the original had none).</summary>
    Remove,
    /// <summary>Keep them with a throwing body - the fallback when removal leaves dangling cross-module refs.</summary>
    Empty,
}

public sealed record DeweaveOptions
{
    /// <summary>Write reverted copies here; null = write back over the input.</summary>
    public string? OutputDir { get; init; }
    public MethodMode MethodMode { get; init; } = MethodMode.Remove;
    /// <summary>Report only, write nothing.</summary>
    public bool DryRun { get; init; }
    /// <summary>Blank the weaver-rewritten CoderAOT.Dummy body (the original was empty).</summary>
    public bool BlankAotDummy { get; init; } = true;
    /// <summary>Replace the weaver's timestamp version with <see cref="VersionReset"/>.</summary>
    public bool ResetVersion { get; init; } = true;
    public string VersionReset { get; init; } = "1.0.0.0";
    /// <summary>Write &lt;name&gt;.weaved.bak before overwriting in place.</summary>
    public bool KeepBackup { get; init; }
}

public sealed class DeweaveReport
{
    public string? Path;
    public bool WasWeaved;
    public int RemovedProcessedAttr;
    public int RemovedInitSyncMembers;
    public int RemovedGetSyncMember;
    public int RemovedNew;
    public int RemovedSyncMethodDelegateNull;
    public int KeptSyncMethodOriginal;
    public int RevertedFieldVisibility;
    public bool ResetVersion;
    public bool BlankAotDummy;
    public bool UsedEmptyFallback;
    /// <summary>Native / non-.NET dll, skipped.</summary>
    public bool NotDotNet;
    /// <summary>File in use by another process, skipped.</summary>
    public bool Locked;
    public string? Error;

    public bool Modified =>
        RemovedProcessedAttr + RemovedInitSyncMembers + RemovedGetSyncMember + RemovedNew
        + RemovedSyncMethodDelegateNull + RevertedFieldVisibility > 0
        || ResetVersion || BlankAotDummy;

    public override string ToString()
    {
        if (Error != null) return $"{Path}: ERROR {Error}";
        if (NotDotNet) return $"{Path}: skipped (not a .NET assembly)";
        if (Locked) return $"{Path}: skipped (in use by another process)";
        if (!WasWeaved) return $"{Path}: not weaved, skipped";
        return string.Join(", ", new[]
        {
            Path,
            "weaved=yes",
            $"-processed={RemovedProcessedAttr}",
            $"-InitSyncMembers={RemovedInitSyncMembers}",
            $"-GetSyncMember={RemovedGetSyncMember}",
            $"-__New={RemovedNew}",
            $"-SyncMethod(Delegate,null)={RemovedSyncMethodDelegateNull}",
            $"keptSyncMethod={KeptSyncMethodOriginal}",
            $"fieldVisReverted={RevertedFieldVisibility}",
            $"versionReset={ResetVersion}",
            $"aotDummyBlanked={BlankAotDummy}",
            UsedEmptyFallback ? "[methods:EMPTIED (remove blocked metadata)]" : null,
        }.Where(x => x != null));
    }
}

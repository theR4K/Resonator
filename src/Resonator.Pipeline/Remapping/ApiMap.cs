using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Resonator.Pipeline.Remapping;

/// <summary>Declarative mappings use original assembly identities and C# documentation IDs, never source spelling.</summary>
public sealed record ApiMap
{
    public string[] Files { get; init; } = ["**/*.cs"];
    public TypeMap[] Types { get; init; } = [];
    public MemberMap[] Members { get; init; } = [];
    public SupportFile[] SupportFiles { get; init; } = [];

    public static ApiMap Load(string file)
    {
        var map = JsonSerializer.Deserialize<ApiMap>(File.ReadAllText(file), new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        }) ?? throw new ArgumentException("empty API map: " + file);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in map.Types)
            if (string.IsNullOrWhiteSpace(t.Assembly) || string.IsNullOrWhiteSpace(t.From) || string.IsNullOrWhiteSpace(t.To)
                || !keys.Add(t.Assembly + ":T:" + t.From)) throw new ArgumentException("invalid/duplicate type mapping in " + file);
        foreach (var m in map.Members)
            if (string.IsNullOrWhiteSpace(m.Assembly) || string.IsNullOrWhiteSpace(m.From) || string.IsNullOrWhiteSpace(m.To)
                || !keys.Add(m.Assembly + ":" + m.From) || m.Kind is not ("rename" or "call" or "constructor" or "member" or "operator")
                || m.Receiver is not ("none" or "value" or "ref")) throw new ArgumentException("invalid/duplicate member mapping in " + file);
        return map;
    }
}

public sealed record TypeMap(string Assembly, string From, string To);
public sealed record MemberMap(string Assembly, string From, string To)
{
    public string Kind { get; init; } = "member";
    public string Receiver { get; init; } = "none";
    // Destination parameter names indexed by ORIGINAL parameter ordinal. Keeping source argument order
    // preserves evaluation order, even when the destination constructor orders its parameters differently.
    public string[] ArgumentNames { get; init; } = [];
}
public sealed record SupportFile(string Source, string Destination);

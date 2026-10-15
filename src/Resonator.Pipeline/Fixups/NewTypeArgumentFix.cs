using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Resonator.Pipeline.Infrastructure;

namespace Resonator.Pipeline.Fixups;

/// <summary>
/// <c>new Type(arg)</c> rendered as <c>new Typearg;</c>. Split only at a KNOWN type name - one declared
/// in the tree or used in a well-formed <c>new X(</c> - followed by a lower-case identifier.
/// </summary>
public sealed class NewTypeArgumentFix : ISourceFix
{
    static readonly Regex Declared = new(@"\b(?:class|struct|enum|interface|record|delegate)\s+([A-Za-z_][A-Za-z0-9_]*)");
    static readonly Regex Constructed = new(@"\bnew\s+([A-Za-z_][A-Za-z0-9_]*)\s*[({\[<]");
    static readonly Regex Fused = new(@"\bnew\s+([A-Za-z_][A-Za-z0-9_]*)(?=;)");
    static readonly Regex Argument = new(@"^[a-z_][A-Za-z0-9_]*$");

    public string Name => "new-parens-files";

    public int Apply(IReadOnlyList<string> files)
    {
        var types = KnownTypes(files);
        int changed = SourceFiles.Rewrite(files, text => Fused.Replace(text, m => Split(m, types)));
        Log.Info($"fixed 'new Typearg' parens in {changed} files");
        return changed;
    }

    static HashSet<string> KnownTypes(IEnumerable<string> files)
    {
        var types = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in files)
        {
            string text = File.ReadAllText(file);
            foreach (Match m in Declared.Matches(text)) types.Add(m.Groups[1].Value);
            foreach (Match m in Constructed.Matches(text)) types.Add(m.Groups[1].Value);
        }
        return types;
    }

    // the longest known type that prefixes the token; the rest must be an argument identifier
    static string Split(Match fused, HashSet<string> types)
    {
        string token = fused.Groups[1].Value;
        if (types.Contains(token)) return fused.Value;
        for (int length = token.Length - 1; length > 0; length--)
        {
            if (!types.Contains(token[..length])) continue;
            string arg = token[length..];
            return Argument.IsMatch(arg) ? $"new {token[..length]}({arg})" : fused.Value;
        }
        return fused.Value;
    }
}

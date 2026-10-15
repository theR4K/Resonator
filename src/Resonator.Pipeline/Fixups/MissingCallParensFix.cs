using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Resonator.Pipeline.Infrastructure;

namespace Resonator.Pipeline.Fixups;

/// <summary>
/// A generic call rendered without its argument parentheses: <c>Name&lt;T&gt;arg</c> -> <c>Name&lt;T&gt; (arg)</c>.
/// Finds each call head, skips the (nested) type-argument list, and parenthesizes the argument
/// that follows it - unless a parenthesis already does.
/// </summary>
public abstract class MissingCallParensFix(string name, string summary, Regex head, Regex argument) : ISourceFix
{
    public string Name => name;

    public int Apply(IReadOnlyList<string> files)
    {
        int calls = 0;
        SourceFiles.Rewrite(files, text => Rewrite(text, ref calls));
        Log.Info($"{summary}: {calls}");
        return calls;
    }

    string Rewrite(string text, ref int calls)
    {
        var sb = new StringBuilder();
        int i = 0;
        for (var call = head.Match(text, i); call.Success; call = head.Match(text, i))
        {
            int close = MatchingAngle(text, call.Index + call.Length - 1);
            if (close < 0) break;
            int after = close + 1;
            if (after < text.Length && text[after] == '(')
            {
                sb.Append(text, i, after + 1 - i);   // already a call
                i = after + 1;
                continue;
            }
            sb.Append(text, i, after - i);
            i = after;
            var arg = argument.Match(text, after);
            if (!arg.Success) continue;
            sb.Append(" (").Append(arg.Groups["arg"].Value.Replace(" ", "")).Append(')');
            i = after + arg.Length;
            calls++;
        }
        return sb.Append(text, i, text.Length - i).ToString();
    }

    /// <summary>The index of the '&gt;' closing the '&lt;' at <paramref name="open"/>; -1 if unbalanced.</summary>
    static int MatchingAngle(string text, int open)
    {
        int depth = 0;
        for (int k = open; k < text.Length; k++)
        {
            if (text[k] == '<') depth++;
            else if (text[k] == '>' && --depth == 0) return k;
        }
        return -1;
    }
}

/// <summary><c>Unsafe.As&lt;T1,T2&gt;expr</c> -> <c>Unsafe.As&lt;T1,T2&gt; (expr)</c>.</summary>
public sealed class UnsafeAsFix() : MissingCallParensFix(
    "unsafe-as", "re-parenthesized Unsafe.As calls",
    new Regex(@"Unsafe\.As<"),
    new Regex(@"\G\s*(?<arg>[A-Za-z_][A-Za-z0-9_]*(?:\s*\.\s*[A-Za-z_][A-Za-z0-9_]*)*)"));

/// <summary>
/// <c>receiver.Member&lt;T&gt;arg</c> -> <c>receiver.Member&lt;T&gt; (arg)</c>. Dot-guarded, so a declaration
/// such as <c>List&lt;T&gt;var</c> is never touched.
/// </summary>
public sealed class GenericMemberCallFix() : MissingCallParensFix(
    "gen-member-calls", "re-parenthesized generic member calls",
    new Regex(@"\b[A-Za-z_][A-Za-z0-9_]*\.[A-Za-z_][A-Za-z0-9_]*<"),
    new Regex(@"\G(?<arg>[a-z_][A-Za-z0-9_]*(?:\.[A-Za-z0-9_]+)*)"));

using System.Collections.Generic;
using System.Text.RegularExpressions;
using Resonator.Pipeline.Infrastructure;

namespace Resonator.Pipeline.Fixups;

/// <summary><c>((ValueType&lt;&gt;)(object)this).Member</c> -> <c>this.Member</c> (a member access on an open-generic receiver).</summary>
public sealed class OpenGenericCastFix : ISourceFix
{
    static readonly Regex Cast = new(@"\(\(([A-Za-z0-9_]+)(?:<[^)]*>)?\)\(object\)this\)\.([A-Za-z0-9_]+)");
    static readonly Regex Site = new(@"\(object\)this\)");

    public string Name => "open-generic-cast";

    public int Apply(IReadOnlyList<string> files)
    {
        int sites = 0;
        int changed = SourceFiles.Rewrite(files, text =>
        {
            string fixedText = Cast.Replace(text, "this.$2");
            if (fixedText != text) sites += Site.Matches(text).Count;
            return fixedText;
        });
        Log.Info($"open-generic cast: fixed {sites} site(s) in {changed} file(s)");
        return sites;
    }
}

using System.Collections.Generic;
using System.Text.RegularExpressions;
using Resonator.Pipeline.Infrastructure;

namespace Resonator.Pipeline.Fixups;

/// <summary>
/// Drops <c>[LoggerMessage(...)]</c> attributes from code that is already generator output: files
/// carrying <c>[GeneratedCode("Microsoft.Extensions.Logging.Generators", ...)]</c>. The shipped dlls
/// have the generated bodies decompiled back in; re-declaring the attribute lets the current
/// framework's generator run again over the same source (SYSLIB1010/1016: methods must be partial
/// and bodiless). Without the attribute the decompiled bodies compile as they are.
/// </summary>
public sealed class LoggerMessageGeneratedFix : ISourceFix
{
    static readonly Regex Generated = new(@"\[GeneratedCode\(""Microsoft\.Extensions\.Logging\.Generators""");
    static readonly Regex Attribute = new(@"^[ \t]*\[LoggerMessage\(.*?\)\][ \t]*\r?\n", RegexOptions.Multiline);

    public string Name => "logger-message-generated";

    public int Apply(IReadOnlyList<string> files)
    {
        int removed = 0;
        SourceFiles.Rewrite(files, text =>
        {
            if (!Generated.IsMatch(text)) return text;
            var m = Attribute.Matches(text);
            if (m.Count == 0) return text;
            removed += m.Count;
            return Attribute.Replace(text, "");
        });
        Log.Info($"logger-message attributes dropped (already-generated code): {removed}");
        return removed;
    }
}

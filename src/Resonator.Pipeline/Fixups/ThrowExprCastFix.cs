using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Resonator.Pipeline.Infrastructure;

namespace Resonator.Pipeline.Fixups;

/// <summary>
/// Repairs decompiled rethrow sites: <c>(x as Exception) ?? throw x</c> becomes
/// <c>(x as Exception) ?? throw (Exception)x</c>. The catch-handler slot is typed as
/// object, but a throw expression requires an Exception operand.
/// </summary>
public sealed class ThrowExprCastFix : ISourceFix
{
    public string Name => "throw-expr-cast";

    public int Apply(IReadOnlyList<string> files)
    {
        int sites = 0;
        SourceFiles.Rewrite(files, text =>
        {
            var tree = CSharpSyntaxTree.ParseText(text);
            var changes = new List<TextChange>();
            foreach (var binary in tree.GetRoot().DescendantNodes().OfType<BinaryExpressionSyntax>())
            {
                if (!binary.IsKind(SyntaxKind.CoalesceExpression)
                    || binary.Left is not ParenthesizedExpressionSyntax { Expression: BinaryExpressionSyntax cast }
                    || !cast.IsKind(SyntaxKind.AsExpression)
                    || cast.Left is not IdentifierNameSyntax source
                    || cast.Right is not IdentifierNameSyntax { Identifier.ValueText: "Exception" }
                    || binary.Right is not ThrowExpressionSyntax { Expression: IdentifierNameSyntax operand }
                    || source.Identifier.ValueText != operand.Identifier.ValueText)
                    continue;

                // Insert at the token span so all original whitespace and comments survive.
                changes.Add(new TextChange(new TextSpan(operand.SpanStart, 0), "(Exception)"));
            }
            sites += changes.Count;
            return changes.Count == 0 ? text : tree.GetText().WithChanges(changes).ToString();
        });
        Log.Info($"throw-expr cast: {sites}");
        return sites;
    }
}

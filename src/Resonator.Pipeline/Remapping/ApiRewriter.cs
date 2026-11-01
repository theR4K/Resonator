using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Resonator.Pipeline.Remapping;

/// <summary>Rewrites bound original symbols. All queries use the unchanged original semantic model.</summary>
public sealed class ApiRewriter(SemanticModel model, ApiMap map, string targetAssembly) : CSharpSyntaxRewriter
{
    public int Changes { get; private set; }

    bool AssemblyMatches(ISymbol symbol, string assembly) => assembly == "$target"
        ? SymbolEqualityComparer.Default.Equals(symbol.ContainingAssembly, model.Compilation.Assembly)
            || symbol.ContainingAssembly?.Name == targetAssembly
        : symbol.ContainingAssembly?.Name == assembly;

    ISymbol? Symbol(SyntaxNode node)
    {
        var info = model.GetSymbolInfo(node);
        if (info.Symbol != null) return info.Symbol;
        // Guessing an overload here could silently change behaviour.
        if (info.CandidateSymbols.Any(s => Member(s) != null || s is INamedTypeSymbol t && Type(t) != null))
            throw new InvalidOperationException($"API remap: unresolved/ambiguous mapped symbol at {node.GetLocation().GetLineSpan()}: {node} ({info.CandidateReason}); "
                + string.Join("; ", model.GetDiagnostics(node.Span).Select(d => d.ToString())));
        return null;
    }

    TypeMap? Type(INamedTypeSymbol type) => map.Types.FirstOrDefault(t => AssemblyMatches(type, t.Assembly)
        && type.ToDisplayString() == t.From);

    MemberMap? Member(ISymbol? symbol)
    {
        if (symbol is IMethodSymbol method) symbol = (method.ReducedFrom ?? method).OriginalDefinition;
        return symbol == null ? null : map.Members.FirstOrDefault(m => AssemblyMatches(symbol, m.Assembly)
            && symbol.GetDocumentationCommentId() == m.From);
    }

    NameSyntax? TypeName(SyntaxNode node)
    {
        if (Symbol(node) is not INamedTypeSymbol symbol || Type(symbol) is not { } mapping) return null;
        Changes++;
        return SyntaxFactory.ParseName("global::" + mapping.To).WithTriviaFrom(node);
    }

    public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
    {
        if (node.IsVar) return node;
        if (TypeName(node) is { } type) return type;
        if (node.Parent is MemberAccessExpressionSyntax access && access.Name == node) return node;
        if (Member(Symbol(node)) is { Kind: "call" } && node.Parent is not InvocationExpressionSyntax)
            throw new InvalidOperationException("API remap cannot adapt a method group; use an explicit lambda: " + node);
        if (Member(Symbol(node)) is { Kind: "rename" or "member" } mapping)
        {
            Changes++;
            return SyntaxFactory.IdentifierName(mapping.To).WithTriviaFrom(node);
        }
        return base.VisitIdentifierName(node);
    }
    public override SyntaxNode? VisitQualifiedName(QualifiedNameSyntax node) => TypeName(node) ?? base.VisitQualifiedName(node);
    public override SyntaxNode? VisitAliasQualifiedName(AliasQualifiedNameSyntax node) => TypeName(node) ?? base.VisitAliasQualifiedName(node);

    public override SyntaxNode? VisitGenericName(GenericNameSyntax node)
    {
        var rewritten = (GenericNameSyntax)base.VisitGenericName(node)!;
        if (node.Parent is MemberAccessExpressionSyntax access && access.Name == node) return rewritten;
        if (Member(Symbol(node)) is not { Kind: "rename" } mapping) return rewritten;
        Changes++;
        return rewritten.WithIdentifier(SyntaxFactory.Identifier(mapping.To).WithTriviaFrom(node.Identifier));
    }

    public override SyntaxNode? VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
    {
        if (TypeName(node) is { } name) return name;
        var mapping = Member(Symbol(node));
        if (mapping?.Kind == "call" && node.Parent is not InvocationExpressionSyntax)
            throw new InvalidOperationException("API remap cannot adapt a method group; use an explicit lambda: " + node);
        var rewritten = (MemberAccessExpressionSyntax)base.VisitMemberAccessExpression(node)!;
        if (mapping == null || mapping.Kind is not ("member" or "rename")) return rewritten;
        Changes++;
        return rewritten.WithName(rewritten.Name.WithIdentifier(SyntaxFactory.Identifier(mapping.To).WithTriviaFrom(node.Name.Identifier)));
    }

    public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        var mapping = Member(model.GetDeclaredSymbol(node));
        var rewritten = (MethodDeclarationSyntax)base.VisitMethodDeclaration(node)!;
        if (mapping?.Kind != "rename") return rewritten;
        Changes++;
        return rewritten.WithIdentifier(SyntaxFactory.Identifier(node.Identifier.LeadingTrivia, mapping.To, node.Identifier.TrailingTrivia));
    }

    public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        var original = Symbol(node) as IMethodSymbol;
        var mapping = Member(original);
        var rewritten = (InvocationExpressionSyntax)base.VisitInvocationExpression(node)!;
        if (mapping == null || mapping.Kind != "call") return rewritten;
        if (original?.ReducedFrom != null)
            throw new InvalidOperationException("API remap requires static syntax for extension calls: " + node);
        if (original is { IsStatic: false } && mapping.Receiver == "none")
            throw new InvalidOperationException("API remap instance calls require an explicit receiver rule: " + node);
        var arguments = RewriteArguments(node.ArgumentList, rewritten.ArgumentList, mapping);
        if (mapping.Receiver != "none")
        {
            if (node.Expression is not MemberAccessExpressionSyntax member
                || model.GetSymbolInfo(node).Symbol is not IMethodSymbol method || method.IsStatic)
                throw new InvalidOperationException("API remap requires an explicit instance receiver: " + node);
            var receiver = SyntaxFactory.Argument(((ExpressionSyntax)Visit(member.Expression)!).WithoutLeadingTrivia());
            if (mapping.Receiver == "ref") receiver = receiver.WithRefKindKeyword(SyntaxFactory.Token(SyntaxKind.RefKeyword).WithTrailingTrivia(SyntaxFactory.Space));
            var withReceiver = arguments.Arguments.Insert(0, receiver);
            if (withReceiver.Count > 1)
            {
                var comma = withReceiver.GetSeparators().First();
                withReceiver = withReceiver.ReplaceSeparator(comma, comma.WithTrailingTrivia(SyntaxFactory.Space));
            }
            arguments = arguments.WithArguments(withReceiver);
        }
        Changes++;
        return rewritten.WithExpression(SyntaxFactory.ParseExpression(mapping.To).WithTriviaFrom(node.Expression)).WithArgumentList(arguments);
    }

    public override SyntaxNode? VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
    {
        var mapping = Member(Symbol(node));
        var rewritten = (ObjectCreationExpressionSyntax)base.VisitObjectCreationExpression(node)!;
        if (mapping?.Kind != "constructor") return rewritten;
        if (node.ArgumentList == null || rewritten.ArgumentList == null) throw new InvalidOperationException("constructor arguments required: " + node);
        Changes++;
        return rewritten.WithType(SyntaxFactory.ParseTypeName(mapping.To).WithTriviaFrom(node.Type))
            .WithArgumentList(RewriteArguments(node.ArgumentList, rewritten.ArgumentList, mapping));
    }

    public override SyntaxNode? VisitImplicitObjectCreationExpression(ImplicitObjectCreationExpressionSyntax node)
    {
        var mapping = Member(Symbol(node));
        var rewritten = (ImplicitObjectCreationExpressionSyntax)base.VisitImplicitObjectCreationExpression(node)!;
        if (mapping?.Kind != "constructor") return rewritten;
        Changes++;
        return SyntaxFactory.ObjectCreationExpression(SyntaxFactory.ParseTypeName(mapping.To))
            .WithNewKeyword(node.NewKeyword.WithTrailingTrivia(SyntaxFactory.Space))
            .WithArgumentList(RewriteArguments(node.ArgumentList, rewritten.ArgumentList, mapping))
            .WithInitializer(rewritten.Initializer).WithTriviaFrom(node);
    }

    public override SyntaxNode? VisitBinaryExpression(BinaryExpressionSyntax node)
    {
        var mapping = Member(Symbol(node));
        var rewritten = (BinaryExpressionSyntax)base.VisitBinaryExpression(node)!;
        if (mapping?.Kind != "operator") return rewritten;
        Changes++;
        if (node.IsKind(SyntaxKind.LogicalAndExpression) || node.IsKind(SyntaxKind.LogicalOrExpression))
            throw new InvalidOperationException("API remap cannot replace a short-circuit operator: " + node);
        return OperatorCall(mapping, rewritten.Left, rewritten.Right, node.OperatorToken).WithTriviaFrom(node);
    }

    public override SyntaxNode? VisitAssignmentExpression(AssignmentExpressionSyntax node)
    {
        var mapping = Member(Symbol(node));
        var rewritten = (AssignmentExpressionSyntax)base.VisitAssignmentExpression(node)!;
        if (mapping?.Kind != "operator") return rewritten;
        // Expanding compound assignment must not evaluate a receiver/indexer twice. Locals and
        // parameters are safe; more complex lvalues need temporaries and are explicitly unsupported.
        if (node.Left is not IdentifierNameSyntax || Symbol(node.Left) is not (ILocalSymbol or IParameterSymbol))
            throw new InvalidOperationException("API remap compound operator requires a local or parameter lvalue: " + node);
        Changes++;
        return SyntaxFactory.AssignmentExpression(SyntaxKind.SimpleAssignmentExpression, rewritten.Left,
            OperatorCall(mapping, rewritten.Left.WithoutTrivia(), rewritten.Right, SyntaxFactory.Token(SyntaxKind.None)))
            .WithOperatorToken(SyntaxFactory.Token(SyntaxKind.EqualsToken).WithTriviaFrom(node.OperatorToken))
            .WithTriviaFrom(node);
    }

    static InvocationExpressionSyntax OperatorCall(MemberMap mapping, ExpressionSyntax left, ExpressionSyntax right, SyntaxToken operation) =>
        SyntaxFactory.InvocationExpression(SyntaxFactory.ParseExpression(mapping.To),
            SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList<ArgumentSyntax>(
                new SyntaxNodeOrToken[] { SyntaxFactory.Argument(left.WithoutLeadingTrivia().WithTrailingTrivia(left.GetTrailingTrivia().Where(t => !t.IsKind(SyntaxKind.WhitespaceTrivia)))),
                    SyntaxFactory.Token(SyntaxKind.CommaToken).WithTrailingTrivia(operation.RawKind == 0 ? SyntaxFactory.TriviaList(SyntaxFactory.Space) : operation.LeadingTrivia.AddRange(operation.TrailingTrivia)), SyntaxFactory.Argument(right) })));

    ArgumentListSyntax RewriteArguments(ArgumentListSyntax original, ArgumentListSyntax rewritten, MemberMap mapping)
    {
        if (mapping.ArgumentNames.Length == 0) return rewritten;
        var arguments = rewritten.Arguments;
        for (int i = 0; i < original.Arguments.Count; i++)
        {
            if (model.GetOperation(original.Arguments[i]) is not IArgumentOperation { Parameter: { } parameter }
                || parameter.Ordinal >= mapping.ArgumentNames.Length)
                throw new InvalidOperationException("API remap cannot bind argument: " + original.Arguments[i]);
            var name = SyntaxFactory.NameColon(mapping.ArgumentNames[parameter.Ordinal]).WithTrailingTrivia(SyntaxFactory.Space);
            arguments = arguments.Replace(arguments[i], arguments[i].WithNameColon(name));
        }
        return rewritten.WithArguments(arguments);
    }

    /// <summary>Detect method overloads that collapse after type substitution, before writing any files.</summary>
    public void ValidateSignatures(IEnumerable<SyntaxTree> trees)
    {
        var selected = trees.ToHashSet();
        var owners = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        var signatures = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tree in selected)
        {
            var semantics = model.Compilation.GetSemanticModel(tree);
            foreach (var declaration in tree.GetRoot().DescendantNodes().OfType<BaseMethodDeclarationSyntax>())
                if (semantics.GetDeclaredSymbol(declaration) is IMethodSymbol method) owners.Add(method.ContainingType);
        }
        foreach (var owner in owners)
            foreach (var method in owner.GetMembers().OfType<IMethodSymbol>())
            {
                if (method.IsImplicitlyDeclared || method.PartialDefinitionPart != null) continue;
                bool rewritten = method.DeclaringSyntaxReferences.Any(r => selected.Contains(r.SyntaxTree));
                var rename = Member(method);
                string signature = method.ContainingType.ToDisplayString() + ":" + (rewritten && rename?.Kind == "rename" ? rename.To : method.MetadataName)
                    + "`" + method.Arity + "(" + string.Join(",", method.Parameters.Select(p => (p.RefKind == RefKind.None ? "" : "&") + MappedType(p.Type, rewritten))) + ")";
                if (method.MethodKind == MethodKind.Conversion) signature += "->" + MappedType(method.ReturnType, rewritten);
                if (!signatures.Add(signature)) throw new InvalidOperationException("API remap creates a signature collision: " + signature);
            }
    }

    string MappedType(ITypeSymbol type, bool rewrite) => type switch
    {
        IArrayTypeSymbol array => MappedType(array.ElementType, rewrite) + "[" + new string(',', array.Rank - 1) + "]",
        IPointerTypeSymbol pointer => MappedType(pointer.PointedAtType, rewrite) + "*",
        ITypeParameterSymbol parameter => (parameter.TypeParameterKind == TypeParameterKind.Method ? "!!" : "!") + parameter.Ordinal,
        INamedTypeSymbol named when rewrite && Type(named) is { } mapping => mapping.To,
        INamedTypeSymbol named when named.IsGenericType => named.OriginalDefinition.ToDisplayString() + "<" + string.Join(",", named.TypeArguments.Select(t => MappedType(t, rewrite))) + ">",
        _ => type.ToDisplayString(),
    };

    /// <summary>Reject misspelled external symbols and missing destinations before transforming source.</summary>
    public void ValidateRules()
    {
        foreach (var mapping in map.Types)
        {
            var sourceAssemblies = model.Compilation.References.Select(r => model.Compilation.GetAssemblyOrModuleSymbol(r)).OfType<IAssemblySymbol>()
                .Append(model.Compilation.Assembly);
            if (!sourceAssemblies.Any(a => a.GetTypeByMetadataName(mapping.From) is { } original && AssemblyMatches(original, mapping.Assembly)))
                throw new InvalidOperationException("API remap source type missing: " + mapping.Assembly + ":" + mapping.From);
            if (model.Compilation.GetTypeByMetadataName(mapping.To) == null)
                throw new InvalidOperationException("API remap destination type missing: " + mapping.To);
        }
        foreach (var mapping in map.Members)
        {
            var sources = DocumentationCommentId.GetSymbolsForDeclarationId(mapping.From, model.Compilation)
                .Where(s => AssemblyMatches(s, mapping.Assembly)).ToArray();
            // A source declaration can already be renamed on a repeat fixup, or be absent in a split target.
            if (sources.Length == 0 && mapping.Assembly == "$target") continue;
            if (sources.Length != 1) throw new InvalidOperationException("API remap source member missing/ambiguous: " + mapping.From);
            if (mapping.Kind is "rename" or "member")
            {
                if (!SyntaxFacts.IsValidIdentifier(mapping.To)) throw new InvalidOperationException("API remap member name invalid: " + mapping.To);
                if (mapping.Kind == "member" && sources[0].ContainingType is { } owner && Type(owner) is { } type)
                    if (model.Compilation.GetTypeByMetadataName(type.To)!.GetMembers(mapping.To).Length == 0)
                        throw new InvalidOperationException("API remap destination member missing: " + type.To + "." + mapping.To);
                continue;
            }
            if (sources[0] is not IMethodSymbol method) throw new InvalidOperationException("API remap expects a method: " + mapping.From);
            if (mapping.Kind == "call" && method.IsStatic != (mapping.Receiver == "none"))
                throw new InvalidOperationException("API remap receiver rule must match the original static/instance method: " + mapping.From);
            if (mapping.Kind != "call" && mapping.Receiver != "none")
                throw new InvalidOperationException("API remap receiver rules apply only to calls: " + mapping.From);
            if (mapping.Kind is "call" or "operator" && method.Arity != 0)
                throw new InvalidOperationException("API remap call adapters currently require non-generic methods: " + mapping.From);
            if (mapping.Kind == "operator" && method.Parameters.Length != 2)
                throw new InvalidOperationException("API remap currently supports binary operators only: " + mapping.From);
            string destination = mapping.To.Replace("global::", "", StringComparison.Ordinal);
            string typeName, memberName;
            if (mapping.Kind == "constructor") { typeName = destination; memberName = ".ctor"; }
            else
            {
                int dot = destination.LastIndexOf('.');
                if (dot < 0) throw new InvalidOperationException("API remap call must name its destination type: " + destination);
                typeName = destination[..dot]; memberName = destination[(dot + 1)..];
            }
            int arity = method.Parameters.Length + (mapping.Receiver == "none" ? 0 : 1);
            var destinations = model.Compilation.GetTypeByMetadataName(typeName)?.GetMembers(memberName).OfType<IMethodSymbol>()
                .Where(m => m.Parameters.Length == arity && (mapping.Kind == "constructor" || m.IsStatic)).ToArray() ?? [];
            if (destinations.Length == 0) throw new InvalidOperationException("API remap destination method/arity missing: " + destination);
            if (mapping.ArgumentNames.Length > 0 && (mapping.ArgumentNames.Length != method.Parameters.Length
                || mapping.ArgumentNames.Distinct(StringComparer.Ordinal).Count() != mapping.ArgumentNames.Length
                || !destinations.Any(d => mapping.ArgumentNames.All(n => d.Parameters.Any(p => p.Name == n)))))
                throw new InvalidOperationException("API remap destination argument names invalid: " + destination);
        }
    }
}

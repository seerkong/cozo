using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cozo.DotNet.LlmWiki.RoslynEnhancer;

/// <summary>
/// Roslyn semantic enhancement capsule (roslyn-csharp-resolution track, design.md §1).
/// Single public entry: <see cref="Analyze"/> builds one lightweight compilation over all
/// given C# sources (no MSBuildWorkspace) and walks invocation / object-creation sites:
///
/// - Compilation recipe (spike-proven, 98.8% 1.0-hit): ParseText every file + one injected
///   global-usings tree simulating ImplicitUsings + full TPA MetadataReferences from
///   AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"). Diagnostics are tolerated.
/// - SymbolInfo.Symbol → confidence 1.0 evidence "semantic"; CandidateSymbols only →
///   0.6 evidence "candidate:&lt;CandidateReason&gt;"; neither → UnknownCalls++.
/// - Callee not in source (BCL / external assemblies) → ExternalCalls++, no edge.
/// - Caller = DocId of the nearest enclosing method / ctor / property accessor / local
///   function; lambdas attribute upward; top-level statements attribute to the synthesized
///   entry point. nameof(...) is not an invocation and is skipped entirely.
/// </summary>
public static class RoslynEnhancer
{
    /// <summary>Analyze all C# sources of one repository as a single lightweight compilation.</summary>
    /// <param name="csFiles">(path, source) pairs; paths are echoed back verbatim in the result.</param>
    public static RoslynAnalysisResult Analyze(IReadOnlyList<(string Path, string Source)> csFiles)
        => RoslynAnalysisWalker.Analyze(csFiles);
}

/// <summary>Internal implementation: compilation building + semantic traversal.</summary>
internal static class RoslynAnalysisWalker
{
    private const double SemanticConfidence = 1.0;
    private const double CandidateConfidence = 0.6;
    private const string SemanticEvidence = "semantic";

    /// <summary>ImplicitUsings simulation for SDK-style repos compiled without csproj context.</summary>
    private const string GlobalUsingsSource = """
        global using System;
        global using System.Collections.Generic;
        global using System.IO;
        global using System.Linq;
        global using System.Net.Http;
        global using System.Threading;
        global using System.Threading.Tasks;
        """;

    private static readonly Lazy<IReadOnlyList<MetadataReference>> TrustedPlatformReferences = new(LoadTrustedPlatformReferences);

    public static RoslynAnalysisResult Analyze(IReadOnlyList<(string Path, string Source)> csFiles)
    {
        var parseOptions = CSharpParseOptions.Default;
        var trees = new SyntaxTree[csFiles.Count];
        for (var i = 0; i < csFiles.Count; i++)
        {
            trees[i] = CSharpSyntaxTree.ParseText(csFiles[i].Source, parseOptions, path: csFiles[i].Path);
        }

        var compilation = CSharpCompilation.Create(
            "RoslynEnhancer.Analysis",
            trees.Append(CSharpSyntaxTree.ParseText(GlobalUsingsSource, parseOptions, path: "__GlobalUsings.g.cs")),
            TrustedPlatformReferences.Value,
            new CSharpCompilationOptions(OutputKind.ConsoleApplication, allowUnsafe: true));

        var edges = new List<RoslynCallEdge>();
        var symbols = new List<RoslynSymbolInfo>();
        var externalSites = new List<RoslynExternalCallSite>();
        var externalCalls = 0;
        var unknownCalls = 0;

        foreach (var tree in trees)
        {
            var model = compilation.GetSemanticModel(tree, ignoreAccessibility: true);
            var path = tree.FilePath;
            foreach (var node in tree.GetRoot().DescendantNodes())
            {
                switch (node)
                {
                    case MemberDeclarationSyntax or LocalFunctionStatementSyntax:
                        CollectSymbol(model, node, path, symbols);
                        break;
                    case InvocationExpressionSyntax invocation when !IsNameOf(invocation):
                        CollectCall(model, invocation, path, edges, externalSites, ref externalCalls, ref unknownCalls);
                        break;
                    case BaseObjectCreationExpressionSyntax creation:
                        CollectCall(model, creation, path, edges, externalSites, ref externalCalls, ref unknownCalls);
                        break;
                }
            }
        }

        return new RoslynAnalysisResult(edges, symbols, externalCalls, unknownCalls) { ExternalCallSites = externalSites };
    }

    /// <summary>nameof(...) parses as an invocation but is a constant expression, never a call.</summary>
    private static bool IsNameOf(InvocationExpressionSyntax invocation)
        => invocation.Expression is IdentifierNameSyntax { Identifier.Text: "nameof" };

    private static void CollectCall(
        SemanticModel model,
        ExpressionSyntax callSite,
        string path,
        List<RoslynCallEdge> edges,
        List<RoslynExternalCallSite> externalSites,
        ref int externalCalls,
        ref int unknownCalls)
    {
        var info = model.GetSymbolInfo(callSite);
        ISymbol? callee;
        double confidence;
        string evidence;
        if (info.Symbol is not null)
        {
            callee = info.Symbol;
            confidence = SemanticConfidence;
            evidence = SemanticEvidence;
        }
        else if (!info.CandidateSymbols.IsDefaultOrEmpty)
        {
            // Deterministic first candidate; evidence records why resolution degraded.
            callee = info.CandidateSymbols[0];
            confidence = CandidateConfidence;
            evidence = $"candidate:{info.CandidateReason}";
        }
        else
        {
            unknownCalls++;
            return;
        }

        // Reduced extension methods carry the receiver in the reduced form; DocIds live on the unreduced definition.
        if (callee is IMethodSymbol { ReducedFrom: not null } reduced)
        {
            callee = reduced.ReducedFrom!;
        }

        var calleeIsInSource = callee.Locations.Any(location => location.IsInSource);
        if (!calleeIsInSource)
        {
            externalCalls++;
            // depa-ontology track (design §4.2): capture the site detail before dropping the
            // external target. QualifiedName erases generic arguments and merges overloads at
            // method-name granularity; ctors normalize to the type name. Sites without an
            // attributable caller are counted only.
            var externalCallerDocId = FindCallerDocId(model, callSite);
            var targetKey = QualifiedName(callee.OriginalDefinition);
            if (!string.IsNullOrEmpty(externalCallerDocId) && targetKey.Length > 0)
            {
                var externalLine = callSite.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                externalSites.Add(new RoslynExternalCallSite(externalCallerDocId!, targetKey, path, externalLine));
            }

            return;
        }

        var calleeDocId = callee.GetDocumentationCommentId() ?? callee.OriginalDefinition.GetDocumentationCommentId();
        var callerDocId = FindCallerDocId(model, callSite);
        if (string.IsNullOrEmpty(calleeDocId) || string.IsNullOrEmpty(callerDocId))
        {
            unknownCalls++;
            return;
        }

        var line = callSite.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        edges.Add(new RoslynCallEdge(callerDocId, calleeDocId, calleeIsInSource, path, line, confidence, evidence));
    }

    /// <summary>
    /// Caller = DocId of the nearest enclosing method-shaped symbol: ordinary method, constructor,
    /// property/event accessor, local function, or the synthesized top-level-statements entry point.
    /// Lambdas / anonymous functions (no DocId) attribute upward to their containing method.
    /// </summary>
    private static string? FindCallerDocId(SemanticModel model, SyntaxNode callSite)
    {
        var enclosing = model.GetEnclosingSymbol(callSite.SpanStart);
        while (enclosing is not null)
        {
            if (enclosing is IMethodSymbol method && method.MethodKind != MethodKind.AnonymousFunction)
            {
                var docId = method.GetDocumentationCommentId();
                if (!string.IsNullOrEmpty(docId))
                {
                    return docId;
                }
            }
            else if (enclosing is IFieldSymbol or IPropertySymbol or IEventSymbol)
            {
                // Field / property initializers attribute to the member itself.
                var docId = enclosing.GetDocumentationCommentId();
                if (!string.IsNullOrEmpty(docId))
                {
                    return docId;
                }
            }

            enclosing = enclosing.ContainingSymbol;
        }

        return null;
    }

    private static void CollectSymbol(SemanticModel model, SyntaxNode declaration, string path, List<RoslynSymbolInfo> symbols)
    {
        switch (declaration)
        {
            case FieldDeclarationSyntax field:
                foreach (var variable in field.Declaration.Variables)
                {
                    AddSymbol(model.GetDeclaredSymbol(variable), declaration, path, symbols);
                }
                return;
            case EventFieldDeclarationSyntax eventField:
                foreach (var variable in eventField.Declaration.Variables)
                {
                    AddSymbol(model.GetDeclaredSymbol(variable), declaration, path, symbols);
                }
                return;
            case BaseTypeDeclarationSyntax or DelegateDeclarationSyntax or MethodDeclarationSyntax
                or ConstructorDeclarationSyntax or PropertyDeclarationSyntax or EnumMemberDeclarationSyntax
                or LocalFunctionStatementSyntax:
                AddSymbol(model.GetDeclaredSymbol(declaration), declaration, path, symbols);
                return;
            default:
                return; // namespaces, usings, accessors, operators etc. — not sym_key targets.
        }
    }

    private static void AddSymbol(ISymbol? symbol, SyntaxNode declaration, string path, List<RoslynSymbolInfo> symbols)
    {
        if (symbol is null)
        {
            return;
        }

        var docId = symbol.GetDocumentationCommentId();
        if (string.IsNullOrEmpty(docId))
        {
            return;
        }

        var qualified = QualifiedName(symbol);
        var arity = symbol is IMethodSymbol method ? method.Parameters.Length : 0;
        var span = declaration.GetLocation().GetLineSpan();
        symbols.Add(new RoslynSymbolInfo(
            docId,
            $"csharp:{qualified}#{arity}",
            qualified,
            arity,
            path,
            span.StartLinePosition.Line + 1,
            span.EndLinePosition.Line + 1));
    }

    /// <summary>
    /// Namespace-qualified dotted name matching the tree-sitter extractor convention:
    /// constructors use the written type name (not ".ctor"); the global namespace contributes nothing.
    /// </summary>
    private static string QualifiedName(ISymbol symbol)
    {
        var parts = new Stack<string>();
        for (var current = symbol; current is not null and not INamespaceSymbol { IsGlobalNamespace: true }; current = current.ContainingSymbol)
        {
            if (current is INamespaceSymbol ns)
            {
                parts.Push(ns.Name);
            }
            else if (current is IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor } ctor)
            {
                parts.Push(ctor.ContainingType.Name);
            }
            else
            {
                parts.Push(current.Name);
            }
        }

        return string.Join('.', parts);
    }

    /// <summary>Full TPA reference set — spike recipe: everything on the trusted platform assemblies list.</summary>
    private static IReadOnlyList<MetadataReference> LoadTrustedPlatformReferences()
    {
        var references = new List<MetadataReference>();
        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string tpa)
        {
            foreach (var assemblyPath in tpa.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    references.Add(MetadataReference.CreateFromFile(assemblyPath));
                }
                catch (IOException)
                {
                    // Unreadable TPA entry — skip; diagnostics are tolerated downstream.
                }
                catch (NotSupportedException)
                {
                }
            }
        }

        return references;
    }
}

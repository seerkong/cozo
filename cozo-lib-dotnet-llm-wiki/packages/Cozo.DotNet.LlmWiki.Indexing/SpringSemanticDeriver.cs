using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cozo.DotNet.Om.CodeKnowledge;

namespace Cozo.DotNet.LlmWiki.Indexing;

/// <summary>
/// Bounded Spring semantic derivation over Java syntax facts. This layer intentionally consumes
/// source text plus CodeKnowledge symbols instead of teaching framework meaning to JavaExtractor.
/// </summary>
internal static class SpringSemanticDeriver
{
    internal const string SpringRoleKind = "SPRING_ROLE";
    internal const string SpringTransactionKind = "SPRING_TRANSACTION";

    private const double AnnotationConfidence = 0.99;
    private const double MapperImportConfidence = 0.95;
    private const double NamingConfidence = 0.70;
    private const string AnnotationResolver = "spring_annotation";
    private const string MapperImportResolver = "spring_mapper_import";
    private const string NamingResolver = "spring_naming";

    private static readonly IReadOnlyList<CodeConceptFact> SpringConcepts =
    [
        new("spring:role:controller", "Spring controller", "HTTP/API boundary derived from Spring annotations."),
        new("spring:role:service", "Spring service", "Application service boundary derived from Spring annotations."),
        new("spring:role:component", "Spring component", "Spring component boundary derived from Spring annotations."),
        new("spring:role:configuration", "Spring configuration", "Spring configuration role derived from Spring annotations."),
        new("spring:role:repository", "Spring repository", "Persistence boundary derived from Spring annotations or mapper imports."),
        new("spring:role:mapper", "Spring mapper", "Persistence or transformation mapper derived from mapper imports."),
        new("spring:role:listener", "Spring listener", "Event or message listener derived from listener annotations."),
        new("spring:role:handler", "Spring handler", "Handler role derived from conservative naming conventions."),
        new("spring:role:dto", "Spring DTO", "Data transfer object role derived from conservative naming conventions."),
        new("spring:role:entity", "Spring entity", "Persistence entity role derived from annotations."),
        new("spring:role:vo", "Spring value object", "Value object role derived from conservative naming conventions."),
        new("spring:transaction", "Spring transaction", "Transaction scope derived from @Transactional."),
    ];

    private static readonly Dictionary<string, string> AnnotationRoles = new(StringComparer.Ordinal)
    {
        ["Controller"] = "spring:role:controller",
        ["RestController"] = "spring:role:controller",
        ["Service"] = "spring:role:service",
        ["Component"] = "spring:role:component",
        ["Configuration"] = "spring:role:configuration",
        ["Repository"] = "spring:role:repository",
        ["Entity"] = "spring:role:entity",
    };

    private static readonly Dictionary<string, string> ShortcutMethods = new(StringComparer.Ordinal)
    {
        ["GetMapping"] = "GET",
        ["PostMapping"] = "POST",
        ["PutMapping"] = "PUT",
        ["DeleteMapping"] = "DELETE",
        ["PatchMapping"] = "PATCH",
    };

    private static readonly HashSet<string> MappingAnnotations = new(StringComparer.Ordinal)
    {
        "RequestMapping",
        "GetMapping",
        "PostMapping",
        "PutMapping",
        "DeleteMapping",
        "PatchMapping",
    };

    private static readonly HashSet<string> TypeKinds = new(StringComparer.Ordinal)
    {
        "class", "interface", "record", "enum", "annotation",
    };

    private static readonly Regex ImportPattern = new(
        @"^\s*import\s+(?:static\s+)?(?<target>[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*(?:\.\*)?)\s*;",
        RegexOptions.Compiled);

    private static readonly Regex RequestMethodPattern = new(
        @"RequestMethod\.([A-Z]+)",
        RegexOptions.Compiled);

    private sealed record AnnotationOccurrence(
        string Name,
        string SimpleName,
        string Arguments,
        string Raw,
        int StartLine,
        int EndLine);

    private sealed record JavaFileContext(
        CallResolver.FileInput File,
        string[] Lines,
        IReadOnlyList<AnnotationOccurrence> Annotations,
        IReadOnlyDictionary<string, string> ImportsBySimpleName);

    internal sealed record Result(
        IReadOnlyList<CodeConceptFact> Concepts,
        IReadOnlyList<CodeEdgeFact> Edges,
        IReadOnlyList<CodeEntryPointFact> EntryPoints,
        IReadOnlyList<CodeDiagnosticFact> Diagnostics,
        IReadOnlyList<CodeSemanticClaimFact> Claims);

    internal static Result Derive(
        IReadOnlyList<CallResolver.FileInput> parsedFiles,
        IReadOnlyList<CodeSymbolFact> symbols)
    {
        var javaFiles = parsedFiles
            .Where(file => file.Language == "java")
            .ToDictionary(file => file.FileId, file => new JavaFileContext(
                file,
                SplitLines(file.SourceText),
                ParseAnnotations(file.SourceText),
                ParseImports(file.SourceText)),
                StringComparer.Ordinal);
        if (javaFiles.Count == 0)
        {
            return new Result([], [], [], [], []);
        }

        var edges = new List<CodeEdgeFact>();
        var entryPoints = new List<CodeEntryPointFact>();
        var diagnostics = new List<CodeDiagnosticFact>();
        var claims = new List<CodeSemanticClaimFact>();
        var emittedClaims = new HashSet<string>(StringComparer.Ordinal);
        var emittedEdges = new HashSet<string>(StringComparer.Ordinal);
        var byId = symbols.ToDictionary(symbol => symbol.SymbolId, StringComparer.Ordinal);
        var symbolsByFile = symbols
            .Where(symbol => symbol.Lang == "java" && javaFiles.ContainsKey(symbol.FileId))
            .GroupBy(symbol => symbol.FileId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderBy(s => s.StartLine).ToArray(), StringComparer.Ordinal);

        var controllerTypeIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var symbol in symbolsByFile.Values.SelectMany(fileSymbols => fileSymbols))
        {
            var context = javaFiles[symbol.FileId];
            var annotations = AdjacentAnnotations(context, symbol).ToArray();
            if (annotations.Length == 0 && TypeKinds.Contains(symbol.Kind))
            {
                AddNamingRoles(symbol, edges, emittedEdges);
                continue;
            }

            if (TypeKinds.Contains(symbol.Kind))
            {
                AddTypeAnnotationRoles(symbol, annotations, context, edges, diagnostics, emittedEdges);
                AddNamingRoles(symbol, edges, emittedEdges);
                if (HasAnnotation(annotations, "Controller") || HasAnnotation(annotations, "RestController"))
                {
                    controllerTypeIds.Add(symbol.SymbolId);
                }
            }

            foreach (var annotation in annotations.Where(a => a.SimpleName == "Transactional"))
            {
                AddEdge(edges, emittedEdges, symbol.SymbolId, "spring:transaction", SpringTransactionKind,
                    symbol.FileId, annotation.StartLine, AnnotationConfidence, AnnotationResolver, annotation.Raw);
                AddSemanticClaim(
                    claims,
                    emittedClaims,
                    symbol,
                    CodeSemanticClaimKinds.TransactionScope,
                    new Dictionary<string, object?>
                    {
                        ["annotation"] = annotation.Name,
                        ["scopeKind"] = symbol.Kind,
                        ["symbolId"] = symbol.SymbolId,
                        ["symbolName"] = symbol.Name,
                    },
                    annotation.StartLine,
                    annotation.EndLine,
                    annotation.Raw);
            }

            if (symbol.Kind == "method")
            {
                DeriveListenerFacts(symbol, annotations, context, byId, edges, entryPoints, emittedEdges);
            }
        }

        foreach (var method in symbolsByFile.Values.SelectMany(fileSymbols => fileSymbols).Where(symbol => symbol.Kind == "method"))
        {
            if (!controllerTypeIds.Contains(method.ParentId))
            {
                continue;
            }

            var context = javaFiles[method.FileId];
            var methodAnnotations = AdjacentAnnotations(context, method).ToArray();
            var mapping = methodAnnotations.FirstOrDefault(annotation => MappingAnnotations.Contains(annotation.SimpleName));
            if (mapping is null)
            {
                continue;
            }

            var classMappings = byId.TryGetValue(method.ParentId, out var parent)
                ? AdjacentAnnotations(context, parent)
                    .Where(annotation => MappingAnnotations.Contains(annotation.SimpleName))
                    .ToArray()
                : [];
            var paths = CombinePaths(
                classMappings.SelectMany(PathsOf),
                PathsOf(mapping));
            var methods = MethodsOf(mapping).DefaultIfEmpty("ANY").Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            var metadata = JoinMetadata(
                ("methods", string.Join(",", methods)),
                ("paths", string.Join(",", paths)),
                ("site", byId.TryGetValue(method.ParentId, out var owner) ? $"{owner.Name}.{method.Name}" : method.Name),
                ("resolver", AnnotationResolver),
                ("confidence", AnnotationConfidence.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)),
                ("evidence", mapping.Raw));
            entryPoints.Add(new CodeEntryPointFact(method.SymbolId, CodeEntryPointKinds.HttpRoute, metadata));
            AddSemanticClaim(
                claims,
                emittedClaims,
                method,
                CodeSemanticClaimKinds.RouteBinding,
                new Dictionary<string, object?>
                {
                    ["httpMethods"] = methods,
                    ["paths"] = paths,
                    ["site"] = byId.TryGetValue(method.ParentId, out var routeOwner)
                        ? $"{routeOwner.Name}.{method.Name}"
                        : method.Name,
                    ["symbolId"] = method.SymbolId,
                },
                mapping.StartLine,
                mapping.EndLine,
                mapping.Raw);
        }

        var sourceClaims = JavaSourceSemanticClaimDeriver.Derive(parsedFiles, symbols);
        claims.AddRange(sourceClaims.Claims);
        diagnostics.AddRange(sourceClaims.Diagnostics);

        return new Result(
            edges.Count > 0 || entryPoints.Count > 0 ? SpringConcepts : [],
            edges.OrderBy(edge => edge.FromId, StringComparer.Ordinal)
                .ThenBy(edge => edge.Kind, StringComparer.Ordinal)
                .ThenBy(edge => edge.ToId, StringComparer.Ordinal)
                .ThenByDescending(edge => edge.Evidence.Contains("EventListener", StringComparison.Ordinal))
                .ThenBy(edge => edge.Line)
                .ToArray(),
            entryPoints.OrderBy(entry => entry.SymbolId, StringComparer.Ordinal)
                .ThenBy(entry => entry.Kind, StringComparer.Ordinal)
                .ToArray(),
            diagnostics.OrderBy(diagnostic => diagnostic.DiagnosticId, StringComparer.Ordinal).ToArray(),
            claims.OrderBy(claim => claim.FileId, StringComparer.Ordinal)
                .ThenBy(claim => claim.StartLine)
                .ThenBy(claim => claim.Kind, StringComparer.Ordinal)
                .ThenBy(claim => claim.ClaimId, StringComparer.Ordinal)
                .ToArray());
    }

    private static void AddSemanticClaim(
        List<CodeSemanticClaimFact> claims,
        HashSet<string> emittedClaims,
        CodeSymbolFact subject,
        string kind,
        IReadOnlyDictionary<string, object?> payload,
        int startLine,
        int endLine,
        string evidence)
    {
        var payloadJson = CodeSemanticClaimIdentity.CanonicalizePayload(JsonSerializer.Serialize(payload));
        var claimId = CodeSemanticClaimIdentity.Create(subject.FileId, kind, payloadJson, startLine, endLine);
        if (!emittedClaims.Add(claimId))
        {
            return;
        }

        claims.Add(new CodeSemanticClaimFact(
            claimId,
            subject.SymbolId,
            kind,
            payloadJson,
            subject.FileId,
            startLine,
            endLine,
            AnnotationConfidence,
            AnnotationResolver,
            NormalizeWhitespace(evidence)));
    }

    private static void AddTypeAnnotationRoles(
        CodeSymbolFact symbol,
        IReadOnlyList<AnnotationOccurrence> annotations,
        JavaFileContext context,
        List<CodeEdgeFact> edges,
        List<CodeDiagnosticFact> diagnostics,
        HashSet<string> emittedEdges)
    {
        foreach (var annotation in annotations)
        {
            if (AnnotationRoles.TryGetValue(annotation.SimpleName, out var role))
            {
                AddEdge(edges, emittedEdges, symbol.SymbolId, role, SpringRoleKind,
                    symbol.FileId, annotation.StartLine, AnnotationConfidence, AnnotationResolver, annotation.Raw);
            }

            if (annotation.SimpleName == "Mapper")
            {
                AddMapperRoles(symbol, annotation, context, edges, diagnostics, emittedEdges);
            }
        }
    }

    private static void AddMapperRoles(
        CodeSymbolFact symbol,
        AnnotationOccurrence annotation,
        JavaFileContext context,
        List<CodeEdgeFact> edges,
        List<CodeDiagnosticFact> diagnostics,
        HashSet<string> emittedEdges)
    {
        var qualified = annotation.Name.Contains('.', StringComparison.Ordinal)
            ? annotation.Name
            : context.ImportsBySimpleName.GetValueOrDefault("Mapper", ResolveMapperWildcard(context));
        switch (qualified)
        {
            case "org.apache.ibatis.annotations.Mapper":
                AddEdge(edges, emittedEdges, symbol.SymbolId, "spring:role:mapper", SpringRoleKind,
                    symbol.FileId, annotation.StartLine, MapperImportConfidence, MapperImportResolver, qualified);
                AddEdge(edges, emittedEdges, symbol.SymbolId, "spring:role:repository", SpringRoleKind,
                    symbol.FileId, annotation.StartLine, MapperImportConfidence, MapperImportResolver, qualified);
                break;
            case "org.mapstruct.Mapper":
                AddEdge(edges, emittedEdges, symbol.SymbolId, "spring:role:mapper", SpringRoleKind,
                    symbol.FileId, annotation.StartLine, MapperImportConfidence, MapperImportResolver, qualified);
                break;
            default:
                diagnostics.Add(new CodeDiagnosticFact(
                    $"diag:spring-ambiguous-mapper:{symbol.SymbolId}",
                    symbol.SymbolId,
                    "spring_ambiguous_mapper",
                    $"Ambiguous @Mapper on {symbol.Name}; import did not identify MyBatis or MapStruct.",
                    "info"));
                break;
        }
    }

    private static string ResolveMapperWildcard(JavaFileContext context)
    {
        var hasMyBatis = context.ImportsBySimpleName.ContainsKey("org.apache.ibatis.annotations.*");
        var hasMapStruct = context.ImportsBySimpleName.ContainsKey("org.mapstruct.*");
        return (hasMyBatis, hasMapStruct) switch
        {
            (true, false) => "org.apache.ibatis.annotations.Mapper",
            (false, true) => "org.mapstruct.Mapper",
            _ => "",
        };
    }

    private static void AddNamingRoles(
        CodeSymbolFact symbol,
        List<CodeEdgeFact> edges,
        HashSet<string> emittedEdges)
    {
        if (symbol.Name.EndsWith("DTO", StringComparison.Ordinal) || symbol.Name.EndsWith("Dto", StringComparison.Ordinal))
        {
            AddEdge(edges, emittedEdges, symbol.SymbolId, "spring:role:dto", SpringRoleKind,
                symbol.FileId, symbol.StartLine, NamingConfidence, NamingResolver, symbol.Name);
        }

        if (symbol.Name.EndsWith("VO", StringComparison.Ordinal) || symbol.Name.EndsWith("Vo", StringComparison.Ordinal))
        {
            AddEdge(edges, emittedEdges, symbol.SymbolId, "spring:role:vo", SpringRoleKind,
                symbol.FileId, symbol.StartLine, NamingConfidence, NamingResolver, symbol.Name);
        }

        if (symbol.Name.EndsWith("Handler", StringComparison.Ordinal))
        {
            AddEdge(edges, emittedEdges, symbol.SymbolId, "spring:role:handler", SpringRoleKind,
                symbol.FileId, symbol.StartLine, NamingConfidence, NamingResolver, symbol.Name);
        }
    }

    private static void DeriveListenerFacts(
        CodeSymbolFact method,
        IReadOnlyList<AnnotationOccurrence> annotations,
        JavaFileContext context,
        IReadOnlyDictionary<string, CodeSymbolFact> byId,
        List<CodeEdgeFact> edges,
        List<CodeEntryPointFact> entryPoints,
        HashSet<string> emittedEdges)
    {
        foreach (var annotation in annotations)
        {
            if (annotation.SimpleName is not ("KafkaListener" or "EventListener"))
            {
                continue;
            }

            AddEdge(edges, emittedEdges, method.SymbolId, "spring:role:listener", SpringRoleKind,
                method.FileId, annotation.StartLine, AnnotationConfidence, AnnotationResolver, annotation.Raw);
            if (byId.TryGetValue(method.ParentId, out var container))
            {
                AddEdge(edges, emittedEdges, container.SymbolId, "spring:role:listener", SpringRoleKind,
                    method.FileId, annotation.StartLine, AnnotationConfidence, AnnotationResolver, annotation.Raw);
            }

            if (annotation.SimpleName == "KafkaListener")
            {
                var topics = ValuesFor(annotation, "topics", includeDirect: false);
                var metadata = JoinMetadata(
                    ("topics", string.Join(",", topics)),
                    ("containerFactory", ValuesFor(annotation, "containerFactory", includeDirect: false).FirstOrDefault() ?? ""),
                    ("site", SiteOf(method, byId)),
                    ("resolver", AnnotationResolver),
                    ("confidence", AnnotationConfidence.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)),
                    ("evidence", annotation.Raw));
                entryPoints.Add(new CodeEntryPointFact(method.SymbolId, "kafka_listener", metadata));
            }
            else
            {
                var eventTypes = ValuesFor(annotation, "classes", includeDirect: true).ToArray();
                if (eventTypes.Length == 0)
                {
                    eventTypes = ExtractParameterTypes(DeclarationTextOf(context, method)).ToArray();
                }

                var metadata = JoinMetadata(
                    ("eventTypes", string.Join(",", eventTypes)),
                    ("site", SiteOf(method, byId)),
                    ("resolver", AnnotationResolver),
                    ("confidence", AnnotationConfidence.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)),
                    ("evidence", annotation.Raw));
                entryPoints.Add(new CodeEntryPointFact(method.SymbolId, "event_listener", metadata));
            }
        }
    }

    private static void AddEdge(
        List<CodeEdgeFact> edges,
        HashSet<string> emittedEdges,
        string fromId,
        string toId,
        string kind,
        string fileId,
        int line,
        double confidence,
        string resolver,
        string evidence)
    {
        var key = string.Join('\n', fromId, toId, kind, fileId, line, resolver);
        if (emittedEdges.Add(key))
        {
            edges.Add(new CodeEdgeFact(fromId, toId, kind, fileId, line, confidence, resolver, NormalizeWhitespace(evidence)));
        }
    }

    private static bool HasAnnotation(IEnumerable<AnnotationOccurrence> annotations, string simpleName) =>
        annotations.Any(annotation => annotation.SimpleName == simpleName);

    private static IEnumerable<AnnotationOccurrence> AdjacentAnnotations(JavaFileContext context, CodeSymbolFact symbol)
    {
        var declarationLine = DeclarationLineOf(context, symbol);
        if (declarationLine <= 1)
        {
            return [];
        }

        var annotationsByEnd = context.Annotations
            .GroupBy(annotation => annotation.EndLine)
            .ToDictionary(group => group.Key, group => group.OrderBy(a => a.StartLine).ToArray());
        var line = declarationLine - 1;
        while (line >= 1 && IsIgnorableBetweenAnnotationAndDeclaration(context.Lines[line - 1]))
        {
            line--;
        }

        if (!annotationsByEnd.TryGetValue(line, out var last))
        {
            return [];
        }

        var result = new List<AnnotationOccurrence>();
        while (line >= 1 && annotationsByEnd.TryGetValue(line, out var block))
        {
            result.AddRange(block.Reverse());
            line = block.Min(annotation => annotation.StartLine) - 1;
            while (line >= 1 && IsIgnorableBetweenAnnotations(context.Lines[line - 1]))
            {
                line--;
            }
        }

        result.Reverse();
        return result;
    }

    private static int DeclarationLineOf(JavaFileContext context, CodeSymbolFact symbol)
    {
        var start = Math.Max(1, symbol.StartLine);
        var end = Math.Min(context.Lines.Length, Math.Max(symbol.EndLine, start));
        for (var lineNumber = start; lineNumber <= end; lineNumber++)
        {
            var line = context.Lines[lineNumber - 1].Trim();
            if (line.StartsWith("@", StringComparison.Ordinal))
            {
                continue;
            }

            if (TypeKinds.Contains(symbol.Kind) && IsTypeDeclarationLine(line, symbol.Name))
            {
                return lineNumber;
            }

            if (symbol.Kind is "method" or "constructor" && IsCallableDeclarationLine(line, symbol.Name))
            {
                return lineNumber;
            }
        }

        return symbol.StartLine;
    }

    private static bool IsTypeDeclarationLine(string line, string name) =>
        Regex.IsMatch(line, $@"\b(class|interface|enum|record)\s+{Regex.Escape(name)}\b")
        || Regex.IsMatch(line, $@"@interface\s+{Regex.Escape(name)}\b");

    private static bool IsCallableDeclarationLine(string line, string name) =>
        Regex.IsMatch(line, $@"\b{Regex.Escape(name)}\s*\(");

    private static string DeclarationTextOf(JavaFileContext context, CodeSymbolFact symbol)
    {
        var line = DeclarationLineOf(context, symbol);
        return line >= 1 && line <= context.Lines.Length ? context.Lines[line - 1].Trim() : symbol.Signature;
    }

    private static bool IsIgnorableBetweenAnnotationAndDeclaration(string line) =>
        line.Trim().Length == 0;

    private static bool IsIgnorableBetweenAnnotations(string line) =>
        line.Trim().Length == 0;

    private static IReadOnlyList<AnnotationOccurrence> ParseAnnotations(string sourceText)
    {
        var lines = SplitLines(sourceText);
        var annotations = new List<AnnotationOccurrence>();
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (!trimmed.StartsWith("@", StringComparison.Ordinal))
            {
                continue;
            }

            var nameMatch = Regex.Match(trimmed, @"^@\s*(?<name>[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*)");
            if (!nameMatch.Success)
            {
                continue;
            }

            var raw = new StringBuilder(lines[i].Trim());
            var name = nameMatch.Groups["name"].Value;
            var firstParen = lines[i].IndexOf('(');
            var args = "";
            var endLine = i + 1;
            if (firstParen >= 0)
            {
                var balance = 0;
                var started = false;
                var argsBuilder = new StringBuilder();
                for (var lineIndex = i; lineIndex < lines.Length; lineIndex++)
                {
                    var startChar = lineIndex == i ? firstParen : 0;
                    if (lineIndex != i)
                    {
                        raw.Append(' ').Append(lines[lineIndex].Trim());
                    }

                    for (var charIndex = startChar; charIndex < lines[lineIndex].Length; charIndex++)
                    {
                        var ch = lines[lineIndex][charIndex];
                        if (ch == '(')
                        {
                            balance++;
                            started = true;
                            if (balance > 1)
                            {
                                argsBuilder.Append(ch);
                            }

                            continue;
                        }

                        if (ch == ')' && started)
                        {
                            balance--;
                            if (balance == 0)
                            {
                                endLine = lineIndex + 1;
                                args = argsBuilder.ToString().Trim();
                                break;
                            }

                            argsBuilder.Append(ch);
                            continue;
                        }

                        if (started)
                        {
                            argsBuilder.Append(ch);
                        }
                    }

                    if (started && balance == 0)
                    {
                        break;
                    }
                }
            }

            annotations.Add(new AnnotationOccurrence(name, SimpleName(name), args, raw.ToString(), i + 1, endLine));
            i = Math.Max(i, endLine - 1);
        }

        return annotations;
    }

    private static IReadOnlyDictionary<string, string> ParseImports(string sourceText)
    {
        var imports = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in SplitLines(sourceText))
        {
            var match = ImportPattern.Match(line);
            if (!match.Success)
            {
                continue;
            }

            var target = match.Groups["target"].Value;
            var key = target.EndsWith(".*", StringComparison.Ordinal) ? target : SimpleName(target);
            imports.TryAdd(key, target);
        }

        return imports;
    }

    private static IReadOnlyList<string> PathsOf(AnnotationOccurrence annotation)
    {
        var paths = ValuesFor(annotation, "path", includeDirect: false)
            .Concat(ValuesFor(annotation, "value", includeDirect: true))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return paths.Length == 0 ? [""] : paths;
    }

    private static IReadOnlyList<string> MethodsOf(AnnotationOccurrence annotation)
    {
        if (ShortcutMethods.TryGetValue(annotation.SimpleName, out var method))
        {
            return [method];
        }

        return RequestMethodPattern.Matches(annotation.Arguments)
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static IEnumerable<string> ValuesFor(AnnotationOccurrence annotation, string name, bool includeDirect)
    {
        var args = annotation.Arguments;
        if (args.Length == 0)
        {
            return [];
        }

        var values = new List<string>();
        var named = ExtractNamedArgument(args, name);
        if (named.Length > 0)
        {
            values.AddRange(ExtractStringOrClassTokens(named));
        }

        if (includeDirect && !LooksLikeOnlyNamedArguments(args))
        {
            values.AddRange(ExtractStringOrClassTokens(FirstTopLevelArgument(args)));
        }

        return values.Where(value => value.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
    }

    private static string ExtractNamedArgument(string args, string name)
    {
        var pattern = $@"(?<![A-Za-z0-9_]){Regex.Escape(name)}\s*=";
        var match = Regex.Match(args, pattern);
        if (!match.Success)
        {
            return "";
        }

        var start = match.Index + match.Length;
        return ReadArgumentValue(args, start);
    }

    private static string FirstTopLevelArgument(string args)
    {
        var comma = FindTopLevelDelimiter(args, ',');
        return (comma < 0 ? args : args[..comma]).Trim();
    }

    private static string ReadArgumentValue(string args, int start)
    {
        var end = args.Length;
        var brace = 0;
        var paren = 0;
        for (var i = start; i < args.Length; i++)
        {
            var ch = args[i];
            switch (ch)
            {
                case '{': brace++; break;
                case '}': if (brace > 0) brace--; break;
                case '(': paren++; break;
                case ')': if (paren > 0) paren--; break;
                case ',' when brace == 0 && paren == 0:
                    end = i;
                    i = args.Length;
                    break;
            }
        }

        return args[start..end].Trim();
    }

    private static int FindTopLevelDelimiter(string value, char delimiter)
    {
        var brace = 0;
        var paren = 0;
        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            switch (ch)
            {
                case '{': brace++; break;
                case '}': if (brace > 0) brace--; break;
                case '(': paren++; break;
                case ')': if (paren > 0) paren--; break;
            }

            if (ch == delimiter && brace == 0 && paren == 0)
            {
                return i;
            }
        }

        return -1;
    }

    private static bool LooksLikeOnlyNamedArguments(string args) =>
        Regex.IsMatch(FirstTopLevelArgument(args), @"^[A-Za-z_][A-Za-z0-9_]*\s*=");

    private static IEnumerable<string> ExtractStringOrClassTokens(string value)
    {
        foreach (Match match in Regex.Matches(value, "\"([^\"]*)\""))
        {
            yield return match.Groups[1].Value;
        }

        foreach (Match match in Regex.Matches(value, @"([A-Za-z_][A-Za-z0-9_$.]*)\.class"))
        {
            yield return SimpleName(match.Groups[1].Value);
        }
    }

    private static IReadOnlyList<string> CombinePaths(IEnumerable<string> classPaths, IEnumerable<string> methodPaths)
    {
        var classList = classPaths.DefaultIfEmpty("").ToArray();
        var methodList = methodPaths.DefaultIfEmpty("").ToArray();
        return classList
            .SelectMany(prefix => methodList.Select(path => NormalizePath(prefix, path)))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static string NormalizePath(string prefix, string path)
    {
        var joined = $"{EnsureLeadingSlash(prefix)}{EnsureLeadingSlash(path)}";
        joined = Regex.Replace(joined, "/{2,}", "/");
        return joined.Length > 1 ? joined.TrimEnd('/') : "/";
    }

    private static string EnsureLeadingSlash(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        return value.StartsWith("/", StringComparison.Ordinal) ? value : "/" + value;
    }

    private static string SiteOf(CodeSymbolFact symbol, IReadOnlyDictionary<string, CodeSymbolFact> byId) =>
        byId.TryGetValue(symbol.ParentId, out var parent) ? $"{parent.Name}.{symbol.Name}" : symbol.Name;

    private static IEnumerable<string> ExtractParameterTypes(string signature)
    {
        var open = signature.IndexOf('(');
        var close = signature.LastIndexOf(')');
        if (open < 0 || close <= open)
        {
            return [];
        }

        return signature[(open + 1)..close]
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(parameter => parameter.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "")
            .Select(type => type.Split('<')[0].Trim())
            .Where(type => type.Length > 0 && char.IsUpper(type[0]))
            .Select(SimpleName)
            .ToArray();
    }

    private static string JoinMetadata(params (string Key, string Value)[] items) =>
        string.Join(";", items.Select(item => $"{item.Key}={NormalizeWhitespace(item.Value)}"));

    private static string NormalizeWhitespace(string value) =>
        Regex.Replace(value, @"\s+", " ").Trim();

    private static string SimpleName(string name)
    {
        var dot = name.LastIndexOf('.');
        var dollar = name.LastIndexOf('$');
        var index = Math.Max(dot, dollar);
        return index >= 0 ? name[(index + 1)..] : name;
    }

    private static string[] SplitLines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
}

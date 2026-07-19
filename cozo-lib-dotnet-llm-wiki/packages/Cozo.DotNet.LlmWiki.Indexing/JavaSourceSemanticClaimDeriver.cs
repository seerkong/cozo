using System.Text.Json;
using System.Text.RegularExpressions;
using Cozo.DotNet.LlmWiki.SemanticParsing;
using Cozo.DotNet.Om.CodeKnowledge;

namespace Cozo.DotNet.LlmWiki.Indexing;

/// <summary>
/// Converts retained Java AST observations into directly anchored semantic claims. It performs
/// conservative local-type resolution but never promotes a claim into a business assertion.
/// </summary>
internal static class JavaSourceSemanticClaimDeriver
{
    private const double SyntaxConfidence = 0.99;
    private const string TreeSitterResolver = "treesitter";
    private const string AnnotationResolver = "spring_annotation";

    private static readonly HashSet<string> TypeKinds = new(StringComparer.Ordinal)
    {
        "class", "interface", "record", "enum",
    };

    private static readonly HashSet<string> RelationTargetKinds = new(StringComparer.Ordinal)
    {
        "class", "interface", "record",
    };

    private static readonly HashSet<string> ScalarTypes = new(StringComparer.Ordinal)
    {
        "boolean", "byte", "char", "double", "float", "int", "long", "short", "void",
        "Boolean", "Byte", "Character", "Double", "Float", "Integer", "Long", "Short",
        "String",
    };

    private static readonly HashSet<string> CollectionTypes = new(StringComparer.Ordinal)
    {
        "Collection", "Iterable", "List", "Set", "SortedSet", "NavigableSet",
        "Queue", "Deque",
    };

    private sealed record AnnotationSyntax(string Name, string Arguments, string Raw);

    private sealed record JavaTypeEntry(
        CodeSymbolFact Symbol,
        string QualifiedName);

    private sealed record JavaFileTypeContext(
        string PackageName,
        IReadOnlyList<string> SingleImports,
        IReadOnlyList<string> WildcardImports);

    private sealed record JavaTypeReference(
        string RawType,
        string TargetTypeText,
        string TargetTypeName,
        bool Collection);

    private sealed record JavaTypeResolution(JavaTypeEntry? Target, bool Ambiguous);

    private sealed record StateFieldObservation(
        string OwnerId,
        string MemberName,
        string EnumTypeId,
        string EnumTypeName);

    private sealed record NormalizedStateValue(
        string Value,
        string Raw,
        string Encoding,
        string EnumTypeName,
        string EnumMember);

    private sealed record StateGuardObservation(
        string MethodSymbolId,
        string Property,
        string Receiver,
        IReadOnlyList<string> AllowedValues,
        string ValueEncoding,
        string Source,
        int EndLine = 0);

    internal sealed record Result(
        IReadOnlyList<CodeSemanticClaimFact> Claims,
        IReadOnlyList<CodeDiagnosticFact> Diagnostics);

    internal static Result Derive(
        IReadOnlyList<CallResolver.FileInput> parsedFiles,
        IReadOnlyList<CodeSymbolFact> symbols)
    {
        var javaFiles = parsedFiles.Where(file => file.Language == "java").ToArray();
        if (javaFiles.Length == 0)
        {
            return new Result([], []);
        }

        var claims = new List<CodeSemanticClaimFact>();
        var diagnostics = new List<CodeDiagnosticFact>();
        var emitted = new HashSet<string>(StringComparer.Ordinal);
        var byId = symbols.ToDictionary(symbol => symbol.SymbolId, StringComparer.Ordinal);
        var typeEntries = symbols
            .Where(symbol => symbol.Lang == "java" && TypeKinds.Contains(symbol.Kind))
            .Select(symbol => new JavaTypeEntry(symbol, QualifiedName(symbol)))
            .Where(entry => entry.QualifiedName.Length > 0)
            .ToArray();
        var typesByName = typeEntries
            .GroupBy(entry => entry.Symbol.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var typesByQualified = typeEntries
            .GroupBy(entry => entry.QualifiedName, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var symbolsByFile = symbols
            .Where(symbol => symbol.Lang == "java")
            .GroupBy(symbol => symbol.FileId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var stateFields = new List<StateFieldObservation>();

        foreach (var file in javaFiles)
        {
            var syntax = file.Parsed.JavaSourceSyntax;
            if (syntax is null)
            {
                diagnostics.Add(new CodeDiagnosticFact(
                    $"diag:semantic-claim-syntax-unavailable:{file.FileId}",
                    file.FileId,
                    "semantic_claim_syntax_unavailable",
                    $"Parser backend '{file.Parsed.BackendName}' did not retain Java member/assignment syntax; no source semantic claims were emitted for {file.RelativePath}.",
                    "warning"));
                continue;
            }

            var fileSymbols = symbolsByFile.GetValueOrDefault(file.FileId, []);
            var typeContext = BuildTypeContext(file, fileSymbols);
            foreach (var member in syntax.Members)
            {
                var subject = ResolveMemberSubject(member, fileSymbols, byId);
                if (subject is null)
                {
                    continue;
                }

                var owner = member.MemberKind == "field"
                    ? byId.GetValueOrDefault(subject.ParentId)
                    : subject;
                if (owner is null || !TypeKinds.Contains(owner.Kind))
                {
                    continue;
                }

                var annotations = ParseAnnotations(member.DeclarationText);
                var typeReference = ParseDeclaredType(member.TypeText);
                var targetType = ResolveLocalType(typeReference, typeContext, typesByQualified, typesByName);
                AddTypedReferenceClaim(
                    claims,
                    diagnostics,
                    emitted,
                    file,
                    subject.SymbolId,
                    subject.SymbolId,
                    owner.SymbolId,
                    member.MemberKind,
                    member.Name,
                    typeReference,
                    targetType,
                    member.StartLine,
                    member.EndLine,
                    member.DeclarationText);

                foreach (var annotation in annotations)
                {
                    foreach (var constraint in ValidationConstraints(annotation))
                    {
                        AddClaim(
                            claims,
                            emitted,
                            file.FileId,
                            subject.SymbolId,
                            CodeSemanticClaimKinds.ValidationConstraint,
                            new Dictionary<string, object?>
                            {
                                ["annotation"] = annotation.Name,
                                ["member"] = member.Name,
                                ["operator"] = constraint.Operator,
                                ["ownerSymbolId"] = owner.SymbolId,
                                ["value"] = constraint.Value,
                            },
                            member.StartLine,
                            member.EndLine,
                            AnnotationResolver,
                            annotation.Raw);
                    }

                    foreach (var constraint in PersistenceConstraints(annotation))
                    {
                        AddClaim(
                            claims,
                            emitted,
                            file.FileId,
                            subject.SymbolId,
                            CodeSemanticClaimKinds.PersistenceConstraint,
                            new Dictionary<string, object?>
                            {
                                ["annotation"] = annotation.Name,
                                ["constraintKind"] = constraint.Operator,
                                ["member"] = member.Name,
                                ["ownerSymbolId"] = owner.SymbolId,
                                ["value"] = constraint.Value,
                            },
                            member.StartLine,
                            member.EndLine,
                            AnnotationResolver,
                            annotation.Raw);
                    }
                }

                if (targetType is not null
                    && targetType.Target?.Symbol.Kind == "enum"
                    && IsStateMember(member.Name, annotations))
                {
                    stateFields.Add(new StateFieldObservation(
                        owner.SymbolId,
                        member.Name,
                        targetType.Target.Symbol.SymbolId,
                        targetType.Target.Symbol.Name));
                    AddClaim(
                        claims,
                        emitted,
                        file.FileId,
                        subject.SymbolId,
                        CodeSemanticClaimKinds.StateField,
                        new Dictionary<string, object?>
                        {
                            ["enumType"] = targetType.Target.Symbol.Name,
                            ["enumTypeSymbolId"] = targetType.Target.Symbol.SymbolId,
                            ["member"] = member.Name,
                            ["ownerSymbolId"] = owner.SymbolId,
                        },
                        member.StartLine,
                        member.EndLine,
                        TreeSitterResolver,
                        member.DeclarationText);
                }
            }

            foreach (var typeUse in syntax.TypeUses)
            {
                var method = ResolveTypeUseMethod(typeUse, fileSymbols);
                if (method is null
                    || !byId.TryGetValue(method.ParentId, out var owner)
                    || !TypeKinds.Contains(owner.Kind))
                {
                    continue;
                }

                var typeReference = ParseDeclaredType(typeUse.TypeText);
                var targetType = ResolveLocalType(typeReference, typeContext, typesByQualified, typesByName);
                AddTypedReferenceClaim(
                    claims,
                    diagnostics,
                    emitted,
                    file,
                    method.SymbolId,
                    method.SymbolId,
                    owner.SymbolId,
                    typeUse.UsageKind,
                    typeUse.Name,
                    typeReference,
                    targetType,
                    typeUse.StartLine,
                    typeUse.EndLine,
                    typeUse.DeclarationText);
            }
        }

        var referencedEnumIds = stateFields.Select(field => field.EnumTypeId).ToHashSet(StringComparer.Ordinal);
        var enumValues = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var file in javaFiles)
        {
            if (file.Parsed.JavaSourceSyntax is not { } syntax)
            {
                continue;
            }

            var fileSymbols = symbolsByFile.GetValueOrDefault(file.FileId, []);
            foreach (var constant in syntax.EnumConstants)
            {
                var enumType = fileSymbols
                    .Where(symbol => symbol.Kind == "enum"
                        && symbol.StartLine <= constant.StartLine
                        && symbol.EndLine >= constant.EndLine)
                    .OrderBy(symbol => symbol.EndLine - symbol.StartLine)
                    .FirstOrDefault();
                if (enumType is null || !referencedEnumIds.Contains(enumType.SymbolId))
                {
                    continue;
                }

                if (!enumValues.TryGetValue(enumType.SymbolId, out var values))
                {
                    values = new HashSet<string>(StringComparer.Ordinal);
                    enumValues[enumType.SymbolId] = values;
                }
                values.Add(constant.Name);

                var subject = fileSymbols.FirstOrDefault(symbol => symbol.Kind == "field"
                    && symbol.ParentId == enumType.SymbolId
                    && symbol.Name == constant.Name
                    && symbol.StartLine == constant.StartLine);
                AddClaim(
                    claims,
                    emitted,
                    file.FileId,
                    subject?.SymbolId ?? enumType.SymbolId,
                    CodeSemanticClaimKinds.StateValue,
                    new Dictionary<string, object?>
                    {
                        ["enumType"] = enumType.Name,
                        ["enumTypeSymbolId"] = enumType.SymbolId,
                        ["value"] = constant.Name,
                    },
                    constant.StartLine,
                    constant.EndLine,
                    TreeSitterResolver,
                    constant.DeclarationText);
            }
        }

        foreach (var file in javaFiles)
        {
            if (file.Parsed.JavaSourceSyntax is not { } syntax)
            {
                continue;
            }

            var fileSymbols = symbolsByFile.GetValueOrDefault(file.FileId, []);
            var stateGuards = syntax.Guards
                .Select(guard =>
                {
                    var method = ResolveContainingMethod(guard.StartLine, guard.EndLine, fileSymbols);
                    if (method is null)
                    {
                        return null;
                    }
                    var effect = RejectEffect(guard.ConsequenceText);
                    if (effect is null)
                    {
                        return null;
                    }
                    var stateGuard = ParseAllowedStateGuard(guard.ConditionText);
                    return stateGuard is null
                        ? null
                        : stateGuard with
                        {
                            MethodSymbolId = method.SymbolId,
                            EndLine = guard.EndLine,
                        };
                })
                .Where(item => item is not null)
                .Select(item => item!)
                .ToArray();
            var variableTypesByMethod = BuildVariableTypes(
                syntax,
                fileSymbols,
                typeEntries,
                BuildTypeContext(file, fileSymbols),
                typesByQualified,
                typesByName);

            foreach (var mutation in syntax.StateMutations)
            {
                var method = fileSymbols
                    .Where(symbol => symbol.Kind == "method"
                        && symbol.StartLine <= mutation.StartLine
                        && symbol.EndLine >= mutation.EndLine)
                    .OrderBy(symbol => symbol.EndLine - symbol.StartLine)
                    .FirstOrDefault();
                if (method is null)
                {
                    continue;
                }

                var fieldName = LowerCamel(mutation.PropertyName);
                if (!IsStateMember(fieldName, []))
                {
                    continue;
                }

                var ownerSymbolId = ResolveMutationOwner(
                    mutation,
                    method,
                    variableTypesByMethod.GetValueOrDefault(
                        method.SymbolId,
                        new Dictionary<string, string>(StringComparer.Ordinal)));
                if (ownerSymbolId.Length == 0)
                {
                    continue;
                }

                var normalizedTo = NormalizeStateValue(mutation.ValueText);
                if (normalizedTo is null)
                {
                    continue;
                }

                var stateField = stateFields.FirstOrDefault(field =>
                    field.OwnerId == ownerSymbolId
                    && string.Equals(field.MemberName, fieldName, StringComparison.Ordinal));
                if (stateField is not null
                    && enumValues.TryGetValue(stateField.EnumTypeId, out var values)
                    && !values.Contains(normalizedTo.Value))
                {
                    continue;
                }

                var matchingGuard = stateGuards
                    .Where(guard => guard.MethodSymbolId == method.SymbolId
                        && guard.EndLine < mutation.StartLine
                        && string.Equals(guard.Property, fieldName, StringComparison.Ordinal)
                        && (guard.Receiver.Length == 0
                            || mutation.ReceiverText is "this" or ""
                            || NormalizeReceiver(guard.Receiver) == NormalizeReceiver(mutation.ReceiverText)))
                    .OrderBy(guard => guard.Source, StringComparer.Ordinal)
                    .FirstOrDefault();
                var fromValue = matchingGuard?.AllowedValues.Count == 1
                    ? matchingGuard.AllowedValues[0]
                    : null;
                var enumTypeName = stateField?.EnumTypeName ?? normalizedTo.EnumTypeName;
                var enumTypeSymbolId = stateField?.EnumTypeId ?? "";
                AddClaim(
                    claims,
                    emitted,
                    file.FileId,
                    method.SymbolId,
                    CodeSemanticClaimKinds.StateAssignment,
                    new Dictionary<string, object?>
                    {
                        ["enumType"] = enumTypeName,
                        ["enumTypeSymbolId"] = enumTypeSymbolId,
                        ["field"] = fieldName,
                        ["fromValue"] = fromValue,
                        ["method"] = method.Name,
                        ["methodSymbolId"] = method.SymbolId,
                        ["mutationKind"] = mutation.MutationKind,
                        ["ownerSymbolId"] = ownerSymbolId,
                        ["property"] = fieldName,
                        ["rawValue"] = normalizedTo.Raw,
                        ["receiver"] = mutation.ReceiverText,
                        ["toValue"] = normalizedTo.Value,
                        ["valueEncoding"] = normalizedTo.Encoding,
                    },
                    mutation.StartLine,
                    mutation.EndLine,
                    TreeSitterResolver,
                    mutation.ExpressionText);
            }

            foreach (var guard in syntax.Guards)
            {
                var method = ResolveContainingMethod(guard.StartLine, guard.EndLine, fileSymbols);
                if (method is null)
                {
                    continue;
                }
                var owner = byId.GetValueOrDefault(method.ParentId);
                if (owner is null || !TypeKinds.Contains(owner.Kind))
                {
                    continue;
                }
                var effect = RejectEffect(guard.ConsequenceText);
                if (effect is null)
                {
                    continue;
                }

                var stateGuard = ParseAllowedStateGuard(guard.ConditionText);
                var payload = new Dictionary<string, object?>
                {
                    ["effectKind"] = effect.Value.Kind,
                    ["effectMessage"] = effect.Value.Message,
                    ["effectSource"] = effect.Value.Source,
                    ["method"] = method.Name,
                    ["methodSymbolId"] = method.SymbolId,
                    ["ownerSymbolId"] = owner.SymbolId,
                    ["predicateSource"] = NormalizePredicate(guard.ConditionText),
                };
                if (stateGuard is not null)
                {
                    payload["stateGuard"] = new Dictionary<string, object?>
                    {
                        ["allowedValues"] = stateGuard.AllowedValues,
                        ["property"] = stateGuard.Property,
                        ["receiver"] = stateGuard.Receiver,
                        ["source"] = stateGuard.Source,
                        ["valueEncoding"] = stateGuard.ValueEncoding,
                    };
                }

                AddClaim(
                    claims,
                    emitted,
                    file.FileId,
                    method.SymbolId,
                    CodeSemanticClaimKinds.BusinessGuard,
                    payload,
                    guard.StartLine,
                    guard.EndLine,
                    TreeSitterResolver,
                    guard.StatementText);
            }
        }

        return new Result(
            claims
                .GroupBy(
                    claim => (claim.SubjectId, claim.Kind, claim.PayloadJson),
                    SemanticClaimContentComparer.Instance)
                .Select(group => group
                    .OrderBy(claim => claim.StartLine)
                    .ThenBy(claim => claim.EndLine)
                    .ThenBy(claim => claim.ClaimId, StringComparer.Ordinal)
                    .First())
                .OrderBy(claim => claim.FileId, StringComparer.Ordinal)
                .ThenBy(claim => claim.StartLine)
                .ThenBy(claim => claim.Kind, StringComparer.Ordinal)
                .ThenBy(claim => claim.ClaimId, StringComparer.Ordinal)
                .ToArray(),
            diagnostics.OrderBy(diagnostic => diagnostic.DiagnosticId, StringComparer.Ordinal).ToArray());
    }

    private static CodeSymbolFact? ResolveMemberSubject(
        ParsedJavaMemberSyntax member,
        IReadOnlyList<CodeSymbolFact> fileSymbols,
        IReadOnlyDictionary<string, CodeSymbolFact> byId)
    {
        if (member.MemberKind == "record_component")
        {
            return fileSymbols.SingleOrDefault(symbol =>
                symbol.Kind == "record" && symbol.Name == member.OwnerName);
        }

        return fileSymbols
            .Where(symbol => symbol.Kind == "field"
                && symbol.Name == member.Name
                && symbol.StartLine == member.StartLine)
            .OrderBy(symbol => byId.ContainsKey(symbol.ParentId) ? 0 : 1)
            .FirstOrDefault();
    }

    private static JavaTypeResolution ResolveLocalType(
        JavaTypeReference typeReference,
        JavaFileTypeContext context,
        IReadOnlyDictionary<string, JavaTypeEntry[]> typesByQualified,
        IReadOnlyDictionary<string, JavaTypeEntry[]> typesByName)
    {
        if (typeReference.TargetTypeName.Length == 0
            || ScalarTypes.Contains(typeReference.TargetTypeName))
        {
            return new JavaTypeResolution(null, Ambiguous: false);
        }

        var targetText = StripTypeDecorations(typeReference.TargetTypeText);
        if (targetText.Contains('.', StringComparison.Ordinal)
            && TryResolveUniqueQualified(targetText, typesByQualified) is { } exact)
        {
            return new JavaTypeResolution(exact, Ambiguous: false);
        }

        var explicitImports = context.SingleImports
            .Where(import => SimpleTypeName(import) == typeReference.TargetTypeName)
            .Select(import => TryResolveUniqueQualified(import, typesByQualified))
            .Where(entry => entry is not null)
            .Select(entry => entry!)
            .DistinctBy(entry => entry.Symbol.SymbolId)
            .ToArray();
        if (explicitImports.Length == 1)
        {
            return new JavaTypeResolution(explicitImports[0], Ambiguous: false);
        }
        if (explicitImports.Length > 1)
        {
            return new JavaTypeResolution(null, Ambiguous: true);
        }

        if (context.PackageName.Length > 0
            && TryResolveUniqueQualified($"{context.PackageName}.{typeReference.TargetTypeName}", typesByQualified) is { } samePackage)
        {
            return new JavaTypeResolution(samePackage, Ambiguous: false);
        }

        var wildcardMatches = context.WildcardImports
            .Select(import => TryResolveUniqueQualified($"{import}.{typeReference.TargetTypeName}", typesByQualified))
            .Where(entry => entry is not null)
            .Select(entry => entry!)
            .DistinctBy(entry => entry.Symbol.SymbolId)
            .ToArray();
        if (wildcardMatches.Length == 1)
        {
            return new JavaTypeResolution(wildcardMatches[0], Ambiguous: false);
        }
        if (wildcardMatches.Length > 1)
        {
            return new JavaTypeResolution(null, Ambiguous: true);
        }

        if (!typesByName.TryGetValue(typeReference.TargetTypeName, out var candidates))
        {
            return new JavaTypeResolution(null, Ambiguous: false);
        }

        return candidates.Length == 1
            ? new JavaTypeResolution(candidates[0], Ambiguous: false)
            : new JavaTypeResolution(null, Ambiguous: true);
    }

    private static JavaTypeEntry? TryResolveUniqueQualified(
        string qualifiedName,
        IReadOnlyDictionary<string, JavaTypeEntry[]> typesByQualified)
    {
        var normalized = StripTypeDecorations(qualifiedName);
        return typesByQualified.TryGetValue(normalized, out var candidates) && candidates.Length == 1
            ? candidates[0]
            : null;
    }

    private static JavaTypeReference ParseDeclaredType(string typeText)
    {
        var raw = Regex.Replace(typeText, @"\s+", " ").Trim();
        var array = raw.EndsWith("[]", StringComparison.Ordinal);
        var rawWithoutArray = array ? raw[..^2] : raw;
        var outer = SimpleTypeName(rawWithoutArray);
        var open = raw.IndexOf('<');
        var close = raw.LastIndexOf('>');
        var collection = array || CollectionTypes.Contains(outer);
        if (collection && open >= 0 && close > open)
        {
            var firstArgument = FirstTopLevelTypeArgument(raw[(open + 1)..close]);
            return new JavaTypeReference(raw, StripTypeDecorations(firstArgument), SimpleTypeName(firstArgument), true);
        }

        return new JavaTypeReference(raw, StripTypeDecorations(rawWithoutArray), outer, collection);
    }

    private static string FirstTopLevelTypeArgument(string value)
    {
        var depth = 0;
        for (var i = 0; i < value.Length; i++)
        {
            depth += value[i] switch
            {
                '<' => 1,
                '>' => -1,
                _ => 0,
            };
            if (value[i] == ',' && depth == 0)
            {
                return value[..i].Trim();
            }
        }
        return value.Trim();
    }

    private static string SimpleTypeName(string value)
    {
        var text = StripTypeDecorations(value);
        var generic = text.IndexOf('<');
        if (generic >= 0)
        {
            text = text[..generic];
        }
        text = text.TrimEnd('[', ']', '?').Trim();
        var dot = Math.Max(text.LastIndexOf('.'), text.LastIndexOf('$'));
        return dot >= 0 ? text[(dot + 1)..] : text;
    }

    private static string StripTypeDecorations(string value)
    {
        var text = value.Trim();
        if (text.StartsWith("? extends ", StringComparison.Ordinal))
        {
            text = text["? extends ".Length..].Trim();
        }
        else if (text.StartsWith("? super ", StringComparison.Ordinal))
        {
            text = text["? super ".Length..].Trim();
        }

        while (text.EndsWith("[]", StringComparison.Ordinal) || text.EndsWith("...", StringComparison.Ordinal))
        {
            text = text.EndsWith("[]", StringComparison.Ordinal) ? text[..^2].Trim() : text[..^3].Trim();
        }
        return text.TrimEnd('?').Trim();
    }

    private static JavaFileTypeContext BuildTypeContext(CallResolver.FileInput file, IReadOnlyList<CodeSymbolFact> fileSymbols)
    {
        var packageName = fileSymbols
            .Where(symbol => symbol.Kind == "package")
            .Select(QualifiedName)
            .FirstOrDefault(qualified => qualified.Length > 0) ?? "";
        var imports = file.Parsed.Edges
            .Where(edge => edge.Kind == CodeEdgeKinds.Imports
                && (edge.FromQualified == file.RelativePath || edge.FromQualified == file.Parsed.FilePath))
            .Select(edge => edge.ToName.Trim())
            .Where(import => import.Length > 0 && !import.StartsWith("static ", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return new JavaFileTypeContext(
            packageName,
            imports.Where(import => !import.EndsWith(".*", StringComparison.Ordinal)).ToArray(),
            imports.Where(import => import.EndsWith(".*", StringComparison.Ordinal))
                .Select(import => import[..^2])
                .ToArray());
    }

    private static string QualifiedName(CodeSymbolFact symbol)
    {
        var symKey = symbol.SymKey;
        var colon = symKey.IndexOf(':');
        var hash = symKey.LastIndexOf('#');
        return colon >= 0 && hash > colon ? symKey[(colon + 1)..hash] : "";
    }

    private static CodeSymbolFact? ResolveMethod(ParsedJavaTypeUseSyntax typeUse, IReadOnlyList<CodeSymbolFact> fileSymbols) =>
        fileSymbols
            .Where(symbol => symbol.Kind == "method"
                && symbol.Name == typeUse.DeclaringName
                && Arity(symbol) == typeUse.DeclaringArity
                && symbol.StartLine <= typeUse.StartLine
                && symbol.EndLine >= typeUse.EndLine)
            .OrderBy(symbol => symbol.EndLine - symbol.StartLine)
            .FirstOrDefault();

    private static CodeSymbolFact? ResolveTypeUseMethod(
        ParsedJavaTypeUseSyntax typeUse,
        IReadOnlyList<CodeSymbolFact> fileSymbols) =>
        typeUse.UsageKind == "local"
            ? ResolveContainingMethod(typeUse.StartLine, typeUse.EndLine, fileSymbols)
            : ResolveMethod(typeUse, fileSymbols);

    private static CodeSymbolFact? ResolveContainingMethod(
        int startLine,
        int endLine,
        IReadOnlyList<CodeSymbolFact> fileSymbols) =>
        fileSymbols
            .Where(symbol => symbol.Kind == "method"
                && symbol.StartLine <= startLine
                && symbol.EndLine >= endLine)
            .OrderBy(symbol => symbol.EndLine - symbol.StartLine)
            .FirstOrDefault();

    private static int Arity(CodeSymbolFact symbol)
    {
        var hash = symbol.SymKey.LastIndexOf('#');
        return hash >= 0 && int.TryParse(symbol.SymKey[(hash + 1)..], out var arity) ? arity : 0;
    }

    private static bool IsStateMember(string memberName, IReadOnlyList<AnnotationSyntax> annotations) =>
        memberName.Equals("state", StringComparison.OrdinalIgnoreCase)
        || memberName.Equals("status", StringComparison.OrdinalIgnoreCase)
        || memberName.EndsWith("State", StringComparison.Ordinal)
        || memberName.EndsWith("Status", StringComparison.Ordinal)
        || annotations.Any(annotation => SimpleName(annotation.Name) is "State" or "Status" or "StateField" or "StatusField");

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> BuildVariableTypes(
        ParsedJavaSourceSyntax syntax,
        IReadOnlyList<CodeSymbolFact> fileSymbols,
        IReadOnlyList<JavaTypeEntry> typeEntries,
        JavaFileTypeContext typeContext,
        IReadOnlyDictionary<string, JavaTypeEntry[]> typesByQualified,
        IReadOnlyDictionary<string, JavaTypeEntry[]> typesByName)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var typeUse in syntax.TypeUses.Where(item => item.UsageKind is "parameter" or "local"))
        {
            var method = ResolveTypeUseMethod(typeUse, fileSymbols);
            if (method is null)
            {
                continue;
            }
            var resolved = ResolveLocalType(
                ParseDeclaredType(typeUse.TypeText),
                typeContext,
                typesByQualified,
                typesByName);
            if (resolved.Target is null)
            {
                continue;
            }
            if (!result.TryGetValue(method.SymbolId, out var variables))
            {
                variables = new Dictionary<string, string>(StringComparer.Ordinal);
                result[method.SymbolId] = variables;
            }
            variables[typeUse.Name] = resolved.Target.Symbol.SymbolId;
        }
        return result.ToDictionary(
            item => item.Key,
            item => (IReadOnlyDictionary<string, string>)item.Value,
            StringComparer.Ordinal);
    }

    private static string ResolveMutationOwner(
        ParsedJavaStateMutationSyntax mutation,
        CodeSymbolFact method,
        IReadOnlyDictionary<string, string> variableTypes)
    {
        var receiver = NormalizeReceiver(mutation.ReceiverText);
        if (receiver.Length == 0 || receiver == "this")
        {
            return method.ParentId;
        }
        return variableTypes.TryGetValue(receiver, out var symbolId) ? symbolId : "";
    }

    private static NormalizedStateValue? NormalizeStateValue(string valueText)
    {
        var raw = Regex.Replace(valueText, @"\s+", " ").Trim();
        if (raw.Length == 0)
        {
            return null;
        }

        var enumCall = Regex.Match(
            raw,
            @"(?<type>[A-Za-z_$][A-Za-z0-9_$]*)\.(?<member>[A-Z][A-Z0-9_]*)(?:\.(?<method>getCode|getDbCode|name)\s*\(\s*\))?$",
            RegexOptions.CultureInvariant);
        if (enumCall.Success)
        {
            var member = enumCall.Groups["member"].Value;
            var method = enumCall.Groups["method"].Value;
            var encoding = method switch
            {
                "getCode" => "enum.getCode",
                "getDbCode" => "enum.getDbCode",
                "name" => "enum.name",
                _ => "enum.member",
            };
            return new NormalizedStateValue(
                member,
                raw,
                encoding,
                enumCall.Groups["type"].Value,
                member);
        }

        if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
        {
            var parsed = ScalarValue(raw)?.ToString() ?? raw[1..^1];
            return new NormalizedStateValue(parsed, raw, "string", "", "");
        }

        if (long.TryParse(raw.TrimEnd('L', 'l'), out var integer))
        {
            return new NormalizedStateValue($"CODE_{integer}", raw, "numeric", "", "");
        }

        return null;
    }

    private static StateGuardObservation? ParseAllowedStateGuard(string conditionText)
    {
        var source = NormalizePredicate(conditionText);
        var stateEquals = ParseStateEquals(source, negated: false);
        if (stateEquals is not null)
        {
            return stateEquals;
        }

        if (source.StartsWith('!'))
        {
            stateEquals = ParseStateEquals(source[1..].Trim(), negated: true);
            if (stateEquals is not null)
            {
                return stateEquals;
            }
        }

        foreach (var op in new[] { "!=", "==" })
        {
            var index = source.IndexOf(op, StringComparison.Ordinal);
            if (index < 0)
            {
                continue;
            }
            var left = source[..index].Trim();
            var right = source[(index + op.Length)..].Trim();
            if (NormalizeStateValue(left) is { } leftValue
                && TryParseStateAccessor(right) is { } rightAccessor)
            {
                return op == "!="
                    ? new StateGuardObservation("", rightAccessor.Property, rightAccessor.Receiver, [leftValue.Value], leftValue.Encoding, source)
                    : null;
            }
            if (TryParseStateAccessor(left) is { } leftAccessor
                && NormalizeStateValue(right) is { } rightValue)
            {
                return op == "!="
                    ? new StateGuardObservation("", leftAccessor.Property, leftAccessor.Receiver, [rightValue.Value], rightValue.Encoding, source)
                    : null;
            }
        }

        return null;
    }

    private static StateGuardObservation? ParseStateEquals(string source, bool negated)
    {
        var stringUtils = Regex.Match(
            source,
            @"^StringUtils\.equals\s*\((?<left>.+),(?<right>.+)\)$",
            RegexOptions.CultureInvariant);
        if (stringUtils.Success)
        {
            return ParseEqualsArguments(
                stringUtils.Groups["left"].Value,
                stringUtils.Groups["right"].Value,
                negated,
                source);
        }

        var equals = Regex.Match(
            source,
            @"^(?<left>.+)\.equals\s*\((?<right>.+)\)$",
            RegexOptions.CultureInvariant);
        return equals.Success
            ? ParseEqualsArguments(
                equals.Groups["left"].Value,
                equals.Groups["right"].Value,
                negated,
                source)
            : null;
    }

    private static StateGuardObservation? ParseEqualsArguments(
        string left,
        string right,
        bool negated,
        string source)
    {
        if (!negated)
        {
            return null;
        }
        var leftText = left.Trim();
        var rightText = right.Trim();
        if (NormalizeStateValue(leftText) is { } leftValue
            && TryParseStateAccessor(rightText) is { } rightAccessor)
        {
            return new StateGuardObservation("", rightAccessor.Property, rightAccessor.Receiver, [leftValue.Value], leftValue.Encoding, source);
        }
        if (TryParseStateAccessor(leftText) is { } leftAccessor
            && NormalizeStateValue(rightText) is { } rightValue)
        {
            return new StateGuardObservation("", leftAccessor.Property, leftAccessor.Receiver, [rightValue.Value], rightValue.Encoding, source);
        }
        return null;
    }

    private static (string Receiver, string Property)? TryParseStateAccessor(string expression)
    {
        var text = expression.Trim();
        var getter = Regex.Match(
            text,
            @"^(?:(?<receiver>[A-Za-z_$][A-Za-z0-9_$]*)\.)?get(?<property>[A-Za-z_$][A-Za-z0-9_$]*)\s*\(\s*\)(?:\.(?:getCode|getDbCode|name)\s*\(\s*\))?$",
            RegexOptions.CultureInvariant);
        if (getter.Success)
        {
            var property = LowerCamel(getter.Groups["property"].Value);
            return IsStateMember(property, [])
                ? (getter.Groups["receiver"].Success ? getter.Groups["receiver"].Value : "this", property)
                : null;
        }

        var fieldCall = Regex.Match(
            text,
            @"^(?:(?<receiver>[A-Za-z_$][A-Za-z0-9_$]*)\.)?(?<property>[A-Za-z_$][A-Za-z0-9_$]*)(?:\.(?:getCode|getDbCode|name)\s*\(\s*\))?$",
            RegexOptions.CultureInvariant);
        if (fieldCall.Success)
        {
            var property = LowerCamel(fieldCall.Groups["property"].Value);
            return IsStateMember(property, [])
                ? (fieldCall.Groups["receiver"].Success ? fieldCall.Groups["receiver"].Value : "this", property)
                : null;
        }
        return null;
    }

    private static string NormalizeReceiver(string value)
    {
        var text = value.Trim();
        return text.StartsWith("this.", StringComparison.Ordinal) ? "this" : text;
    }

    private static string LowerCamel(string value)
    {
        var normalized = new string(value.Where(char.IsLetterOrDigit).ToArray());
        return normalized.Length == 0 || !char.IsLetter(normalized[0])
            ? ""
            : char.ToLowerInvariant(normalized[0]) + normalized[1..];
    }

    private static IEnumerable<(string Operator, object? Value)> ValidationConstraints(AnnotationSyntax annotation)
    {
        var name = SimpleName(annotation.Name);
        switch (name)
        {
            case "NotNull":
            case "NotBlank":
            case "NotEmpty":
                yield return ("required", true);
                break;
            case "Size":
                if (NamedArgument(annotation.Arguments, "min") is { Length: > 0 } min)
                {
                    yield return ("minLength", ScalarValue(min));
                }
                if (NamedArgument(annotation.Arguments, "max") is { Length: > 0 } max)
                {
                    yield return ("maxLength", ScalarValue(max));
                }
                break;
            case "Min":
            case "DecimalMin":
                yield return ("min", ScalarValue(DirectArgument(annotation.Arguments)));
                break;
            case "Max":
            case "DecimalMax":
                yield return ("max", ScalarValue(DirectArgument(annotation.Arguments)));
                break;
            case "Pattern":
                var regex = NamedArgument(annotation.Arguments, "regexp");
                yield return ("pattern", ScalarValue(regex.Length > 0 ? regex : DirectArgument(annotation.Arguments)));
                break;
        }
    }

    private static IEnumerable<(string Operator, object? Value)> PersistenceConstraints(AnnotationSyntax annotation)
    {
        var name = SimpleName(annotation.Name);
        if (name is "Column" or "JoinColumn")
        {
            if (NamedArgument(annotation.Arguments, "nullable") is { Length: > 0 } nullable)
            {
                yield return ("nullable", ScalarValue(nullable));
            }
            if (NamedArgument(annotation.Arguments, "unique") is { Length: > 0 } unique)
            {
                yield return ("unique", ScalarValue(unique));
            }
        }

        if (name is "ManyToOne" or "OneToOne")
        {
            yield return ("association", name);
            if (NamedArgument(annotation.Arguments, "optional") is { Length: > 0 } optional)
            {
                yield return ("optional", ScalarValue(optional));
            }
        }
        else if (name is "OneToMany" or "ManyToMany")
        {
            yield return ("association", name);
        }
    }

    private static IReadOnlyList<AnnotationSyntax> ParseAnnotations(string declaration)
    {
        var result = new List<AnnotationSyntax>();
        for (var index = 0; index < declaration.Length; index++)
        {
            if (declaration[index] != '@')
            {
                continue;
            }

            var start = index++;
            while (index < declaration.Length
                && (char.IsLetterOrDigit(declaration[index]) || declaration[index] is '_' or '.' or '$'))
            {
                index++;
            }
            var name = declaration[(start + 1)..index];
            if (name.Length == 0)
            {
                continue;
            }

            while (index < declaration.Length && char.IsWhiteSpace(declaration[index]))
            {
                index++;
            }
            var arguments = "";
            if (index < declaration.Length && declaration[index] == '(')
            {
                var close = FindBalancedClose(declaration, index);
                if (close < 0)
                {
                    continue;
                }
                arguments = declaration[(index + 1)..close].Trim();
                index = close;
            }
            else
            {
                index--;
            }

            result.Add(new AnnotationSyntax(name, arguments, declaration[start..(index + 1)].Trim()));
        }
        return result;
    }

    private static int FindBalancedClose(string text, int open)
    {
        var depth = 0;
        var quoted = false;
        var escaped = false;
        for (var index = open; index < text.Length; index++)
        {
            var ch = text[index];
            if (quoted)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (ch == '\\')
                {
                    escaped = true;
                }
                else if (ch == '"')
                {
                    quoted = false;
                }
                continue;
            }
            if (ch == '"')
            {
                quoted = true;
            }
            else if (ch == '(')
            {
                depth++;
            }
            else if (ch == ')' && --depth == 0)
            {
                return index;
            }
        }
        return -1;
    }

    private static string NamedArgument(string arguments, string name)
    {
        foreach (var part in SplitTopLevel(arguments))
        {
            var equals = part.IndexOf('=');
            if (equals > 0 && part[..equals].Trim() == name)
            {
                return part[(equals + 1)..].Trim();
            }
        }
        return "";
    }

    private static string DirectArgument(string arguments) =>
        SplitTopLevel(arguments).FirstOrDefault(part => !part.Contains('='))?.Trim() ?? "";

    private static IEnumerable<string> SplitTopLevel(string value)
    {
        var start = 0;
        var depth = 0;
        var quoted = false;
        var escaped = false;
        for (var index = 0; index < value.Length; index++)
        {
            var ch = value[index];
            if (quoted)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (ch == '\\')
                {
                    escaped = true;
                }
                else if (ch == '"')
                {
                    quoted = false;
                }
                continue;
            }
            switch (ch)
            {
                case '"': quoted = true; break;
                case '(':
                case '{':
                case '[':
                case '<': depth++; break;
                case ')':
                case '}':
                case ']':
                case '>': depth--; break;
                case ',' when depth == 0:
                    yield return value[start..index];
                    start = index + 1;
                    break;
            }
        }
        if (start <= value.Length)
        {
            yield return value[start..];
        }
    }

    private static object? ScalarValue(string value)
    {
        var normalized = value.Trim();
        if (normalized.Length == 0)
        {
            return null;
        }
        if (normalized is "true" or "false")
        {
            return normalized == "true";
        }
        if (long.TryParse(normalized.TrimEnd('L', 'l'), out var integer))
        {
            return integer;
        }
        if (normalized.Length >= 2 && normalized[0] == '"' && normalized[^1] == '"')
        {
            try
            {
                return JsonSerializer.Deserialize<string>(normalized);
            }
            catch (JsonException)
            {
                return normalized[1..^1];
            }
        }
        return normalized;
    }

    private static string LastIdentifier(string value)
    {
        var matches = Regex.Matches(value, @"[A-Za-z_$][A-Za-z0-9_$]*");
        return matches.Count == 0 ? "" : matches[^1].Value;
    }

    private static (string Kind, string Source, string Message)? RejectEffect(string consequenceText)
    {
        var source = Regex.Replace(consequenceText, @"\s+", " ").Trim();
        if (source.Length == 0)
        {
            return null;
        }
        if (Regex.IsMatch(source, @"\bthrow\b", RegexOptions.CultureInvariant))
        {
            return ("throw", source, FirstStringLiteral(source));
        }
        if (Regex.IsMatch(source, @"\breturn\b", RegexOptions.CultureInvariant)
            && Regex.IsMatch(source, @"\b(fail|failure|error|forbid|forbidden|reject|denied|deny)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return ("error_return", source, FirstStringLiteral(source));
        }
        return null;
    }

    private static string NormalizePredicate(string conditionText)
    {
        var text = Regex.Replace(conditionText, @"\s+", " ").Trim();
        while (text.Length >= 2 && text[0] == '(' && text[^1] == ')' && BalancedInner(text))
        {
            text = text[1..^1].Trim();
        }
        return text;
    }

    private static bool BalancedInner(string text)
    {
        var depth = 0;
        var quoted = false;
        var escaped = false;
        for (var index = 1; index < text.Length - 1; index++)
        {
            var ch = text[index];
            if (quoted)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (ch == '\\')
                {
                    escaped = true;
                }
                else if (ch == '"')
                {
                    quoted = false;
                }
                continue;
            }
            if (ch == '"')
            {
                quoted = true;
            }
            else if (ch == '(')
            {
                depth++;
            }
            else if (ch == ')' && --depth < 0)
            {
                return false;
            }
        }
        return depth == 0;
    }

    private static string FirstStringLiteral(string value)
    {
        var match = Regex.Match(value, "\"(?:\\\\.|[^\"\\\\])*\"");
        if (!match.Success)
        {
            return "";
        }
        try
        {
            return JsonSerializer.Deserialize<string>(match.Value) ?? "";
        }
        catch (JsonException)
        {
            return match.Value.Trim('"');
        }
    }

    private static string SimpleName(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot >= 0 ? name[(dot + 1)..] : name;
    }

    private static void AddTypedReferenceClaim(
        List<CodeSemanticClaimFact> claims,
        List<CodeDiagnosticFact> diagnostics,
        HashSet<string> emitted,
        CallResolver.FileInput file,
        string subjectId,
        string declaringSymbolId,
        string ownerSymbolId,
        string usageKind,
        string member,
        JavaTypeReference typeReference,
        JavaTypeResolution resolution,
        int startLine,
        int endLine,
        string evidence)
    {
        if (typeReference.TargetTypeName.Length == 0
            || ScalarTypes.Contains(typeReference.TargetTypeName))
        {
            return;
        }

        if (resolution.Target is null)
        {
            if (resolution.Ambiguous)
            {
                diagnostics.Add(new CodeDiagnosticFact(
                    $"diag:semantic-typed-reference-ambiguous:{file.FileId}:{startLine}:{member}",
                    file.FileId,
                    "semantic_typed_reference_ambiguous",
                    $"Java type reference '{typeReference.TargetTypeText}' for {usageKind} '{member}' in {file.RelativePath}:{startLine} is ambiguous; no typed_reference claim was emitted.",
                    "warning"));
            }
            return;
        }

        var targetType = resolution.Target.Symbol;
        if (!RelationTargetKinds.Contains(targetType.Kind))
        {
            return;
        }

        AddClaim(
            claims,
            emitted,
            file.FileId,
            subjectId,
            CodeSemanticClaimKinds.TypedReference,
            new Dictionary<string, object?>
            {
                ["collection"] = typeReference.Collection,
                ["declaringSymbolId"] = declaringSymbolId,
                ["member"] = member,
                ["ownerSymbolId"] = ownerSymbolId,
                ["rawType"] = typeReference.RawType,
                ["resolvedTypeName"] = targetType.Name,
                ["resolvedTypeQualifiedName"] = resolution.Target.QualifiedName,
                ["resolvedTypeSymbolId"] = targetType.SymbolId,
                ["usageKind"] = usageKind,
            },
            startLine,
            endLine,
            TreeSitterResolver,
            evidence);
    }

    private static void AddClaim(
        List<CodeSemanticClaimFact> claims,
        HashSet<string> emitted,
        string fileId,
        string subjectId,
        string kind,
        IReadOnlyDictionary<string, object?> payload,
        int startLine,
        int endLine,
        string resolver,
        string evidence)
    {
        var payloadJson = CodeSemanticClaimIdentity.CanonicalizePayload(JsonSerializer.Serialize(payload));
        var claimId = CodeSemanticClaimIdentity.Create(fileId, kind, payloadJson, startLine, endLine);
        if (!emitted.Add(claimId))
        {
            return;
        }

        claims.Add(new CodeSemanticClaimFact(
            claimId,
            subjectId,
            kind,
            payloadJson,
            fileId,
            startLine,
            endLine,
            SyntaxConfidence,
            resolver,
            Regex.Replace(evidence, @"\s+", " ").Trim()));
    }

    private sealed class SemanticClaimContentComparer : IEqualityComparer<(string SubjectId, string Kind, string PayloadJson)>
    {
        public static readonly SemanticClaimContentComparer Instance = new();

        public bool Equals(
            (string SubjectId, string Kind, string PayloadJson) x,
            (string SubjectId, string Kind, string PayloadJson) y) =>
            StringComparer.Ordinal.Equals(x.SubjectId, y.SubjectId)
            && StringComparer.Ordinal.Equals(x.Kind, y.Kind)
            && StringComparer.Ordinal.Equals(x.PayloadJson, y.PayloadJson);

        public int GetHashCode((string SubjectId, string Kind, string PayloadJson) value) =>
            HashCode.Combine(
                StringComparer.Ordinal.GetHashCode(value.SubjectId),
                StringComparer.Ordinal.GetHashCode(value.Kind),
                StringComparer.Ordinal.GetHashCode(value.PayloadJson));
    }
}

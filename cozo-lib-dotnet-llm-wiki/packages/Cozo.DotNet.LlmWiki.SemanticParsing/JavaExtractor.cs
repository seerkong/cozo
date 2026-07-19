namespace Cozo.DotNet.LlmWiki.SemanticParsing;

/// <summary>
/// Java symbol/edge extractor. Query-driven like CSharpExtractor/TypeScriptExtractor:
/// declarations, package/imports, heritage clauses, modifiers and call sites are captured
/// by one tree-sitter query, then normalized into the shared parsed fact model.
/// </summary>
internal static class JavaExtractor
{
    private const string Contains = "CONTAINS";
    private const string HasMethod = "HAS_METHOD";
    private const string HasProperty = "HAS_PROPERTY";
    private const string Extends = "EXTENDS";
    private const string Implements = "IMPLEMENTS";
    private const string Imports = "IMPORTS";

    private const double StructuralConfidence = 0.9;
    private const string StructuralEvidence = "syntax";

    private const int MaxSignatureLength = 200;

    private static readonly HashSet<string> AccessModifiers = new(StringComparer.Ordinal)
    {
        "public", "private", "protected",
    };

    private static readonly HashSet<string> TypeKindsWithMembers = new(StringComparer.Ordinal)
    {
        "class", "interface", "enum", "record", "annotation",
    };

    private static readonly HashSet<string> HeritageOwnerKinds = new(StringComparer.Ordinal)
    {
        "class", "interface", "enum", "record", "annotation",
    };

    public const string QuerySource = """
        (package_declaration (scoped_identifier) @package.name) @package.decl
        (package_declaration (identifier) @package.name) @package.decl
        (import_declaration) @import
        (class_declaration name: (identifier) @class.name) @class.decl
        (interface_declaration name: (identifier) @interface.name) @interface.decl
        (enum_declaration name: (identifier) @enum.name) @enum.decl
        (record_declaration name: (identifier) @record.name parameters: (formal_parameters) @record.params) @record.decl
        (annotation_type_declaration name: (identifier) @annotation.name) @annotation.decl
        (method_declaration name: (identifier) @method.name parameters: (formal_parameters) @method.params) @method.decl
        (constructor_declaration name: (identifier) @constructor.name parameters: (formal_parameters) @constructor.params) @constructor.decl
        (field_declaration declarator: (variable_declarator name: (identifier) @field.name)) @field.decl
        (enum_constant name: (identifier) @enumconstant.name) @enumconstant.decl
        (superclass) @extends
        (super_interfaces) @implements
        (extends_interfaces) @extends
        (modifiers) @modifier
        (method_invocation object: _ @call.receiver name: (identifier) @call.target arguments: (argument_list) @call.args) @call.site
        (method_invocation !object name: (identifier) @call.target arguments: (argument_list) @call.args) @call.site
        (object_creation_expression type: (_) @new.type arguments: (argument_list)? @new.args) @new.site
        (assignment_expression left: (field_access) @assign.lhs)
        (field_access object: _ @access.receiver field: (_) @access.target) @access.site
        """;

    private sealed class Decl
    {
        public required string Kind;
        public required string Name;
        public int StartByte;
        public int EndByte;
        public int EffectiveEndByte;
        public int NameStartByte;
        public int StartLine;
        public int EndLine;
        public string Signature = "";
        public int Arity;
        public List<string> Access = [];
        public Decl? Parent;
        public List<Decl> Children = [];
        public string Qualified = "";
    }

    private static readonly HashSet<string> CallerOwnerKinds = new(StringComparer.Ordinal)
    {
        "class", "interface", "enum", "record", "annotation",
        "method", "constructor", "field",
    };

    public static (IReadOnlyList<ParsedSymbol> Symbols, IReadOnlyList<ParsedEdge> Edges, IReadOnlyList<ParsedCallSite> CallSites) Extract(
        string filePath,
        int sourceByteLength,
        IReadOnlyList<TreeSitterQueryMatchResult> matches)
    {
        var decls = new List<Decl>();
        var extendsClauses = new List<TreeSitterQueryCapture>();
        var implementsClauses = new List<TreeSitterQueryCapture>();
        var imports = new List<TreeSitterQueryCapture>();
        var modifiers = new List<TreeSitterQueryCapture>();
        var callSiteCollector = new CallSiteCollector();

        foreach (var match in matches)
        {
            if (callSiteCollector.TryCollect(match))
            {
                continue;
            }

            TreeSitterQueryCapture? decl = null, name = null, parameters = null;
            var kind = "";
            foreach (var capture in match.Captures)
            {
                if (capture.CaptureName.EndsWith(".decl", StringComparison.Ordinal))
                {
                    decl = capture;
                    kind = capture.CaptureName[..^".decl".Length];
                }
                else if (capture.CaptureName.EndsWith(".name", StringComparison.Ordinal))
                {
                    name = capture;
                }
                else if (capture.CaptureName.EndsWith(".params", StringComparison.Ordinal))
                {
                    parameters = capture;
                }
                else
                {
                    switch (capture.CaptureName)
                    {
                        case "extends": extendsClauses.Add(capture); break;
                        case "implements": implementsClauses.Add(capture); break;
                        case "import": imports.Add(capture); break;
                        case "modifier": modifiers.Add(capture); break;
                    }
                }
            }

            if (decl is null || name is null)
            {
                continue;
            }

            var package = kind == "package";
            decls.Add(new Decl
            {
                Kind = kind switch
                {
                    "enumconstant" => "field",
                    _ => kind,
                },
                Name = package ? ParsePackageName(decl.Text, name.Text) : name.Text,
                StartByte = decl.StartByte,
                EndByte = decl.EndByte,
                EffectiveEndByte = package ? sourceByteLength : decl.EndByte,
                NameStartByte = name.StartByte,
                StartLine = decl.StartLine,
                EndLine = decl.EndLine,
                Signature = FirstLine(decl.Text),
                Arity = parameters is null ? 0 : CountParameters(parameters.Text),
            });
        }

        foreach (var decl in decls)
        {
            Decl? best = null;
            foreach (var candidate in decls)
            {
                if (ReferenceEquals(candidate, decl) || !StrictlyContains(candidate, decl))
                {
                    continue;
                }

                if (best is null || Span(candidate) < Span(best))
                {
                    best = candidate;
                }
            }

            decl.Parent = best;
            best?.Children.Add(decl);
        }

        foreach (var modifier in modifiers)
        {
            foreach (var token in ParseAccessModifiers(modifier.Text))
            {
                foreach (var decl in decls)
                {
                    if (decl.StartByte <= modifier.StartByte && modifier.StartByte < decl.NameStartByte)
                    {
                        decl.Access.Add(token);
                    }
                }
            }
        }

        var roots = decls.Where(decl => decl.Parent is null).OrderBy(decl => decl.StartByte).ToList();
        foreach (var root in roots)
        {
            Qualify(root, prefix: null);
        }

        var edges = new List<ParsedEdge>();
        foreach (var decl in decls)
        {
            edges.Add(new ParsedEdge(
                decl.Parent?.Qualified ?? filePath,
                decl.Qualified,
                Contains,
                StructuralConfidence,
                StructuralEvidence));

            if (decl.Parent is { } owner && TypeKindsWithMembers.Contains(owner.Kind))
            {
                if (decl.Kind is "method" or "constructor")
                {
                    edges.Add(new ParsedEdge(owner.Qualified, decl.Qualified, HasMethod, StructuralConfidence, StructuralEvidence));
                }
                else if (decl.Kind == "field")
                {
                    edges.Add(new ParsedEdge(owner.Qualified, decl.Qualified, HasProperty, StructuralConfidence, StructuralEvidence));
                }
            }
        }

        AddHeritageEdges(edges, decls, extendsClauses, "extends", Extends);
        AddHeritageEdges(edges, decls, implementsClauses, "implements", Implements);

        foreach (var import in imports)
        {
            if (ParseImportTarget(import.Text) is { Length: > 0 } target)
            {
                edges.Add(new ParsedEdge(filePath, target, Imports, StructuralConfidence, StructuralEvidence));
            }
        }

        var callSites = callSiteCollector.Build(position =>
            FindInnermostOwner(decls, position, CallerOwnerKinds)?.Qualified is { Length: > 0 } qualified ? qualified : "<file>");

        return (roots.Select(ToParsedSymbol).ToArray(), edges, callSites);
    }

    private static void AddHeritageEdges(
        List<ParsedEdge> edges,
        List<Decl> decls,
        List<TreeSitterQueryCapture> clauses,
        string keyword,
        string edgeKind)
    {
        foreach (var clause in clauses)
        {
            var owner = FindInnermostOwner(decls, clause.StartByte, HeritageOwnerKinds);
            if (owner is null)
            {
                continue;
            }

            foreach (var target in ParseHeritageTargets(clause.Text, keyword))
            {
                edges.Add(new ParsedEdge(owner.Qualified, target, edgeKind, StructuralConfidence, StructuralEvidence));
            }
        }
    }

    private static void Qualify(Decl decl, string? prefix)
    {
        decl.Qualified = prefix is null ? decl.Name : $"{prefix}.{decl.Name}";
        decl.Children.Sort((a, b) => a.StartByte.CompareTo(b.StartByte));
        foreach (var child in decl.Children)
        {
            Qualify(child, decl.Qualified);
        }
    }

    private static ParsedSymbol ToParsedSymbol(Decl decl)
    {
        var visibility = string.Join(" ", decl.Access.Distinct(StringComparer.Ordinal));
        return new ParsedSymbol(
            decl.Name,
            decl.Kind,
            decl.StartLine,
            decl.EndLine,
            decl.Signature,
            visibility,
            Exported: decl.Access.Contains("public"),
            Children: decl.Children.Select(ToParsedSymbol).ToArray())
        {
            SymKey = $"java:{decl.Qualified}#{decl.Arity}",
        };
    }

    private static bool StrictlyContains(Decl outer, Decl inner) =>
        outer.StartByte <= inner.StartByte
        && inner.EffectiveEndByte <= outer.EffectiveEndByte
        && Span(outer) > Span(inner);

    private static int Span(Decl decl) => decl.EffectiveEndByte - decl.StartByte;

    private static Decl? FindInnermostOwner(List<Decl> decls, int position, HashSet<string> kinds)
    {
        Decl? best = null;
        foreach (var decl in decls)
        {
            if (!kinds.Contains(decl.Kind) || position < decl.StartByte || position >= decl.EffectiveEndByte)
            {
                continue;
            }

            if (best is null || Span(decl) < Span(best))
            {
                best = decl;
            }
        }

        return best;
    }

    private static string FirstLine(string declarationText)
    {
        var newline = declarationText.IndexOf('\n');
        var line = (newline >= 0 ? declarationText[..newline] : declarationText).Trim();
        return line.Length > MaxSignatureLength ? line[..MaxSignatureLength] : line;
    }

    private static int CountParameters(string parameterListText)
    {
        var inner = parameterListText.Trim();
        if (inner.StartsWith('(')) inner = inner[1..];
        if (inner.EndsWith(')')) inner = inner[..^1];
        inner = inner.Trim();
        if (inner.Length == 0)
        {
            return 0;
        }

        var depth = 0;
        var count = 1;
        foreach (var ch in inner)
        {
            if (ch is '<' or '(' or '[' or '{')
            {
                depth++;
            }
            else if (ch is '>' or ')' or ']' or '}')
            {
                depth--;
            }
            else if (ch == ',' && depth == 0)
            {
                count++;
            }
        }

        return count;
    }

    private static IEnumerable<string> ParseHeritageTargets(string clauseText, string keyword)
    {
        var text = clauseText.Trim();
        if (text.StartsWith(keyword, StringComparison.Ordinal))
        {
            text = text[keyword.Length..];
        }

        foreach (var entry in SplitTopLevel(text))
        {
            var name = entry;
            var cut = name.AsSpan().IndexOfAny('<', '(');
            if (cut >= 0)
            {
                name = name[..cut];
            }

            name = name.Trim();
            if (name.Length > 0)
            {
                yield return name;
            }
        }
    }

    private static IEnumerable<string> SplitTopLevel(string text)
    {
        var depth = 0;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch is '<' or '(' or '[' or '{')
            {
                depth++;
            }
            else if (ch is '>' or ')' or ']' or '}')
            {
                depth--;
            }
            else if (ch == ',' && depth == 0)
            {
                yield return text[start..i].Trim();
                start = i + 1;
            }
        }

        if (start < text.Length)
        {
            yield return text[start..].Trim();
        }
    }

    private static IEnumerable<string> ParseAccessModifiers(string modifiersText)
    {
        foreach (var token in modifiersText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (AccessModifiers.Contains(token))
            {
                yield return token;
            }
        }
    }

    private static string ParsePackageName(string declarationText, string capturedName)
    {
        var text = declarationText.Trim();
        if (text.StartsWith("package", StringComparison.Ordinal))
        {
            text = text["package".Length..].Trim();
        }

        text = text.TrimEnd(';').Trim();
        return text.Length == 0 ? capturedName : text;
    }

    private static string? ParseImportTarget(string importText)
    {
        var text = importText.Trim();
        if (text.StartsWith("import", StringComparison.Ordinal))
        {
            text = text["import".Length..].Trim();
        }

        if (text.StartsWith("static", StringComparison.Ordinal))
        {
            text = text["static".Length..].Trim();
        }

        text = text.TrimEnd(';').Trim();
        return text.Length == 0 ? null : text;
    }
}

namespace Cozo.DotNet.LlmWiki.SemanticParsing;

/// <summary>
/// TypeScript/JavaScript symbol/edge extractor (design.md §3, T2.2). Query-driven like
/// CSharpExtractor: a single tree-sitter query captures declarations, heritage clauses,
/// import statements, export statements and accessibility modifiers; nesting is rebuilt
/// from byte-range containment. JavaScript files are parsed with the same typescript
/// grammar (track findings): plain JS is a syntactic subset for the constructs covered
/// here, so both language ids share this extractor and the "ts:" sym_key prefix.
///
/// Produces:
/// - ParsedSymbol tree with v2 kinds (class — including abstract classes — /interface/enum/
///   type_alias/function/method/property), visibility from accessibility modifiers
///   (public/private/protected), exported = wrapped in an export statement or explicit
///   public modifier, signature = first declaration line (truncated),
///   sym_key "ts:&lt;file-scoped nested qualified name&gt;#&lt;arity&gt;" (TS has no namespace
///   qualifier here; nesting inside the file qualifies, e.g. "ts:Greeter.greet#1").
/// - Structural edges: CONTAINS (file→top-level, parent→child), HAS_METHOD/HAS_PROPERTY
///   (type→member) at confidence 0.9 evidence "syntax"; EXTENDS (class extends_clause and
///   interface extends_type_clause) and IMPLEMENTS (implements_clause) also at 0.9
///   evidence "syntax" because TS distinguishes them syntactically (unlike the C# base_list
///   heuristic); IMPORTS (file→module specifier string, one edge per import statement).
/// - Call sites (call-resolution track T1.1): call_expression (identifier / member_expression
///   forms), new_expression (kind=new, generic-stripped constructor), member_expression
///   reads/writes (assignment lhs = write); de-dup rules in CallSiteCollector.
///
/// Known non-coverage (recorded in track findings): enum members, arrow functions assigned
/// to non-top-level variables (module-level const/let declarations are covered as
/// kind=constant since T3.1), TS namespace (internal module) declarations, getter/setter pairing,
/// re-exports ("export { x } from ..." produces no IMPORTS edge), "export { x }" of an
/// earlier declaration does not mark it exported, JSX/TSX (needs the tsx grammar).
/// </summary>
internal static class TypeScriptExtractor
{
    /// <summary>Edge kind names aligned with v2 CodeEdgeKinds (string-level; this package does not reference Om.CodeKnowledge).</summary>
    private const string Contains = "CONTAINS";
    private const string HasMethod = "HAS_METHOD";
    private const string HasProperty = "HAS_PROPERTY";
    private const string Extends = "EXTENDS";
    private const string Implements = "IMPLEMENTS";
    private const string Imports = "IMPORTS";

    private const double StructuralConfidence = 0.9;
    private const string StructuralEvidence = "syntax";

    private const int MaxSignatureLength = 200;

    private static readonly HashSet<string> TypeKindsWithMembers = new(StringComparer.Ordinal)
    {
        "class", "interface",
    };

    private static readonly HashSet<string> HeritageOwnerKinds = new(StringComparer.Ordinal)
    {
        "class", "interface",
    };

    /// <summary>
    /// Extraction query (.scm). Capture naming convention mirrors CSharpExtractor:
    /// "&lt;kind&gt;.decl" + "&lt;kind&gt;.name" (+ ".params" for arity-bearing kinds).
    /// Heritage clauses, import/export statements and accessibility modifiers are captured
    /// as standalone patterns and attached to declarations by byte containment.
    /// </summary>
    public const string QuerySource = """
        (class_declaration name: (type_identifier) @class.name) @class.decl
        (abstract_class_declaration name: (type_identifier) @class.name) @class.decl
        (interface_declaration name: (type_identifier) @interface.name) @interface.decl
        (enum_declaration name: (identifier) @enum.name) @enum.decl
        (type_alias_declaration name: (type_identifier) @type_alias.name) @type_alias.decl
        (function_declaration name: (identifier) @function.name parameters: (formal_parameters) @function.params) @function.decl
        (method_definition name: (_) @method.name parameters: (formal_parameters) @method.params) @method.decl
        (method_signature name: (_) @method.name parameters: (formal_parameters) @method.params) @method.decl
        (abstract_method_signature name: (_) @method.name parameters: (formal_parameters) @method.params) @method.decl
        (public_field_definition name: (_) @property.name) @property.decl
        (property_signature name: (_) @property.name) @property.decl
        (program (lexical_declaration (variable_declarator name: (identifier) @constant.name)) @constant.decl)
        (export_statement (lexical_declaration (variable_declarator name: (identifier) @constant.name)) @constant.decl)
        (statement_block (lexical_declaration (variable_declarator name: (identifier) @variable.name)) @variable.decl)
        (extends_clause) @extends
        (extends_type_clause) @extends
        (implements_clause) @implements
        (import_statement source: (string) @import.source) @import
        (export_statement) @export
        (accessibility_modifier) @modifier
        (call_expression function: (identifier) @call.target arguments: (arguments) @call.args) @call.site
        (call_expression function: (member_expression object: _ @call.receiver property: (_) @call.target) @call.fn arguments: (arguments) @call.args) @call.site
        (new_expression constructor: (_) @new.type arguments: (arguments)? @new.args) @new.site
        (assignment_expression left: (member_expression) @assign.lhs)
        (member_expression object: _ @access.receiver property: (_) @access.target) @access.site
        """;

    private sealed class Decl
    {
        public required string Kind;
        public required string Name;
        public int StartByte;
        public int EndByte;
        public int NameStartByte;
        public int StartLine;
        public int EndLine;
        public string Signature = "";
        public int Arity;
        public List<string> Access = [];
        public bool ExportWrapped;
        public Decl? Parent;
        public List<Decl> Children = [];
        public string Qualified = "";
    }

    /// <summary>All declared kinds can own a call site (TS has no namespace-like whole-file scope here).</summary>
    private static readonly HashSet<string> CallerOwnerKinds = new(StringComparer.Ordinal)
    {
        "class", "interface", "enum", "type_alias", "function", "method", "property", "constant", "variable",
    };

    public static (IReadOnlyList<ParsedSymbol> Symbols, IReadOnlyList<ParsedEdge> Edges, IReadOnlyList<ParsedCallSite> CallSites) Extract(
        string filePath,
        IReadOnlyList<TreeSitterQueryMatchResult> matches)
    {
        var decls = new List<Decl>();
        var extendsClauses = new List<TreeSitterQueryCapture>();
        var implementsClauses = new List<TreeSitterQueryCapture>();
        var importSources = new List<TreeSitterQueryCapture>();
        var exports = new List<TreeSitterQueryCapture>();
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
                        case "import.source": importSources.Add(capture); break;
                        case "export": exports.Add(capture); break;
                        case "modifier": modifiers.Add(capture); break;
                    }
                }
            }

            if (decl is null || name is null)
            {
                continue;
            }

            decls.Add(new Decl
            {
                Kind = kind,
                Name = name.Text,
                StartByte = decl.StartByte,
                EndByte = decl.EndByte,
                NameStartByte = name.StartByte,
                StartLine = decl.StartLine,
                EndLine = decl.EndLine,
                Signature = FirstLine(decl.Text),
                Arity = parameters is null ? 0 : CountParameters(parameters.Text),
            });
        }

        // Nesting by byte containment (strict; innermost container wins), as in CSharpExtractor.
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

        // Visibility: an accessibility modifier (public/private/protected) belongs to the
        // declaration it sits between the start of and the name of.
        foreach (var modifier in modifiers)
        {
            foreach (var decl in decls)
            {
                if (decl.StartByte <= modifier.StartByte && modifier.StartByte < decl.NameStartByte)
                {
                    decl.Access.Add(modifier.Text);
                }
            }
        }

        // Exported: the declaration is directly wrapped in an export statement — contained by
        // the export range while its parent declaration (if any) is not (this keeps members of
        // an exported class unexported). "export { x }" of a prior declaration is not covered.
        foreach (var decl in decls)
        {
            decl.ExportWrapped = exports.Any(export =>
                ContainsRange(export, decl)
                && (decl.Parent is null || !ContainsRange(export, decl.Parent)));
        }

        var roots = decls.Where(decl => decl.Parent is null).OrderBy(decl => decl.StartByte).ToList();
        foreach (var root in roots)
        {
            Qualify(root, prefix: null);
        }

        var edges = new List<ParsedEdge>();

        // CONTAINS: file → top-level, parent → child; HAS_METHOD/HAS_PROPERTY: type → member.
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
                if (decl.Kind == "method")
                {
                    edges.Add(new ParsedEdge(owner.Qualified, decl.Qualified, HasMethod, StructuralConfidence, StructuralEvidence));
                }
                else if (decl.Kind == "property")
                {
                    edges.Add(new ParsedEdge(owner.Qualified, decl.Qualified, HasProperty, StructuralConfidence, StructuralEvidence));
                }
            }
        }

        // EXTENDS/IMPLEMENTS: TS heritage clauses are syntactically distinct (extends_clause /
        // extends_type_clause vs implements_clause), so both stay at 0.9 evidence=syntax.
        AddHeritageEdges(edges, decls, extendsClauses, "extends", Extends);
        AddHeritageEdges(edges, decls, implementsClauses, "implements", Implements);

        // IMPORTS: file → module specifier string (one edge per import statement); resolution is P3/G4.
        foreach (var source in importSources)
        {
            if (StripQuotes(source.Text) is { Length: > 0 } specifier)
            {
                edges.Add(new ParsedEdge(filePath, specifier, Imports, StructuralConfidence, StructuralEvidence));
            }
        }

        // Call sites (T1.1): caller attribution is the innermost declaration containing the
        // site's start byte (a top-level "const x = () => f()" attributes to x); top-level
        // statements fall back to "<file>".
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
        var visibility = string.Join(" ", decl.Access);
        return new ParsedSymbol(
            decl.Name,
            decl.Kind,
            decl.StartLine,
            decl.EndLine,
            decl.Signature,
            visibility,
            Exported: decl.ExportWrapped || decl.Access.Contains("public"),
            Children: decl.Children.Select(ToParsedSymbol).ToArray())
        {
            SymKey = $"ts:{decl.Qualified}#{decl.Arity}",
        };
    }

    private static bool StrictlyContains(Decl outer, Decl inner) =>
        outer.StartByte <= inner.StartByte
        && inner.EndByte <= outer.EndByte
        && Span(outer) > Span(inner);

    private static bool ContainsRange(TreeSitterQueryCapture outer, Decl inner) =>
        outer.StartByte <= inner.StartByte && inner.EndByte <= outer.EndByte;

    private static int Span(Decl decl) => decl.EndByte - decl.StartByte;

    private static Decl? FindInnermostOwner(List<Decl> decls, int position, HashSet<string> kinds)
    {
        Decl? best = null;
        foreach (var decl in decls)
        {
            if (!kinds.Contains(decl.Kind) || position < decl.StartByte || position >= decl.EndByte)
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

    /// <summary>Parameter arity from a formal_parameters' text: top-level comma count (angle/paren/bracket/brace aware).</summary>
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

    /// <summary>
    /// Split a heritage clause's text ("extends Base&lt;T&gt;" / "implements IFoo, IBar") into
    /// target names with type arguments stripped.
    /// </summary>
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

    /// <summary>Module specifier from an import string node's text: surrounding quotes stripped.</summary>
    private static string StripQuotes(string stringText)
    {
        var text = stringText.Trim();
        if (text.Length >= 2 && (text[0] is '"' or '\'' or '`') && text[^1] == text[0])
        {
            text = text[1..^1];
        }

        return text.Trim();
    }
}

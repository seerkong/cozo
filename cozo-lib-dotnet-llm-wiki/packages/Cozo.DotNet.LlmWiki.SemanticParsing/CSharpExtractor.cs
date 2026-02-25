namespace Cozo.DotNet.LlmWiki.SemanticParsing;

/// <summary>
/// C# symbol/edge extractor (design.md §3, T2.1). Query-driven: a single tree-sitter query
/// captures declarations, base lists, using directives and modifiers; nesting is rebuilt from
/// byte-range containment (a file-scoped namespace scopes to end-of-file per C# semantics).
///
/// Produces:
/// - ParsedSymbol tree with v2 kinds (namespace/class/interface/struct/enum/record/delegate/
///   method/constructor/property/field), visibility from access modifiers, exported=public,
///   signature=first declaration line (truncated), sym_key "csharp:&lt;qualified&gt;#&lt;arity&gt;".
/// - Structural edges: CONTAINS (file→top-level, parent→child), HAS_METHOD/HAS_PROPERTY
///   (type→member) at confidence 0.9 evidence "syntax"; EXTENDS/IMPLEMENTS from base_list via
///   the I-prefix heuristic at 0.7 evidence "heuristic" (decisions.md #1; corrected by G4);
///   IMPORTS (file→using target, unresolved name reference — resolution is P3/G4).
/// - Call sites (call-resolution track T1.1): invocation_expression (identifier / generic_name /
///   member_access forms), object_creation_expression (kind=new, generic-stripped target),
///   member_access_expression reads/writes (assignment lhs = write); de-dup rules in CallSiteCollector.
///
/// Known non-coverage (recorded in track findings): events, operators, indexers, destructors,
/// extern aliases; interface members' implicit public visibility is not synthesized (visibility
/// reflects written modifiers only). Enum members (kind=field) and local functions
/// (kind=function) are covered since T3.1 (dogfood coverage gate).
/// </summary>
internal static class CSharpExtractor
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
    private const double HeuristicConfidence = 0.7;
    private const string HeuristicEvidence = "heuristic";

    private const int MaxSignatureLength = 200;

    private static readonly HashSet<string> AccessModifiers = new(StringComparer.Ordinal)
    {
        "public", "private", "protected", "internal", "file",
    };

    private static readonly HashSet<string> TypeKindsWithMembers = new(StringComparer.Ordinal)
    {
        "class", "interface", "struct", "record",
    };

    private static readonly HashSet<string> BaseListOwnerKinds = new(StringComparer.Ordinal)
    {
        "class", "interface", "struct", "enum", "record",
    };

    /// <summary>
    /// Extraction query (.scm). Capture naming convention: "&lt;kind&gt;.decl" + "&lt;kind&gt;.name"
    /// (+ ".params" for arity-bearing kinds); "filescoped.decl" marks the file-scoped namespace
    /// whose scope extends to end-of-file. base_list/using_directive/modifier are captured as
    /// standalone patterns and attached to declarations by byte containment.
    /// </summary>
    public const string QuerySource = """
        (namespace_declaration name: (_) @namespace.name) @namespace.decl
        (file_scoped_namespace_declaration name: (_) @namespace.name) @filescoped.decl
        (class_declaration name: (identifier) @class.name) @class.decl
        (interface_declaration name: (identifier) @interface.name) @interface.decl
        (struct_declaration name: (identifier) @struct.name) @struct.decl
        (enum_declaration name: (identifier) @enum.name) @enum.decl
        (record_declaration name: (identifier) @record.name) @record.decl
        (delegate_declaration name: (identifier) @delegate.name) @delegate.decl
        (method_declaration name: (identifier) @method.name parameters: (parameter_list) @method.params) @method.decl
        (constructor_declaration name: (identifier) @constructor.name parameters: (parameter_list) @constructor.params) @constructor.decl
        (property_declaration name: (identifier) @property.name) @property.decl
        (field_declaration (variable_declaration (variable_declarator name: (identifier) @field.name))) @field.decl
        (enum_member_declaration name: (identifier) @enummember.name) @enummember.decl
        (local_function_statement name: (identifier) @localfn.name parameters: (parameter_list) @localfn.params) @localfn.decl
        (base_list) @bases
        (using_directive) @using
        (modifier) @modifier
        (invocation_expression function: (identifier) @call.target arguments: (argument_list) @call.args) @call.site
        (invocation_expression function: (generic_name) @call.target arguments: (argument_list) @call.args) @call.site
        (invocation_expression function: (member_access_expression expression: _ @call.receiver name: (_) @call.target) @call.fn arguments: (argument_list) @call.args) @call.site
        (object_creation_expression type: (_) @new.type arguments: (argument_list)? @new.args) @new.site
        (assignment_expression left: (member_access_expression) @assign.lhs)
        (member_access_expression expression: _ @access.receiver name: (_) @access.target) @access.site
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

    /// <summary>Kinds that can own a call site (everything but namespaces — a file-scoped namespace spans the whole file and would swallow top-level code).</summary>
    private static readonly HashSet<string> CallerOwnerKinds = new(StringComparer.Ordinal)
    {
        "class", "interface", "struct", "enum", "record", "delegate",
        "method", "constructor", "property", "field", "function",
    };

    public static (IReadOnlyList<ParsedSymbol> Symbols, IReadOnlyList<ParsedEdge> Edges, IReadOnlyList<ParsedCallSite> CallSites) Extract(
        string filePath,
        int sourceByteLength,
        IReadOnlyList<TreeSitterQueryMatchResult> matches)
    {
        var decls = new List<Decl>();
        var baseLists = new List<TreeSitterQueryCapture>();
        var usings = new List<TreeSitterQueryCapture>();
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
                        case "bases": baseLists.Add(capture); break;
                        case "using": usings.Add(capture); break;
                        case "modifier": modifiers.Add(capture); break;
                    }
                }
            }

            if (decl is null || name is null)
            {
                continue;
            }

            var fileScoped = kind == "filescoped";
            decls.Add(new Decl
            {
                // enum members are constant fields in v2 kind terms; local functions are functions
                // (not type members, so they never get HAS_METHOD — CONTAINS chains through the
                // declaring method by byte containment).
                Kind = fileScoped ? "namespace" : kind switch
                {
                    "enummember" => "field",
                    "localfn" => "function",
                    _ => kind,
                },
                Name = name.Text,
                StartByte = decl.StartByte,
                EndByte = decl.EndByte,
                // A file-scoped namespace declaration node covers only "namespace X;", but its
                // scope is the whole file (all later declarations are its members).
                EffectiveEndByte = fileScoped ? sourceByteLength : decl.EndByte,
                NameStartByte = name.StartByte,
                StartLine = decl.StartLine,
                EndLine = decl.EndLine,
                Signature = FirstLine(decl.Text),
                Arity = parameters is null ? 0 : CountParameters(parameters.Text),
            });
        }

        // Nesting by byte containment (strict: identical ranges — e.g. two declarators of one
        // field_declaration — stay siblings). Innermost container wins.
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

        // Visibility: an access modifier belongs to every declaration it sits between the start
        // of and the name of (this naturally skips enclosing scopes and accessor-level modifiers,
        // and covers both declarators of a multi-variable field).
        foreach (var modifier in modifiers)
        {
            if (!AccessModifiers.Contains(modifier.Text))
            {
                continue;
            }

            foreach (var decl in decls)
            {
                if (decl.StartByte <= modifier.StartByte && modifier.StartByte < decl.NameStartByte)
                {
                    decl.Access.Add(modifier.Text);
                }
            }
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
                if (decl.Kind is "method" or "constructor")
                {
                    edges.Add(new ParsedEdge(owner.Qualified, decl.Qualified, HasMethod, StructuralConfidence, StructuralEvidence));
                }
                else if (decl.Kind == "property")
                {
                    edges.Add(new ParsedEdge(owner.Qualified, decl.Qualified, HasProperty, StructuralConfidence, StructuralEvidence));
                }
            }
        }

        // EXTENDS/IMPLEMENTS: C# base_list has no syntactic distinction; the I-prefix heuristic
        // decides the kind, both at reduced confidence 0.7 with evidence=heuristic (G4 corrects).
        foreach (var baseList in baseLists)
        {
            var owner = FindInnermostOwner(decls, baseList.StartByte, BaseListOwnerKinds);
            if (owner is null)
            {
                continue;
            }

            foreach (var target in ParseBaseTypes(baseList.Text))
            {
                var kind = LooksLikeInterfaceName(target) ? Implements : Extends;
                edges.Add(new ParsedEdge(owner.Qualified, target, kind, HeuristicConfidence, HeuristicEvidence));
            }
        }

        // IMPORTS: file → using target as an unresolved name reference (resolution is P3/G4).
        foreach (var usingDirective in usings)
        {
            if (ParseUsingTarget(usingDirective.Text) is { Length: > 0 } target)
            {
                edges.Add(new ParsedEdge(filePath, target, Imports, StructuralConfidence, StructuralEvidence));
            }
        }

        // Call sites (T1.1): caller attribution is the innermost non-namespace declaration
        // containing the site's start byte; top-level statements fall back to "<file>".
        var callSites = callSiteCollector.Build(position =>
            FindInnermostOwner(decls, position, CallerOwnerKinds)?.Qualified is { Length: > 0 } qualified ? qualified : "<file>");

        return (roots.Select(ToParsedSymbol).ToArray(), edges, callSites);
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
            Exported: decl.Access.Contains("public"),
            Children: decl.Children.Select(ToParsedSymbol).ToArray())
        {
            SymKey = $"csharp:{decl.Qualified}#{decl.Arity}",
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

    /// <summary>Parameter arity from a parameter_list's text: top-level comma count (angle/paren/bracket aware).</summary>
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
            if (ch is '<' or '(' or '[')
            {
                depth++;
            }
            else if (ch is '>' or ')' or ']')
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

    /// <summary>Split a base_list's text (": A, B&lt;T&gt;, C(args)") into base type names with type arguments and primary-constructor argument lists stripped.</summary>
    private static IEnumerable<string> ParseBaseTypes(string baseListText)
    {
        var text = baseListText.Trim();
        if (text.StartsWith(':'))
        {
            text = text[1..];
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
            if (name.StartsWith("global::", StringComparison.Ordinal))
            {
                name = name["global::".Length..];
            }

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
            if (ch is '<' or '(' or '[')
            {
                depth++;
            }
            else if (ch is '>' or ')' or ']')
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

    /// <summary>I-prefix heuristic on the last name segment: "IFoo" → interface (decisions.md #1).</summary>
    private static bool LooksLikeInterfaceName(string target)
    {
        var dot = target.LastIndexOf('.');
        var last = dot >= 0 ? target[(dot + 1)..] : target;
        return last.Length >= 2 && last[0] == 'I' && char.IsUpper(last[1]);
    }

    /// <summary>Target of a using_directive: alias RHS for "using X = Y;", otherwise the imported name (keywords global/static/unsafe stripped).</summary>
    private static string? ParseUsingTarget(string usingText)
    {
        var text = usingText.Trim().TrimEnd(';').Trim();
        var eq = text.IndexOf('=');
        if (eq >= 0)
        {
            return text[(eq + 1)..].Trim();
        }

        var tokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Where(token => token is not ("using" or "global" or "static" or "unsafe"))
            .ToArray();
        return tokens.Length == 0 ? null : tokens[^1];
    }
}

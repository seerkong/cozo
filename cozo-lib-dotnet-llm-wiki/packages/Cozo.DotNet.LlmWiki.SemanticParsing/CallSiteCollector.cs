namespace Cozo.DotNet.LlmWiki.SemanticParsing;

/// <summary>
/// Shared call-site capture collection for the per-language extractors (call-resolution track T1.1).
/// Both grammar queries use the same capture family names (call.* / new.* / assign.lhs / access.*),
/// so the grouping, de-duplication and ParsedCallSite construction live here.
///
/// De-duplication rules:
/// - a member access that is the function of an invocation (@call.fn) or the constructor/type of a
///   new expression (@new.type) is not also emitted as an access (it already counts as call/new);
/// - a member access on the left side of an assignment (@assign.lhs) is emitted once with
///   AccessMode=write; every other surviving member access is a read.
/// Nested member accesses (the receiver part of "a.b.c" or of "a.b.c()") still count as reads —
/// they are genuine property reads.
/// </summary>
internal sealed class CallSiteCollector
{
    private readonly List<(TreeSitterQueryCapture Site, string? Receiver, string Target, string? Args)> _calls = [];
    private readonly List<(TreeSitterQueryCapture Site, string Type, string? Args)> _news = [];
    private readonly List<(TreeSitterQueryCapture Site, string Receiver, string Target)> _accesses = [];
    private readonly HashSet<(int Start, int End)> _writeRanges = [];
    private readonly HashSet<(int Start, int End)> _excludedAccessRanges = [];

    /// <summary>Consume one query match if it belongs to a call-site capture family. Returns true when consumed.</summary>
    public bool TryCollect(TreeSitterQueryMatchResult match)
    {
        TreeSitterQueryCapture? site = null, target = null, receiver = null, args = null, fn = null, lhs = null;
        var family = "";
        foreach (var capture in match.Captures)
        {
            switch (capture.CaptureName)
            {
                case "call.site": site = capture; family = "call"; break;
                case "call.target": target = capture; break;
                case "call.receiver": receiver = capture; break;
                case "call.args": args = capture; break;
                case "call.fn": fn = capture; break;
                case "new.site": site = capture; family = "new"; break;
                case "new.type": target = capture; break;
                case "new.args": args = capture; break;
                case "assign.lhs": lhs = capture; family = "assign"; break;
                case "access.site": site = capture; family = "access"; break;
                case "access.target": target = capture; break;
                case "access.receiver": receiver = capture; break;
            }
        }

        switch (family)
        {
            case "call" when site is not null && target is not null:
                _calls.Add((site, receiver?.Text, target.Text, args?.Text));
                if (fn is not null)
                {
                    _excludedAccessRanges.Add((fn.StartByte, fn.EndByte));
                }

                return true;
            case "new" when site is not null && target is not null:
                _news.Add((site, target.Text, args?.Text));
                // TS "new a.B()" has a member_expression constructor; never double-count it as an access.
                _excludedAccessRanges.Add((target.StartByte, target.EndByte));
                return true;
            case "assign" when lhs is not null:
                _writeRanges.Add((lhs.StartByte, lhs.EndByte));
                return true;
            case "access" when site is not null && target is not null && receiver is not null:
                _accesses.Add((site, receiver.Text, target.Text));
                return true;
            case "":
                return false;
            default:
                // Malformed/partial capture set of a call family: consume without emitting.
                return true;
        }
    }

    /// <summary>Build the final call-site list, ordered by source position. callerOf maps a byte offset to the enclosing symbol's qualified name (or "&lt;file&gt;").</summary>
    public IReadOnlyList<ParsedCallSite> Build(Func<int, string> callerOf)
    {
        var sites = new List<(int StartByte, ParsedCallSite Site)>();

        foreach (var (site, receiverText, targetText, argsText) in _calls)
        {
            sites.Add((site.StartByte, new ParsedCallSite(
                callerOf(site.StartByte),
                StripTypeArguments(targetText),
                receiverText,
                CountArguments(argsText),
                "call",
                AccessMode: null,
                site.StartLine)));
        }

        foreach (var (site, typeText, argsText) in _news)
        {
            sites.Add((site.StartByte, new ParsedCallSite(
                callerOf(site.StartByte),
                StripTypeArguments(typeText),
                ReceiverText: null,
                CountArguments(argsText),
                "new",
                AccessMode: null,
                site.StartLine)));
        }

        foreach (var (site, receiverText, targetText) in _accesses)
        {
            if (_excludedAccessRanges.Contains((site.StartByte, site.EndByte)))
            {
                continue;
            }

            var mode = _writeRanges.Contains((site.StartByte, site.EndByte)) ? "write" : "read";
            sites.Add((site.StartByte, new ParsedCallSite(
                callerOf(site.StartByte),
                targetText,
                receiverText,
                Arity: 0,
                "access",
                mode,
                site.StartLine)));
        }

        return sites.OrderBy(entry => entry.StartByte).Select(entry => entry.Site).ToArray();
    }

    /// <summary>"List&lt;int&gt;" → "List"; "Demo.Foo&lt;T&gt;" → "Demo.Foo" (design §1: targetName is generic-stripped).</summary>
    private static string StripTypeArguments(string name)
    {
        var cut = name.IndexOf('<');
        return (cut >= 0 ? name[..cut] : name).Trim();
    }

    /// <summary>Argument arity from an argument list's text "(a, f(b, c), new[] { 1, 2 })": top-level comma count (angle/paren/bracket/brace aware). Null (e.g. "new T { ... }" without parens) counts as 0.</summary>
    private static int CountArguments(string? argumentListText)
    {
        if (argumentListText is null)
        {
            return 0;
        }

        var inner = argumentListText.Trim();
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
}

using Cozo.DotNet.Om.CodeKnowledge;

namespace Cozo.DotNet.LlmWiki.Indexing;

/// <summary>
/// Syntax-level entry-point candidate detection (add-llm-wiki-process-extraction track,
/// design.md §1/§3). The graph layer cannot see route registrations — MapGet and friends are
/// external calls that never become edges — so the indexing pipeline, which has the parsed call
/// sites, detects the candidates and ships them into ck_entry_point via
/// <see cref="CodeKnowledgeBatch.EntryPoints"/>. <c>ExtractProcessesAsync</c> then merges them
/// with its own graph-level detection (main / public_api).
///
/// Heuristics (MVP, limitations recorded in track findings):
/// - http_route: a call site whose target is MapGet/MapPost/MapPut/MapDelete/MapMethods
///   registers the *enclosing method* (the registration site, not the lambda handler) as the
///   entry candidate.
/// - mcp_tool: a method named CallAsync whose containing type name contains "ToolRunner".
/// Internal by design — the public knob is <see cref="Core.RepositoryIndexRequest.ExtractProcesses"/>.
/// </summary>
internal static class EntryPointCandidates
{
    private static readonly HashSet<string> RouteRegistrars = new(StringComparer.Ordinal)
    {
        "MapGet", "MapPost", "MapPut", "MapDelete", "MapMethods",
    };

    internal static IReadOnlyList<CodeEntryPointFact> Detect(
        IReadOnlyList<CallResolver.FileInput> parsedFiles,
        IReadOnlyList<CodeSymbolFact> symbols)
    {
        var facts = new Dictionary<(string SymbolId, string Kind), CodeEntryPointFact>();

        // (fileId, qualified-name) → symbolId for caller lookup, same sym_key convention as
        // CallResolver ("lang:Qualified#arity"); plus id → symbol for the mcp_tool parent walk.
        var byFileQualified = new Dictionary<(string FileId, string Qualified), string>();
        var byId = new Dictionary<string, CodeSymbolFact>(StringComparer.Ordinal);
        foreach (var symbol in symbols)
        {
            byId.TryAdd(symbol.SymbolId, symbol);
            var symKey = symbol.SymKey;
            var colon = symKey.IndexOf(':');
            var hash = symKey.LastIndexOf('#');
            if (colon >= 0 && hash > colon)
            {
                byFileQualified.TryAdd((symbol.FileId, symKey[(colon + 1)..hash]), symbol.SymbolId);
            }
        }

        // http_route: route-registrar call sites → the registering method.
        foreach (var file in parsedFiles)
        {
            foreach (var site in file.Parsed.CallSites)
            {
                if (site.Kind != "call" || !RouteRegistrars.Contains(site.TargetName))
                {
                    continue;
                }

                if (byFileQualified.TryGetValue((file.FileId, site.CallerQualified), out var callerId))
                {
                    facts.TryAdd((callerId, "http_route"), new CodeEntryPointFact(
                        callerId,
                        "http_route",
                        $"detector=indexing;registrar={site.TargetName};site={file.RelativePath}:{site.Line}"));
                }
            }
        }

        // mcp_tool: CallAsync methods on *ToolRunner* containers.
        foreach (var symbol in symbols)
        {
            if (symbol.Kind == "method"
                && symbol.Name == "CallAsync"
                && symbol.ParentId.Length > 0
                && byId.TryGetValue(symbol.ParentId, out var parent)
                && parent.Name.Contains("ToolRunner", StringComparison.Ordinal))
            {
                facts.TryAdd((symbol.SymbolId, "mcp_tool"), new CodeEntryPointFact(
                    symbol.SymbolId,
                    "mcp_tool",
                    $"detector=indexing;heuristic=ToolRunner.CallAsync;container={parent.Name}"));
            }
        }

        return facts.Values
            .OrderBy(fact => fact.SymbolId, StringComparer.Ordinal)
            .ThenBy(fact => fact.Kind, StringComparer.Ordinal)
            .ToArray();
    }
}

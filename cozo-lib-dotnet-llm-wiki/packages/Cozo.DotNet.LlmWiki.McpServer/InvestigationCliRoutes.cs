using System.Text.Json.Nodes;

namespace Cozo.DotNet.LlmWiki.McpServer;

/// <summary>
/// Stable, read-only CLI routes for external business-semantic analyzers. The route table is
/// intentionally separate from the full MCP tool registry so investigate can never become an
/// arbitrary tool, SQL, source-path, or mutation dispatcher.
/// </summary>
internal static class InvestigationCliRoutes
{
    private static readonly IReadOnlyDictionary<string, string> ToolByRoute =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["overview/get"] = "ontology_investigation_overview",
            ["terms/find"] = "find_business_terms",
            ["patterns/find"] = "find_semantic_patterns",
            ["evidence/list"] = "list_semantic_evidence",
            ["evidence/get"] = "get_semantic_evidence",
            ["domains/discover"] = "discover_domain_charters",
            ["use-cases/list"] = "list_cross_layer_use_cases",
            ["state-rules/find"] = "find_state_rule_clusters",
            ["implementations/find"] = "find_implementation_clusters",
            ["topology/domain"] = "get_domain_topology",
            ["ontology/subjects/inspect"] = "inspect_ontology_subject",
            ["ontology/use-cases/list"] = "list_use_case_slices",
            ["ontology/use-cases/get"] = "get_use_case_slice",
        };

    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> AllowedArgumentsByTool =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            ["ontology_investigation_overview"] = Set("ontologyId"),
            ["find_business_terms"] = Set("term", "ontologyId", "cursor", "limit"),
            ["list_use_case_slices"] = Set("ontologyId", "cursor", "limit"),
            ["get_use_case_slice"] = Set("ontologyId", "sliceId"),
            ["find_semantic_patterns"] = Set("kind", "term", "cursor", "limit"),
            ["list_semantic_evidence"] = Set("term", "cursor", "limit"),
            ["get_semantic_evidence"] = Set("evidenceIds"),
            ["inspect_ontology_subject"] = Set("ontologyId", "subjectKind", "subjectId"),
            ["discover_domain_charters"] = Set("term", "cursor", "limit"),
            ["list_cross_layer_use_cases"] = Set("entrySymbolId", "domainSeed", "cursor", "limit"),
            ["find_state_rule_clusters"] = Set("term", "cursor", "limit"),
            ["find_implementation_clusters"] = Set("domainSeed", "evidenceIds", "cursor", "limit"),
            ["get_domain_topology"] = Set("term"),
        };

    public static IReadOnlyList<string> Paths => ToolByRoute.Keys.Order(StringComparer.Ordinal).ToArray();

    public static string Resolve(IReadOnlyList<string> pathSegments)
    {
        if (pathSegments.Count == 0 || pathSegments.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("investigate requires a resource/action path.", nameof(pathSegments));
        }
        var route = string.Join('/', pathSegments.Select(segment => segment.Trim().ToLowerInvariant()));
        return ToolByRoute.TryGetValue(route, out var toolName)
            ? toolName
            : throw new ArgumentException(
                $"Unknown investigate path '{route}'. Supported paths: {string.Join(", ", Paths)}.",
                nameof(pathSegments));
    }

    public static void ValidateArguments(string toolName, JsonObject arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!AllowedArgumentsByTool.TryGetValue(toolName, out var allowed))
        {
            throw new ArgumentException($"Unknown investigation operation '{toolName}'.", nameof(toolName));
        }

        var extras = arguments.Select(property => property.Key)
            .Where(name => !allowed.Contains(name))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (extras.Length > 0)
        {
            throw new ArgumentException(
                $"investigate {toolName} does not accept: {string.Join(", ", extras)}.",
                nameof(arguments));
        }
    }

    private static IReadOnlySet<string> Set(params string[] values) =>
        new HashSet<string>(values, StringComparer.Ordinal);
}

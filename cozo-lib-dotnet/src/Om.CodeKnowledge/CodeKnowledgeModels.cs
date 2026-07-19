using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Cozo.DotNet.Om.CodeKnowledge;

public sealed record CodeKnowledgeBatch(
    IReadOnlyList<CodeRepositoryFact>? Repositories = null,
    IReadOnlyList<CodeFileFact>? Files = null,
    IReadOnlyList<CodeSymbolFact>? Symbols = null,
    [property: Obsolete("ck_relation was replaced by ck_edge (schema v2). Use Edges with CodeEdgeFact instead; entries supplied here are converted to ck_edge rows with confidence=0.3 and resolver=\"regex\".")]
    IReadOnlyList<CodeRelationFact>? Relations = null,
    IReadOnlyList<CodeEdgeFact>? Edges = null,
    IReadOnlyList<CodeDocBlockFact>? DocBlocks = null,
    IReadOnlyList<CodeConceptFact>? Concepts = null,
    IReadOnlyList<CodeDiagnosticFact>? Diagnostics = null,
    IReadOnlyList<CodeOwnerFact>? Owners = null,
    // add-llm-wiki-process-extraction track (design.md §3, add-only): syntax-level entry-point
    // candidates (http_route / mcp_tool) detected by the indexing pipeline; written to
    // ck_entry_point with :put so a later ExtractProcessesAsync merge preserves them.
    IReadOnlyList<CodeEntryPointFact>? EntryPoints = null,
    // add-llm-wiki-depa-ontology track (design §4.2, add-only): aggregated out-of-repo call
    // summaries written to ck_external_call. Facts with an empty Category are pre-classified
    // at write time against the built-in effect whitelist (fast path); depa_scan re-matches
    // with the merged built-in + user whitelist without touching these rows.
    IReadOnlyList<CodeExternalCallFact>? ExternalCalls = null,
    // add-onto-semantic-business-understanding T1.1: reproducible source observations.
    // These rows are not business assertions and are file-scope-cleaned with other ck_* facts.
    IReadOnlyList<CodeSemanticClaimFact>? SemanticClaims = null);

public sealed record CodeRepositoryFact(string RepoId, string RootPath, string? Name = null, string? Commit = null);

public sealed record CodeFileFact(string FileId, string RepoId, string Path, string Language = "", string Hash = "", string UpdatedAt = "");

public sealed record CodeSymbolFact(
    string SymbolId,
    string FileId,
    string Name,
    string Kind = "",
    int StartLine = 0,
    int EndLine = 0,
    string Signature = "",
    string ParentId = "",
    string Lang = "",
    string Visibility = "",
    bool Exported = false,
    string SymKey = "",
    string DocId = "",
    string Resolver = "");

public sealed record CodeRelationFact(string FromId, string ToId, string Kind, string? FileId = null, int Line = 0, string Evidence = "");

/// <summary>
/// Typed edge fact for ck_edge (schema v2). Call-site granularity: the storage key is
/// (FromId, ToId, Kind, FileId, Line), so multiple call sites of the same symbol pair are preserved.
/// </summary>
public sealed record CodeEdgeFact(
    string FromId,
    string ToId,
    string Kind,
    string FileId = "",
    int Line = 0,
    double Confidence = 0.0,
    string Resolver = "",
    string Evidence = "");

/// <summary>
/// One aggregated out-of-repo call summary row for ck_external_call (track
/// add-llm-wiki-depa-ontology, design §4.2). Key = (CallerId, TargetKey):
/// CallerId is a ck_symbol.symbol_id, TargetKey the normalized external FQN
/// (generics erased, overloads merged at method-name granularity, e.g.
/// "System.IO.File.WriteAllText"). Only the first call site is stored
/// (FirstFileId/FirstLine); Count carries how many sites hit the same target.
/// Category is the effect-whitelist pre-classification ("" = unclassified;
/// filled from the built-in table at index write time when left empty).
/// </summary>
public sealed record CodeExternalCallFact(
    string CallerId,
    string TargetKey,
    int Count = 1,
    string Category = "",
    string FirstFileId = "",
    int FirstLine = 0,
    string Resolver = "");

/// <summary>
/// A directly anchored source observation stored in ck_semantic_claim. PayloadJson is
/// canonicalized by the write path; Kind is restricted to <see cref="CodeSemanticClaimKinds"/>.
/// </summary>
public sealed record CodeSemanticClaimFact(
    string ClaimId,
    string SubjectId,
    string Kind,
    string PayloadJson,
    string FileId,
    int StartLine,
    int EndLine,
    double Confidence,
    string Resolver,
    string Evidence);

/// <summary>Closed initial vocabulary for ck_semantic_claim.kind.</summary>
public static class CodeSemanticClaimKinds
{
    public const string TypedReference = "typed_reference";
    public const string ValidationConstraint = "validation_constraint";
    public const string PersistenceConstraint = "persistence_constraint";
    public const string StateField = "state_field";
    public const string StateValue = "state_value";
    public const string StateAssignment = "state_assignment";
    public const string TransactionScope = "transaction_scope";
    public const string RouteBinding = "route_binding";
    public const string BusinessGuard = "business_guard";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        TypedReference,
        ValidationConstraint,
        PersistenceConstraint,
        StateField,
        StateValue,
        StateAssignment,
        TransactionScope,
        RouteBinding,
        BusinessGuard,
    };

    public static bool IsSupported(string kind) => All.Contains(kind);
}

/// <summary>Canonical JSON and deterministic identity contract shared by claim producers.</summary>
public static class CodeSemanticClaimIdentity
{
    public static string Create(
        string sourceIdentity,
        string kind,
        string payloadJson,
        int startLine,
        int endLine)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceIdentity);
        if (!CodeSemanticClaimKinds.IsSupported(kind))
        {
            throw new ArgumentException($"Unsupported semantic claim kind '{kind}'.", nameof(kind));
        }

        if (startLine < 1 || endLine < startLine)
        {
            throw new ArgumentOutOfRangeException(
                nameof(startLine),
                "Semantic claim identity requires a one-based non-empty source line range.");
        }

        var canonicalPayload = CanonicalizePayload(payloadJson);
        var material = string.Join(
            '\u001f',
            CodeKnowledgeSchema.SchemaVersion.ToString(CultureInfo.InvariantCulture),
            sourceIdentity,
            kind,
            canonicalPayload,
            startLine.ToString(CultureInfo.InvariantCulture),
            endLine.ToString(CultureInfo.InvariantCulture));
        return "semantic:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
    }

    public static string CanonicalizePayload(string payloadJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadJson);
        using var document = JsonDocument.Parse(payloadJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Semantic claim payload must be a JSON object.", nameof(payloadJson));
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteCanonicalJson(writer, document.RootElement);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteCanonicalJson(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                var properties = element.EnumerateObject().ToArray();
                var duplicate = properties
                    .GroupBy(property => property.Name, StringComparer.Ordinal)
                    .FirstOrDefault(group => group.Count() > 1);
                if (duplicate is not null)
                {
                    throw new ArgumentException(
                        $"Semantic claim payload contains duplicate property '{duplicate.Key}'.",
                        "payloadJson");
                }

                foreach (var property in properties.OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonicalJson(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonicalJson(writer, item);
                }
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}

/// <summary>
/// Read-only semantic-claim schema preflight. A false result requires explicit repository
/// reindexing; running this check never creates, removes, or migrates a relation.
/// </summary>
public sealed record CodeSemanticClaimPreflight(
    bool Ready,
    int? SchemaVersion,
    string Diagnostic);

/// <summary>Edge kind constants for ck_edge (design.md §4 of track redesign-codeknowledge-schema-v2).</summary>
public static class CodeEdgeKinds
{
    public const string Contains = "CONTAINS";
    public const string Imports = "IMPORTS";
    public const string Extends = "EXTENDS";
    public const string Implements = "IMPLEMENTS";
    public const string HasMethod = "HAS_METHOD";
    public const string HasProperty = "HAS_PROPERTY";
    public const string Calls = "CALLS";
    public const string Accesses = "ACCESSES";
    public const string MethodOverrides = "METHOD_OVERRIDES";
    public const string MethodImplements = "METHOD_IMPLEMENTS";
    public const string DocLinks = "DOC_LINKS";
    public const string Mentions = "MENTIONS";
}

/// <summary>
/// Normalization map from v1 lowercase edge kinds (legacy ck_relation vocabulary) to the
/// v2 <see cref="CodeEdgeKinds"/> enumeration. Unknown kinds pass through unchanged, so
/// heuristic-only kinds such as the regex indexer's "near" survive as open strings.
/// </summary>
public static class CodeEdgeKindMap
{
    public static string NormalizeV1(string kind) =>
        kind switch
        {
            "defines" => CodeEdgeKinds.Contains,
            "imports" => CodeEdgeKinds.Imports,
            "extends" => CodeEdgeKinds.Extends,
            "implements" => CodeEdgeKinds.Implements,
            "calls" => CodeEdgeKinds.Calls,
            "documents" => CodeEdgeKinds.DocLinks,
            "mentions" => CodeEdgeKinds.Mentions,
            _ => kind,
        };
}

public sealed record CodeDocBlockFact(string DocId, string FileId, string Anchor = "", string Text = "", string Hash = "", string UpdatedAt = "");

public sealed record CodeConceptFact(string ConceptId, string Name, string Description = "");

public sealed record CodeDiagnosticFact(string DiagnosticId, string TargetId, string Kind, string Message, string Severity = "info");

public sealed record CodeOwnerFact(string TargetId, string Owner, string Kind = "owner");

public sealed record CodeKnowledgeIndexResult(
    int Repositories,
    int Files,
    int Symbols,
    int Relations,
    int Edges,
    int DocBlocks,
    int Concepts,
    int Diagnostics,
    int Owners,
    int EntryPoints = 0,
    int ExternalCalls = 0,
    int SemanticClaims = 0);

public sealed record SymbolContextResult(
    CodeSymbolSummary? Symbol,
    IReadOnlyList<CodeRelationSummary> Incoming,
    IReadOnlyList<CodeRelationSummary> Outgoing,
    IReadOnlyList<CodeDocSummary> Docs)
{
    // deepen-llm-wiki-context-impact track (design.md §3, add-only): enrichment fields with
    // empty defaults so the positional v1 constructor keeps working unchanged. They stay empty
    // when the community/process tables are absent or the enrichment queries fail (never throws).

    /// <summary>Execution flows (ck_process) the symbol participates in, canonical order, capped at 10.</summary>
    public IReadOnlyList<CodeProcessSummary> Processes { get; init; } = [];

    /// <summary>Community the symbol belongs to (ck_member), or "" when not a member.</summary>
    public string CommunityId { get; init; } = "";

    /// <summary>Label of the symbol's community, or "" when not a member.</summary>
    public string CommunityLabel { get; init; } = "";

    /// <summary>Incoming edge count per edge kind (from the same relations as <see cref="Incoming"/>).</summary>
    public IReadOnlyDictionary<string, int> IncomingByKind { get; init; } =
        new Dictionary<string, int>(StringComparer.Ordinal);

    /// <summary>Outgoing edge count per edge kind (from the same relations as <see cref="Outgoing"/>).</summary>
    public IReadOnlyDictionary<string, int> OutgoingByKind { get; init; } =
        new Dictionary<string, int>(StringComparer.Ordinal);
}

public sealed record CodeSymbolSummary(string SymbolId, string Name, string Kind, string FileId, string Path, int StartLine, int EndLine, string Signature);

public sealed record CodeRelationSummary(string FromId, string ToId, string Kind, string Evidence, double Confidence = 0.0);

public sealed record CodeDocSummary(string DocId, string FileId, string Anchor, string Text);

public sealed record ImpactOfChangeResult(string RootId, IReadOnlyList<CodeRelationSummary> Edges, IReadOnlyList<string> ImpactedIds)
{
    // deepen-llm-wiki-context-impact track (design §4, add-only): layered enrichment populated by
    // the impact_of_change tool from DeepImpactAsync results. Empty defaults keep the positional
    // v1 constructor — and every legacy ImpactOfChangeAsync caller — unchanged.

    /// <summary>Traversal direction of the layered fields: "up" | "down"; "" when not populated.</summary>
    public string Direction { get; init; } = "";

    /// <summary>BFS layers from <c>DeepImpactAsync</c>; empty when not populated.</summary>
    public IReadOnlyList<ImpactLayer> Layers { get; init; } = [];

    /// <summary>Risk rating: "LOW" | "MEDIUM" | "HIGH" | "CRITICAL"; "" when not populated.</summary>
    public string Risk { get; init; } = "";

    /// <summary>Execution flows containing the root or a reported layer member (capped at 20).</summary>
    public IReadOnlyList<CodeProcessSummary> AffectedProcesses { get; init; } = [];

    /// <summary>Processes cut by the AffectedProcesses cap.</summary>
    public int TruncatedProcesses { get; init; }
}

/// <summary>
/// Traversal direction for <c>DeepImpactAsync</c> (track deepen-llm-wiki-context-impact,
/// design §2 / decisions #1): Up = who (transitively) calls me / is affected by my change
/// (CALLS edges walked in reverse); Down = whom I (transitively) call / depend on.
/// </summary>
public enum ImpactDirection
{
    Up,
    Down,
}

/// <summary>
/// Risk rating of a change (track deepen-llm-wiki-context-impact, design §2): based on the
/// real (pre-truncation) count of directly affected symbols — LOW &lt; 4, MEDIUM 4–9,
/// HIGH &gt;= 10 — escalated to CRITICAL when HIGH and the root participates in &gt;= 3
/// execution flows (ck_process).
/// </summary>
public enum RiskLevel
{
    Low,
    Medium,
    High,
    Critical,
}

/// <summary>
/// Options for <c>DeepImpactAsync</c> (layered impact BFS over CALLS edges).
/// </summary>
/// <param name="Direction">Up = callers of the root transitively (default); Down = its callees. See <see cref="ImpactDirection"/>.</param>
/// <param name="MaxDepth">Number of BFS layers (clamped to 1..16, default 3).</param>
/// <param name="MinConfidence">CALLS edges below this confidence are not traversed (default 0.0 = everything).</param>
/// <param name="MaxPerLayer">Symbols reported per layer; the layer's real total survives in <see cref="ImpactLayer.Total"/> (default 50, min 1).</param>
public sealed record DeepImpactOptions(
    ImpactDirection Direction = ImpactDirection.Up,
    int MaxDepth = 3,
    double MinConfidence = 0.0,
    int MaxPerLayer = 50);

/// <summary>
/// One symbol of an impact layer: decorated with its name and declaration site, plus the
/// highest confidence among the traversal edges that first reached it.
/// </summary>
public sealed record ImpactLayerSymbol(
    string SymbolId,
    string Name,
    string FileId,
    string Path,
    int Line,
    double Confidence);

/// <summary>
/// One BFS layer of <c>DeepImpactAsync</c>: symbols first reached at <paramref name="Depth"/>
/// (1 = directly connected to the root). <paramref name="Total"/> is the real first-reach count;
/// <paramref name="Truncated"/> counts symbols cut by MaxPerLayer (Total - Symbols.Count).
/// Symbols are sorted by symbol id (deterministic).
/// </summary>
public sealed record ImpactLayer(
    int Depth,
    IReadOnlyList<ImpactLayerSymbol> Symbols,
    int Total,
    int Truncated);

/// <summary>
/// Result of <c>DeepImpactAsync</c>. Layers stop at the first empty layer or MaxDepth.
/// <paramref name="AffectedProcesses"/> is the deduplicated set of execution flows the root or
/// any reported layer member participates in (canonical process order, capped at 20;
/// <paramref name="TruncatedProcesses"/> counts the cut). Empty when process tables are
/// absent/empty — never an exception.
/// </summary>
public sealed record DeepImpactResult(
    string RootId,
    ImpactDirection Direction,
    IReadOnlyList<ImpactLayer> Layers,
    RiskLevel Risk,
    IReadOnlyList<CodeProcessSummary> AffectedProcesses,
    int TruncatedProcesses = 0);

public sealed record TraceConceptResult(string ConceptId, IReadOnlyList<CodeRelationSummary> Mentions, IReadOnlyList<CodeDocSummary> Docs);

public sealed record DocsForCodeResult(string TargetId, IReadOnlyList<CodeDocSummary> Docs, bool Missing);

public sealed record ExplainRelationResult(string FromId, string ToId, IReadOnlyList<CodeRelationSummary> Relations);

/// <summary>
/// Options for <c>ComputeCommunitiesAsync</c> (community detection over the weighted
/// CALLS+IMPORTS cluster projection).
/// </summary>
/// <param name="MinConfidence">Edges below this confidence are excluded from the cluster input (default 0.0 = everything).</param>
/// <param name="MaxCommunities">Budget for persisted communities; communities beyond it are truncated by descending size. Default: max(10, min(200, symbolCount / 20)).</param>
/// <param name="MinCommunitySize">Communities with fewer symbol members are dropped as noise (members counted in <see cref="CommunityDetectionResult.NoiseSymbols"/>, not persisted). Default 3.</param>
/// <param name="Algorithm">Community detection algorithm. Only "louvain" is supported today; the option is the strategy seam for future algorithms (e.g. Leiden).</param>
public sealed record CommunityDetectionOptions(
    double MinConfidence = 0.0,
    int? MaxCommunities = null,
    int MinCommunitySize = 3,
    string Algorithm = "louvain");

/// <summary>
/// One detected community (ck_community row). <c>Cohesion</c> = intra-community edge weight /
/// (intra + boundary edge weight) over the cluster input; an isolated community has cohesion 1.0.
/// <c>Label</c> is the longest common dot-separated qualified-name prefix of the members
/// (falling back to the highest weighted-degree member's name), suffixed with #n on collisions.
/// </summary>
public sealed record CodeCommunitySummary(string CommunityId, string Label, double Cohesion, int SymbolCount, string Algo);

/// <summary>
/// Result of <c>ComputeCommunitiesAsync</c>. Output is deterministic: communities are numbered
/// community:001… by (size desc, smallest member symbolId asc) and members are sorted.
/// </summary>
/// <param name="Communities">Persisted communities in canonical order.</param>
/// <param name="MemberCommunity">symbolId → communityId for all persisted members.</param>
/// <param name="NoiseSymbols">Symbols dropped because their community was below MinCommunitySize (diagnostic count; not persisted).</param>
/// <param name="TruncatedCommunities">Communities dropped by the MaxCommunities budget (diagnostic count; not persisted).</param>
public sealed record CommunityDetectionResult(
    IReadOnlyList<CodeCommunitySummary> Communities,
    IReadOnlyDictionary<string, string> MemberCommunity,
    int NoiseSymbols = 0,
    int TruncatedCommunities = 0);

/// <summary>
/// Options for <c>TraceCallPathAsync</c> (shortest call path over the CALLS graph).
/// </summary>
/// <param name="MaxDepth">Maximum number of hops; a shortest path with more hops is reported as not found (default 16).</param>
/// <param name="MinConfidence">CALLS edges below this confidence are excluded from the traced graph (default 0.7).</param>
public sealed record TraceOptions(int MaxDepth = 16, double MinConfidence = 0.7);

/// <summary>
/// One hop of a traced call path: the CALLS edge from <paramref name="FromId"/> to
/// <paramref name="ToId"/> with its call site (FileId:Line) and confidence. When the same
/// symbol pair has multiple call sites, the highest-confidence row is reported
/// (ties broken by file_id, then line ascending — deterministic).
/// </summary>
public sealed record TraceHop(
    string FromId,
    string FromName,
    string ToId,
    string ToName,
    string FileId,
    int Line,
    string Kind,
    double Confidence);

/// <summary>
/// Result of <c>TraceCallPathAsync</c>. <c>Found</c> is false with empty <c>Hops</c> and an
/// explanatory <c>Reason</c> when no path exists under the options (never an exception).
/// Output is deterministic for a fixed graph and options.
/// </summary>
public sealed record TraceResult(bool Found, IReadOnlyList<TraceHop> Hops, string Reason = "");

/// <summary>Which edge graph(s) <c>DetectCyclesAsync</c> inspects for cycles.</summary>
public enum CycleKind
{
    Import,
    Calls,
    Both,
}

/// <summary>
/// One detected cycle: a strongly connected component with more than one member.
/// <c>Kind</c> is the edge kind of the graph ("IMPORTS" or "CALLS"). <c>Members</c> is
/// canonicalized: the walk starts at the smallest member id and follows in-component edges
/// (smallest unvisited successor first), so output is deterministic across runs.
/// </summary>
public sealed record CodeCycle(string Kind, IReadOnlyList<string> Members);

/// <summary>
/// Entry-point kind constants for ck_entry_point (track add-llm-wiki-process-extraction, design §1).
/// main / public_api are detected graph-level by <c>ExtractProcessesAsync</c>; http_route / mcp_tool
/// candidates are written syntax-level by the indexing pipeline; cli_command is folded into main (MVP).
/// </summary>
public static class CodeEntryPointKinds
{
    public const string Main = "main";
    public const string CliCommand = "cli_command";
    public const string HttpRoute = "http_route";
    public const string McpTool = "mcp_tool";
    public const string PublicApi = "public_api";
}

/// <summary>
/// Options for <c>ExtractProcessesAsync</c> (bounded BFS over the CALLS graph from detected entry points).
/// </summary>
/// <param name="MinConfidence">CALLS edges below this confidence are not traversed (default 0.7).</param>
/// <param name="MinSteps">Processes with fewer steps (entry included) are dropped and counted in <see cref="ProcessExtractionResult.DroppedProcesses"/> (default 3).</param>
/// <param name="MaxStepsPerProcess">BFS stops after this many steps; each truncated process increments <see cref="ProcessExtractionResult.TruncatedSteps"/> (default 50).</param>
/// <param name="MaxProcesses">Budget for persisted processes. Default: max(20, min(300, symbolCount / 10)). Entry points beyond the budget (public_api tail first, per canonical entry ordering) are not traversed and counted in <see cref="ProcessExtractionResult.TruncatedProcesses"/>.</param>
/// <param name="EntryPointKinds">Entry-point kinds to traverse (default null = all). Detection and ck_entry_point persistence always cover all kinds; this filters traversal only.</param>
public sealed record ProcessExtractionOptions(
    double MinConfidence = 0.7,
    int MinSteps = 3,
    int MaxStepsPerProcess = 50,
    int? MaxProcesses = null,
    IReadOnlyList<string>? EntryPointKinds = null);

/// <summary>One ck_entry_point row (symbol + entry kind + free-form metadata such as detector evidence).</summary>
public sealed record CodeEntryPointSummary(string SymbolId, string Kind, string Metadata = "");

/// <summary>
/// Entry-point candidate fact for <see cref="CodeKnowledgeBatch.EntryPoints"/> (batch contract,
/// track add-llm-wiki-process-extraction design §3). Used by indexing pipelines to feed
/// syntax-level candidates (http_route / mcp_tool) into ck_entry_point; graph-level kinds
/// (main / public_api) are detected by <c>ExtractProcessesAsync</c> itself.
/// </summary>
public sealed record CodeEntryPointFact(string SymbolId, string Kind, string Metadata = "");

/// <summary>
/// One extracted process (ck_process row). <c>ProcessType</c> is the entry kind, suffixed with
/// ",cross_community" when the steps span two or more ck_member communities.
/// </summary>
public sealed record CodeProcessSummary(string ProcessId, string Name, string EntrySymbolId, string EntryKind, string ProcessType, int StepCount);

/// <summary>One ck_process_step row. Step 0 is the entry symbol itself (empty ViaKind); traversed steps carry ViaKind="CALLS".</summary>
public sealed record CodeProcessStepSummary(string ProcessId, int Step, string SymbolId, string ViaKind);

/// <summary>
/// Result of <c>ExtractProcessesAsync</c>. Output is deterministic: entry points are ordered by
/// (kind priority main &gt; http_route &gt; mcp_tool &gt; public_api, then symbolId asc) and processes
/// are numbered process:001… in that order.
/// </summary>
/// <param name="EntryPoints">All persisted ck_entry_point rows in canonical order (including kinds filtered out of traversal).</param>
/// <param name="Processes">Persisted processes in canonical order.</param>
/// <param name="DroppedProcesses">Traversed entry points whose reachable chain was shorter than MinSteps (diagnostic count; not persisted).</param>
/// <param name="TruncatedSteps">Number of processes whose BFS was cut at MaxStepsPerProcess (the processes are persisted with the truncated step list).</param>
/// <param name="TruncatedProcesses">Entry points beyond the MaxProcesses budget (not traversed at all, so no MinSteps verdict; diagnostic count).</param>
public sealed record ProcessExtractionResult(
    IReadOnlyList<CodeEntryPointSummary> EntryPoints,
    IReadOnlyList<CodeProcessSummary> Processes,
    int DroppedProcesses = 0,
    int TruncatedSteps = 0,
    int TruncatedProcesses = 0);

public sealed record WikiPlan(
    IReadOnlyList<WikiPlanPage> Pages,
    IReadOnlyList<string> MissingDocs,
    IReadOnlyList<string> StaleDocs);

public sealed record WikiPlanPage(string PageId, string Title, IReadOnlyList<string> SourceFileIds, IReadOnlyList<string> SymbolIds, IReadOnlyList<string> DocIds);

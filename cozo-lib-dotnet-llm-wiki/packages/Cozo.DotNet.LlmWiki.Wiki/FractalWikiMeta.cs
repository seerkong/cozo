using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Cozo.DotNet.LlmWiki.Wiki;

// Page-level incremental cache (add-llm-wiki-llm-pipeline track T2.2, design §3).
// `.meta.json` lives at the preview output root and records, per relative page path, the
// stable hashes of that page's deterministic skeleton inputs (structureHash) and of its
// narrative prompt inputs (narrativeInputsHash). A rebuild recomputes both from the graph
// snapshot BEFORE any LLM call: pages whose hashes match are skipped — no disk write, no
// LLM call. Everything here is internal: the public contract is FractalWikiOptions.Force
// plus the FractalWikiResult SkippedPages/RebuiltPages counters.

/// <summary>Per-page cache entry of <see cref="FractalWikiMeta"/>.</summary>
internal sealed class FractalWikiPageMeta
{
    /// <summary>SHA-256 (lowercase hex) of the page's serialized deterministic skeleton inputs.</summary>
    public string StructureHash { get; set; } = "";

    /// <summary>SHA-256 (lowercase hex) of the page's narrative prompt inputs ("" for pages without a narrative slot).</summary>
    public string NarrativeInputsHash { get; set; } = "";

    /// <summary>ISO-8601 UTC timestamp of the last actual regeneration (kept across skips).</summary>
    public string GeneratedAt { get; set; } = "";

    /// <summary>
    /// Whether the page's narrative slot is still pending (LLM unavailable or failed at the last
    /// rebuild). Design §1.5: the pending marker doubles as the incremental backfill point — a
    /// later build with an available LLM rebuilds exactly this page even when both hashes match,
    /// and a successful narrative clears the flag. Always false for pages without a narrative slot.
    /// </summary>
    public bool NarrativePending { get; set; }
}

/// <summary>
/// The `.meta.json` document: { schemaVersion, fromCommit?, pages: { relativePath → hashes } }.
/// fromCommit is the HEAD commit of the work directory at build time (null outside a git repo)
/// — the "graph and wiki agree at this commit" marker of design §3.3.
/// </summary>
internal sealed class FractalWikiMeta
{
    public const string FileName = ".meta.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public int SchemaVersion { get; set; } = 1;

    public string? FromCommit { get; set; }

    public Dictionary<string, FractalWikiPageMeta> Pages { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Loads the meta from the output root; any missing/corrupt/foreign-schema file yields null (full rebuild).</summary>
    public static FractalWikiMeta? TryLoad(string outputRoot)
    {
        var path = Path.Combine(outputRoot, FileName);
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var meta = JsonSerializer.Deserialize<FractalWikiMeta>(File.ReadAllText(path), SerializerOptions);
            return meta is { SchemaVersion: 1 } ? meta : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public async Task SaveAsync(string outputRoot, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(outputRoot);
        await File.WriteAllTextAsync(
            Path.Combine(outputRoot, FileName),
            JsonSerializer.Serialize(this, SerializerOptions),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken);
    }

    /// <summary>Stable content hash: SHA-256 over UTF-8 bytes, lowercase hex.</summary>
    public static string HashText(string payload) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
}

/// <summary>
/// The two invalidation hashes of one page, computed from pre-LLM deterministic inputs, plus the
/// page's narrative user prompt itself ("" for pages without a narrative slot) so the build can
/// issue per-page narrative calls from exactly the hashed inputs.
/// </summary>
internal sealed record FractalWikiPageInputs(string StructureHash, string NarrativeInputsHash, string NarrativePrompt = "");

/// <summary>One group entry of the `.grouping-review.json` document.</summary>
internal sealed record FractalWikiReviewGroup(
    string Id,
    string Name,
    IReadOnlyList<string> Members,
    IReadOnlyList<string> SourceCommunities);

/// <summary>
/// The `.grouping-review.json` human-in-the-loop gate at the output root (design §2 Phase 1.3):
/// each build whose Phase 1 grouping is (re)computed rewrites the draft — final groups
/// (id/name/members/sourceCommunities) plus the raw LLM-suggested review actions — with
/// `"approved": false`. A human may edit the file and set `"approved": true`; from then on its
/// groups are authoritative, the LLM grouping review is skipped, and the draft is never
/// overwritten. Because the approved grouping is a deterministic on-disk input it is applied
/// BEFORE page-hash computation, so edits to an approved file invalidate the group pages.
/// </summary>
internal sealed class FractalWikiGroupingReview
{
    public const string FileName = ".grouping-review.json";

    public bool Approved { get; init; }

    public IReadOnlyList<FractalWikiReviewGroup> Groups { get; init; } = [];

    /// <summary>Loads the review file; missing → null; malformed/empty-approved → null with a diagnostic (default flow).</summary>
    public static FractalWikiGroupingReview? TryLoad(string outputRoot, List<string> diagnostics)
    {
        var path = Path.Combine(outputRoot, FileName);
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject node)
            {
                diagnostics.Add($"phase1: {FileName} is not a JSON object; falling back to the default review flow");
                return null;
            }

            var approved = node["approved"]?.GetValue<bool>() ?? false;
            var groups = new List<FractalWikiReviewGroup>();
            foreach (var entry in (node["groups"] as JsonArray ?? []).OfType<JsonObject>())
            {
                var id = entry["id"]?.GetValue<string>() ?? "";
                if (id.Length == 0)
                {
                    continue;
                }

                groups.Add(new FractalWikiReviewGroup(
                    id,
                    entry["name"]?.GetValue<string>() is { Length: > 0 } name ? name : id,
                    ReadStrings(entry["members"]),
                    ReadStrings(entry["sourceCommunities"])));
            }

            if (approved && groups.Count == 0)
            {
                diagnostics.Add($"phase1: approved {FileName} carries no groups; falling back to the default review flow");
                return null;
            }

            return new FractalWikiGroupingReview { Approved = approved, Groups = groups };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException)
        {
            diagnostics.Add($"phase1: unreadable {FileName}, falling back to the default review flow: {ex.Message}");
            return null;
        }
    }

    /// <summary>Rewrites the unapproved draft from the final Phase 1 groups and the raw LLM suggestions.</summary>
    public static async Task SaveDraftAsync(
        string outputRoot,
        IEnumerable<FractalWikiGroup> groups,
        IReadOnlyList<JsonObject> llmSuggestedActions,
        CancellationToken cancellationToken)
    {
        var groupsJson = new JsonArray();
        foreach (var group in groups.OrderBy(g => g.Id, StringComparer.Ordinal))
        {
            var members = new JsonArray();
            foreach (var member in group.Members.Order(StringComparer.Ordinal))
            {
                members.Add(member);
            }

            var sources = new JsonArray { group.Id };
            foreach (var merged in group.MergedFrom.Order(StringComparer.Ordinal))
            {
                sources.Add(merged);
            }

            groupsJson.Add(new JsonObject
            {
                ["id"] = group.Id,
                ["name"] = group.Name,
                ["members"] = members,
                ["sourceCommunities"] = sources,
            });
        }

        var actions = new JsonArray();
        foreach (var action in llmSuggestedActions)
        {
            actions.Add(action.DeepClone());
        }

        var document = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["approved"] = false,
            ["groups"] = groupsJson,
            ["llmSuggestedActions"] = actions,
        };

        Directory.CreateDirectory(outputRoot);
        await File.WriteAllTextAsync(
            Path.Combine(outputRoot, FileName),
            document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken);
    }

    private static string[] ReadStrings(JsonNode? node) =>
        (node as JsonArray ?? [])
        .Select(item => item?.GetValue<string>() ?? "")
        .Where(item => item.Length > 0)
        .ToArray();
}

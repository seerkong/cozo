using System.Text.Json;

namespace Cozo.DotNet.Om.Depa;

/// <summary>
/// One effect-API whitelist entry (design §4.1). <see cref="Pattern"/> is a dot-segmented
/// FQN glob: '*' matches within a single segment, '**' matches any number of segments.
/// Entries are matched in order; the first hit wins.
/// </summary>
public sealed record DepaEffectRule(string Pattern, string Category, string Direction = "both");

/// <summary>Capsule declaration from depa-map.json (design §4.3, assigned_by=config).</summary>
public sealed record DepaCapsuleConfig(string Name, string RootPath, string InternalsGlob = "**/Internals/**");

/// <summary>Fact-source grading entry from depa-map.json (fact-source-truth ladder, grade 1-7).</summary>
public sealed record DepaFactSourceConfig(string SymbolOrPath, int Grade, string ExpectedOwner = "");

/// <summary>
/// Runtime-parameter placement entry from depa-map.json (design §1.1 depa_runtime_param,
/// track add-llm-wiki-depa-conformance-tools). <see cref="Role"/> follows the
/// runtime | input | config vocabulary; <see cref="DeclaredType"/> names the parameter's
/// declared type symbol (resolved to declared_type_id at scan time) — V-F1 inspects the
/// fields of that type.
/// </summary>
public sealed record DepaRuntimeParamConfig(string SymbolOrPath, string Role, string DeclaredType = "");

/// <summary>
/// One declared architecture layer from depa-map.json (track expand-depa-detection-rules
/// T3.1, violation-catalog C4). Layers are declared low→high: the first entry is the most
/// reusable/innermost layer, later entries are outer calling domains. V-P2 flags a
/// lower-layer symbol that IMPORTS/CALLS/ACCESSES a higher-layer symbol — reusable
/// components must stay unaware of their calling domain.
/// </summary>
public sealed record DepaLayerConfig(string Name, IReadOnlyList<string> PathGlobs);

/// <summary>
/// Parsed depa-map.json (design §4.3): the manual truth source for role annotations.
/// A missing file parses to <see cref="Empty"/> — zero annotations mean the scan reports
/// BLOCKED instead of guessing.
/// </summary>
public sealed record DepaMapConfig(
    IReadOnlyList<DepaCapsuleConfig> Capsules,
    IReadOnlyList<string> ContractPackages,
    IReadOnlyList<string> RuntimeCarrierTypes,
    IReadOnlyList<DepaFactSourceConfig> FactSources)
{
    public static DepaMapConfig Empty { get; } = new([], [], [], []);

    // --- track add-llm-wiki-depa-conformance-tools (add-only, init properties keep the
    // positional G5 constructor and every existing caller unchanged) ---

    /// <summary>Symbols judged as depa_impl purity=core (V-E1/V-E2 subjects), assigned_by=config.</summary>
    public IReadOnlyList<string> Cores { get; init; } = [];

    /// <summary>Symbols judged as depa_projection (V-S1 subjects), assigned_by=config.</summary>
    public IReadOnlyList<string> Projections { get; init; } = [];

    /// <summary>fn(runtime,input,config) placements (V-F1 reads role=config entries), assigned_by=config.</summary>
    public IReadOnlyList<DepaRuntimeParamConfig> RuntimeParams { get; init; } = [];

    // --- track expand-depa-detection-rules T3.1 (add-only batch-2 annotation keys) ---

    /// <summary>
    /// Recovery/startup path globs (slash-segmented; '*' one segment, '**' any depth).
    /// Reads of grade-5 checkpoints / grade-4 journals from inside these paths are the
    /// compliant recovery path; reads from anywhere else are V-S2a/V-S2b phenomena
    /// (fact-source-truth 规则③④).
    /// </summary>
    public IReadOnlyList<string> RecoveryPaths { get; init; } = [];

    /// <summary>
    /// Architecture layers declared low→high (violation-catalog C4): V-P2 flags lower-layer
    /// symbols reaching up into higher layers. Empty means undeclared — V-P2 reports BLOCKED.
    /// </summary>
    public IReadOnlyList<DepaLayerConfig> Layers { get; init; } = [];

    /// <summary>Loads depa-map.json. Missing file yields <see cref="Empty"/>; absent sections yield empty lists.</summary>
    public static DepaMapConfig Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return Empty;
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return Empty;
        }

        var capsules = new List<DepaCapsuleConfig>();
        if (root.TryGetProperty("capsules", out var capsulesEl) && capsulesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in capsulesEl.EnumerateArray())
            {
                var name = GetString(item, "name");
                var rootPath = GetString(item, "rootPath");
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(rootPath))
                {
                    continue;
                }

                var internalsGlob = GetString(item, "internalsGlob");
                capsules.Add(string.IsNullOrWhiteSpace(internalsGlob)
                    ? new DepaCapsuleConfig(name!, rootPath!)
                    : new DepaCapsuleConfig(name!, rootPath!, internalsGlob!));
            }
        }

        var factSources = new List<DepaFactSourceConfig>();
        if (root.TryGetProperty("factSources", out var factsEl) && factsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in factsEl.EnumerateArray())
            {
                var symbolOrPath = GetString(item, "symbolOrPath");
                if (string.IsNullOrWhiteSpace(symbolOrPath)
                    || !item.TryGetProperty("grade", out var gradeEl)
                    || gradeEl.ValueKind != JsonValueKind.Number)
                {
                    continue;
                }

                factSources.Add(new DepaFactSourceConfig(symbolOrPath!, gradeEl.GetInt32(), GetString(item, "expectedOwner") ?? ""));
            }
        }

        var runtimeParams = new List<DepaRuntimeParamConfig>();
        if (root.TryGetProperty("runtimeParams", out var paramsEl) && paramsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in paramsEl.EnumerateArray())
            {
                var symbolOrPath = GetString(item, "symbolOrPath");
                var role = GetString(item, "role");
                if (string.IsNullOrWhiteSpace(symbolOrPath) || string.IsNullOrWhiteSpace(role))
                {
                    continue;
                }

                runtimeParams.Add(new DepaRuntimeParamConfig(symbolOrPath!, role!, GetString(item, "declaredType") ?? ""));
            }
        }

        var layers = new List<DepaLayerConfig>();
        if (root.TryGetProperty("layers", out var layersEl) && layersEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in layersEl.EnumerateArray())
            {
                var name = GetString(item, "name");
                var pathGlobs = item.ValueKind == JsonValueKind.Object ? GetStringList(item, "pathGlobs") : [];
                if (string.IsNullOrWhiteSpace(name) || pathGlobs.Count == 0)
                {
                    continue;
                }

                layers.Add(new DepaLayerConfig(name!, pathGlobs));
            }
        }

        return new DepaMapConfig(
            capsules,
            GetStringList(root, "contractPackages"),
            GetStringList(root, "runtimeCarrierTypes"),
            factSources)
        {
            Cores = GetStringList(root, "cores"),
            Projections = GetStringList(root, "projections"),
            RuntimeParams = runtimeParams,
            RecoveryPaths = GetStringList(root, "recoveryPaths"),
            Layers = layers,
        };
    }

    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static IReadOnlyList<string> GetStringList(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return list.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()!)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToArray();
    }
}

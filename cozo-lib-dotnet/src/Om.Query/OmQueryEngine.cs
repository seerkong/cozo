using System.Text.Json;
using Cozo.DotNet.Datalog;
using Cozo.DotNet.Datalog.Cozo;
using Cozo.DotNet.Om.Contracts;

namespace Cozo.DotNet.Om.Query;

public sealed class OmQueryEngine
{
    private readonly ICozoOmStore _store;
    private readonly OmQueryRegistry _registry;

    public OmQueryEngine(CozoOm om, OmQueryRegistry? registry = null)
        : this(om?.Runtime.Store ?? throw new ArgumentNullException(nameof(om)), registry)
    {
    }

    public OmQueryEngine(ICozoOmStore store, OmQueryRegistry? registry = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _registry = registry ?? new OmQueryRegistry();
    }

    public OmQueryRegistry Registry => _registry;

    public async Task<OmQueryExecutionResult> ExecuteNamedAsync(
        NamedQueryInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!_registry.TryGet(input.Name, out var definition, input.Version))
        {
            return Error(input.Name, input.Version ?? "", OmQueryResultShape.Raw, DatalogDiagnostic.Error("OMQ404", $"NamedQuery '{input.Name}' not found."));
        }

        var missing = (definition.Parameters ?? [])
            .Where(p => p.Required && (input.Parameters is null || !input.Parameters.ContainsKey(p.Name)))
            .Select(p => p.Name)
            .ToArray();
        if (missing.Length > 0)
        {
            return Error(definition.Name, definition.Version, definition.ResultShape, DatalogDiagnostic.Error("OMQ400", $"Missing required query parameters: {string.Join(", ", missing)}"));
        }

        return await ExecutePortableAsync(
            new PortableDatalogQueryInput(
                definition.Source,
                definition.RelationMappings,
                input.Parameters,
                definition.ResultShape,
                definition.SafetyPolicy),
            definition.Name,
            definition.Version,
            cancellationToken);
    }

    public async Task<OmQueryExecutionResult> ExecutePortableAsync(
        PortableDatalogQueryInput input,
        string queryName = "adhoc",
        string version = "v1",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var validation = new PortableDatalogValidator().ParseAndValidate(input.Source);
        if (!validation.Success || validation.Program is null)
        {
            return Error(queryName, version, input.ResultShape, validation.Diagnostics);
        }

        var compiler = new CozoDatalogCompiler();
        var compiled = compiler.Compile(
            validation.Program,
            new CozoDatalogCompileOptions(input.RelationMappings, input.Parameters));
        if (!compiled.Success)
        {
            return Error(queryName, version, input.ResultShape, compiled.Diagnostics);
        }

        var script = ApplyLimit(compiled.Script, input.SafetyPolicy?.Limit);
        var result = await _store.RunAsync(
            script,
            compiled.Parameters,
            input.SafetyPolicy?.Immutable ?? true,
            cancellationToken);

        var table = ToTable(result);
        var graph = input.ResultShape == OmQueryResultShape.Graph ? ToGraph(table) : null;
        return new OmQueryExecutionResult(
            queryName,
            version,
            true,
            input.ResultShape,
            table,
            graph,
            script,
            result.Raw.Clone(),
            []);
    }

    private static OmQueryExecutionResult Error(string queryName, string version, OmQueryResultShape shape, params DatalogDiagnostic[] diagnostics) =>
        Error(queryName, version, shape, (IReadOnlyList<DatalogDiagnostic>)diagnostics);

    private static OmQueryExecutionResult Error(string queryName, string version, OmQueryResultShape shape, IReadOnlyList<DatalogDiagnostic> diagnostics) =>
        new(queryName, version, false, shape, new OmQueryTable([], []), null, null, null, diagnostics);

    private static string ApplyLimit(string script, int? limit)
    {
        if (limit is null or <= 0) return script;
        if (script.Contains(":limit", StringComparison.Ordinal)) return script;
        return $"{script}\n:limit {limit.Value}";
    }

    private static OmQueryTable ToTable(OmQueryResult result)
    {
        var rows = result.Rows
            .Select(row =>
            {
                var dict = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                for (var i = 0; i < result.Headers.Count && i < row.Count; i++)
                {
                    dict[result.Headers[i]] = row[i].Clone();
                }

                return (IReadOnlyDictionary<string, JsonElement>)dict;
            })
            .ToArray();
        return new OmQueryTable(result.Headers, rows);
    }

    private static OmQueryGraph ToGraph(OmQueryTable table)
    {
        var nodes = new Dictionary<string, OmQueryGraphNode>(StringComparer.Ordinal);
        var edges = new List<OmQueryGraphEdge>();
        foreach (var row in table.Rows)
        {
            var source = StringValue(row, "source") ?? StringValue(row, "from_id") ?? StringValue(row, "from");
            var target = StringValue(row, "target") ?? StringValue(row, "to_id") ?? StringValue(row, "to");
            var kind = StringValue(row, "kind") ?? StringValue(row, "rel_name") ?? StringValue(row, "relation") ?? "edge";
            if (source is null || target is null) continue;
            nodes.TryAdd(source, new OmQueryGraphNode(source, source, "node", new Dictionary<string, JsonElement>()));
            nodes.TryAdd(target, new OmQueryGraphNode(target, target, "node", new Dictionary<string, JsonElement>()));
            edges.Add(new OmQueryGraphEdge(source, target, kind, row));
        }

        return new OmQueryGraph(nodes.Values.ToArray(), edges);
    }

    private static string? StringValue(IReadOnlyDictionary<string, JsonElement> row, string key)
    {
        foreach (var (candidateKey, value) in row)
        {
            if (string.Equals(candidateKey, key, StringComparison.OrdinalIgnoreCase) &&
                value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }

        return null;
    }
}

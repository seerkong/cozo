using System.Text.Json;
using Cozo.DotNet.Datalog;
using Cozo.DotNet.Datalog.Cozo;

namespace Cozo.DotNet.Om.Query;

public enum OmQueryResultShape
{
    Table,
    Graph,
    Raw,
}

public sealed record NamedQueryParameter(
    string Name,
    string? Type = null,
    bool Required = true,
    string? Description = null);

public sealed record OmQuerySafetyPolicy(
    int? Limit = null,
    bool Immutable = true);

public sealed record NamedQueryDefinition(
    string Name,
    string Version,
    string Source,
    IReadOnlyDictionary<string, CozoStoredRelationMapping> RelationMappings,
    OmQueryResultShape ResultShape = OmQueryResultShape.Table,
    IReadOnlyList<NamedQueryParameter>? Parameters = null,
    OmQuerySafetyPolicy? SafetyPolicy = null,
    string Description = "");

public sealed record PortableDatalogQueryInput(
    string Source,
    IReadOnlyDictionary<string, CozoStoredRelationMapping> RelationMappings,
    IReadOnlyDictionary<string, object?>? Parameters = null,
    OmQueryResultShape ResultShape = OmQueryResultShape.Table,
    OmQuerySafetyPolicy? SafetyPolicy = null);

public sealed record NamedQueryInput(
    string Name,
    IReadOnlyDictionary<string, object?>? Parameters = null,
    string? Version = null);

public sealed record OmQueryExecutionResult(
    string QueryName,
    string Version,
    bool Success,
    OmQueryResultShape Shape,
    OmQueryTable Table,
    OmQueryGraph? Graph,
    string? Script,
    JsonElement? Raw,
    IReadOnlyList<DatalogDiagnostic> Diagnostics)
{
    public bool HasErrors => Diagnostics.Any(d => d.Severity == DatalogDiagnosticSeverity.Error);
}

public sealed record OmQueryTable(
    IReadOnlyList<string> Headers,
    IReadOnlyList<IReadOnlyDictionary<string, JsonElement>> Rows);

public sealed record OmQueryGraph(
    IReadOnlyList<OmQueryGraphNode> Nodes,
    IReadOnlyList<OmQueryGraphEdge> Edges);

public sealed record OmQueryGraphNode(string Id, string Label, string Kind, IReadOnlyDictionary<string, JsonElement> Properties);

public sealed record OmQueryGraphEdge(string Source, string Target, string Kind, IReadOnlyDictionary<string, JsonElement> Properties);

using System.Text.Json;

namespace Cozo.DotNet.VizServer;

public sealed record DemoTable(string Name, IReadOnlyList<string> Columns, IReadOnlyList<Dictionary<string, JsonElement>> Rows);

public sealed record DemoQuery(
    string Id,
    string Label,
    string Description,
    string Meaning,
    string Dsl,
    string DefaultView);

public sealed record DemoDefinition(
    string Id,
    string Label,
    IReadOnlyList<DemoTable> Tables,
    IReadOnlyList<DemoQuery> Queries,
    DemoSeed Seed,
    IReadOnlyDictionary<string, DemoQueryPlan> Plans);

public sealed record DemoSeed(
    IReadOnlyList<DemoTypeDef> Types,
    IReadOnlyList<DemoAttributeDef> Attributes,
    IReadOnlyList<DemoRelationDef> Relations,
    IReadOnlyList<DemoTable> BatchTables);

public sealed record DemoTypeDef(string TypeName, string Description = "", string? ParentType = null);

public sealed record DemoAttributeDef(string TypeName, string AttrName, string ValueType, bool Required = false, string Description = "");

public sealed record DemoRelationDef(string RelName, string FromType, string ToType, bool Directed = true, string Description = "");

public sealed record DemoQueryPlan(
    string Kind,
    string? TypeName = null,
    string? AttrName = null,
    double? MinNumber = null,
    string? ExcludeString = null,
    string? RootId = null,
    IReadOnlyList<string>? RelNames = null,
    string Direction = "outgoing",
    int MaxDepth = 3,
    int TopK = 5,
    double DegreeWeight = 1,
    string? Action = null,
    string? AsOf = null);

public sealed record RunRequest(string DemoId, string QueryId, IReadOnlyList<DemoTable>? Tables = null);

public sealed record TableResult(IReadOnlyList<string> Columns, IReadOnlyList<Dictionary<string, object?>> Rows);

public sealed record FrontendGraph(IReadOnlyList<FrontendGraphNode> Nodes, IReadOnlyList<FrontendGraphEdge> Edges);

public sealed record FrontendGraphNode(string Id, string Label, string? Group = null);

public sealed record FrontendGraphEdge(string From, string To, string? Label = null);

public sealed record FrontendTreeNode(string Id, string Label, IReadOnlyList<FrontendTreeNode>? Children = null);

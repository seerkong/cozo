using System.Text.Json;
using Cozo.DotNet;
using Cozo.DotNet.Om;
using Cozo.DotNet.Om.Analytics;
using Cozo.DotNet.Om.Batch;
using Cozo.DotNet.Om.Contracts.Models;
using Cozo.DotNet.Om.Inputs;

namespace Cozo.DotNet.VizServer;

public static class DemoRunner
{
    public static async Task<IResult> RunAsync(RunRequest request)
    {
        var demo = DemoCatalog.Find(request.DemoId);
        if (demo is null)
        {
            return Results.Json(new { status = "error", error = $"Unknown demo: {request.DemoId}" });
        }

        if (!demo.Plans.TryGetValue(request.QueryId, out var plan))
        {
            return Results.Json(new { status = "error", error = $"Unknown query: {request.QueryId}" });
        }

        using var db = new CozoDb(engine: "mem", path: "");
        var om = new CozoOm(db);
        await SeedAsync(om, demo, request.Tables is { Count: > 0 } ? request.Tables : demo.Seed.BatchTables);

        return plan.Kind switch
        {
            "table" => Results.Json(new { status = "ok", table = await TableQueryAsync(om, plan) }),
            "impact" => Results.Json(new
            {
                status = "ok",
                graph = ToFrontendGraph((await om.ImpactAnalysisAsync(new ImpactAnalysisInput(
                    plan.RootId ?? "",
                    plan.RelNames ?? [],
                    MaxDepth: plan.MaxDepth,
                    Direction: Direction(plan.Direction)))).Data.Visual.Graph)
            }),
            "tree" => Results.Json(new
            {
                status = "ok",
                tree = ToFrontendTreeNodes(
                    (await om.OwnershipTreeAsync(new OwnershipTreeInput(plan.RootId ?? "", plan.RelNames ?? [], MaxDepth: plan.MaxDepth))).Data.Visual.Tree,
                    await LabelsByIdAsync(om, demo.Seed.Types.Select(t => t.TypeName)))
            }),
            "risk" => Results.Json(new
            {
                status = "ok",
                table = ToTable((await om.RiskHotspotAsync(new RiskHotspotInput(
                    plan.TypeName ?? "",
                    plan.AttrName ?? "",
                    TopK: plan.TopK,
                    DegreeWeight: plan.DegreeWeight))).Data.Visual.Ranking)
            }),
            "action" => Results.Json(new { status = "ok", table = await ActionDemoAsync(om, plan) }),
            "temporal" => Results.Json(new { status = "ok", table = await TemporalDemoAsync(om, plan) }),
            _ => Results.Json(new { status = "error", error = $"Unsupported query: {request.QueryId}" })
        };
    }

    private static async Task SeedAsync(CozoOm om, DemoDefinition demo, IReadOnlyList<DemoTable> batchTables)
    {
        await om.InitSchemaAsync();
        foreach (var type in demo.Seed.Types)
        {
            await om.DefineTypeAsync(type.TypeName, type.Description, type.ParentType);
        }

        foreach (var attr in demo.Seed.Attributes)
        {
            await om.DefineAttributeAsync(attr.TypeName, attr.AttrName, ValueType(attr.ValueType), attr.Required, attr.Description);
        }

        foreach (var rel in demo.Seed.Relations)
        {
            await om.DefineRelationAsync(rel.RelName, rel.FromType, rel.ToType, rel.Directed, rel.Description);
        }

        await om.IngestBatchAsync(ToBatch(batchTables), new OmBatchOptions(ValidateRequired: false));
    }

    private static OmBatchInput ToBatch(IReadOnlyList<DemoTable> tables)
    {
        var entities = new List<OmBatchEntity>();
        var properties = new List<OmBatchProperty>();
        var edges = new List<OmBatchEdge>();
        foreach (var table in tables)
        {
            if (string.Equals(table.Name, "entities", StringComparison.OrdinalIgnoreCase) || string.Equals(table.Name, "实体数据", StringComparison.OrdinalIgnoreCase))
            {
                entities.AddRange(table.Rows.Select(row => new OmBatchEntity(StringAt(row, "id"), StringAt(row, "typeName"), StringAt(row, "label"))));
            }
            else if (string.Equals(table.Name, "properties", StringComparison.OrdinalIgnoreCase) || string.Equals(table.Name, "属性数据", StringComparison.OrdinalIgnoreCase))
            {
                properties.AddRange(table.Rows.Select(row => new OmBatchProperty(StringAt(row, "entityId"), StringAt(row, "attrName"), ValueAt(row, "value"))));
            }
            else if (string.Equals(table.Name, "edges", StringComparison.OrdinalIgnoreCase) || string.Equals(table.Name, "边数据", StringComparison.OrdinalIgnoreCase))
            {
                edges.AddRange(table.Rows.Select(row => new OmBatchEdge(StringAt(row, "fromId"), StringAt(row, "relName"), StringAt(row, "toId"), ValueAt(row, "props") ?? new Dictionary<string, object?>())));
            }
        }

        return new OmBatchInput(entities, properties, edges);
    }

    private static async Task<TableResult> TableQueryAsync(CozoOm om, DemoQueryPlan plan)
    {
        var rows = new List<Dictionary<string, object?>>();
        foreach (var entity in await om.FindByTypeAsync(plan.TypeName ?? "", new FindByTypeOptions(Exact: false)))
        {
            var value = string.IsNullOrWhiteSpace(plan.AttrName) ? null : JsonToObject(await om.GetPropertyAsync(entity.Id, plan.AttrName));
            if (plan.MinNumber.HasValue && ToDouble(value) < plan.MinNumber.Value)
            {
                continue;
            }

            rows.Add(new Dictionary<string, object?>
            {
                ["id"] = entity.Id,
                ["label"] = entity.Label,
                ["typeName"] = entity.TypeName,
                [plan.AttrName ?? "value"] = value
            });
        }

        var attr = plan.AttrName ?? "value";
        return new TableResult(["id", "label", "typeName", attr], rows);
    }

    private static async Task<TableResult> ActionDemoAsync(CozoOm om, DemoQueryPlan plan)
    {
        switch (plan.Action)
        {
            case "approve":
                await om.SetPropertyAsync("req:1002", "status", "approved");
                await om.SetPropertyAsync("req:1002", "effective_status", "approved", new WriteOptions(ValidTime: "2026-02-01T00:00:00Z"));
                return await ApprovalRequestsTableAsync(om, ["req:1002"]);
            case "submit":
                await om.SetPropertyAsync("req:1001", "status", "submitted");
                await om.SetPropertyAsync("req:1001", "effective_status", "submitted", new WriteOptions(ValidTime: "2026-03-01T00:00:00Z"));
                await om.LinkEntitiesAsync("req:1001", "belongs_to_dept", "dept:it");
                await om.LinkEntitiesAsync("req:1001", "assigned_to", "appr:alice");
                return await ApprovalRequestsTableAsync(om, ["req:1001"]);
            case "validate":
                var highRisk = await om.GetPropertyAsync("req:1002", "risk_score");
                var requiresReview = await om.GetPropertyAsync("req:1002", "requires_review");
                var valid = ToDouble(JsonToObject(highRisk)) <= 80 || (requiresReview?.ValueKind == JsonValueKind.True);
                return new TableResult(["entityId", "valid", "errors"], [new Dictionary<string, object?> { ["entityId"] = "dept:finance", ["valid"] = valid, ["errors"] = valid ? "" : "requires_review must be true when risk_score > 80" }]);
            case "timeline":
                await om.SetPropertyAsync("req:1001", "status", "submitted");
                await om.SetPropertyAsync("req:1001", "effective_status", "submitted", new WriteOptions(ValidTime: "2026-03-01T00:00:00Z"));
                await om.SetPropertyAsync("req:1001", "effective_status", "approved", new WriteOptions(ValidTime: "2026-04-01T00:00:00Z"));
                return new TableResult(["as_of", "status"], [
                    new Dictionary<string, object?> { ["as_of"] = "2026-03-15", ["status"] = JsonToObject(await om.GetPropertyAsOfAsync("req:1001", "effective_status", "2026-03-15T00:00:00Z")) },
                    new Dictionary<string, object?> { ["as_of"] = "2026-04-15", ["status"] = JsonToObject(await om.GetPropertyAsOfAsync("req:1001", "effective_status", "2026-04-15T00:00:00Z")) }
                ]);
            default:
                return await ApprovalRequestsTableAsync(om, ["req:1001", "req:1002", "req:1003"]);
        }
    }

    private static async Task<TableResult> ApprovalRequestsTableAsync(CozoOm om, IReadOnlyList<string> ids)
    {
        var rows = new List<Dictionary<string, object?>>();
        foreach (var id in ids)
        {
            var view = await om.GetEntityViewAsync(id);
            if (view is null) continue;
            var assigned = await om.GetNeighborsAsync(id, "assigned_to", OmDirection.Outgoing);
            rows.Add(new Dictionary<string, object?>
            {
                ["id"] = view.Id,
                ["label"] = view.Label,
                ["status"] = JsonToObject(await om.GetPropertyAsync(id, "status")),
                ["effective_status"] = JsonToObject(await om.GetPropertyAsync(id, "effective_status")),
                ["amount"] = JsonToObject(await om.GetPropertyAsync(id, "amount")),
                ["risk_score"] = JsonToObject(await om.GetPropertyAsync(id, "risk_score")),
                ["requires_review"] = JsonToObject(await om.GetPropertyAsync(id, "requires_review")),
                ["approval_chain_length"] = assigned.Outgoing.Count
            });
        }

        return new TableResult(["id", "label", "status", "effective_status", "amount", "risk_score", "requires_review", "approval_chain_length"], rows);
    }

    private static async Task<TableResult> TemporalDemoAsync(CozoOm om, DemoQueryPlan plan)
    {
        if (plan.Action == "headcount")
        {
            var rows = new List<Dictionary<string, object?>>();
            foreach (var dept in await om.FindByTypeAsync("Department", new FindByTypeOptions(Exact: true)))
            {
                var count = (await om.GetNeighborsAsync(dept.Id, "belongs_to", OmDirection.Incoming)).Incoming.Count;
                rows.Add(new Dictionary<string, object?> { ["as_of"] = plan.AsOf, ["department_id"] = dept.Id, ["department"] = dept.Label, ["headcount"] = count });
            }

            return new TableResult(["as_of", "department_id", "department", "headcount"], rows);
        }

        if (plan.Action == "timeline")
        {
            return new TableResult(["employee_id", "valid_from", "department_id", "department_label"], [
                new Dictionary<string, object?> { ["employee_id"] = "emp:alice", ["valid_from"] = "2024-01-01", ["department_id"] = "dept:eng", ["department_label"] = "Engineering" },
                new Dictionary<string, object?> { ["employee_id"] = "emp:alice", ["valid_from"] = "2024-10-01", ["department_id"] = "dept:product", ["department_label"] = "Product" }
            ]);
        }

        var resultRows = new List<Dictionary<string, object?>>();
        foreach (var emp in await om.FindByTypeAsync("Employee", new FindByTypeOptions(Exact: true)))
        {
            var dept = (await om.GetNeighborsAsync(emp.Id, "belongs_to", OmDirection.Outgoing)).Outgoing.FirstOrDefault();
            var team = (await om.GetNeighborsAsync(emp.Id, "manages", OmDirection.Outgoing)).Outgoing.FirstOrDefault();
            resultRows.Add(new Dictionary<string, object?>
            {
                ["as_of"] = plan.AsOf,
                ["employee_id"] = emp.Id,
                ["employee"] = emp.Label,
                ["department_id"] = dept?.EntityId,
                ["department"] = dept?.Label,
                ["team_id"] = team?.EntityId,
                ["team"] = team?.Label
            });
        }

        return new TableResult(["as_of", "employee_id", "employee", "department_id", "department", "team_id", "team"], resultRows);
    }

    private static TableResult ToTable(IReadOnlyList<RankingVisualEntry> ranking)
    {
        return new TableResult(
            ["rank", "id", "label", "score", "baseScore", "degree", "degreeWeight"],
            ranking.Select(row => new Dictionary<string, object?>
            {
                ["rank"] = row.Rank,
                ["id"] = row.Id,
                ["label"] = row.Label,
                ["score"] = row.Score,
                ["baseScore"] = row.Factors.BaseScore,
                ["degree"] = row.Factors.Degree,
                ["degreeWeight"] = row.Factors.DegreeWeight
            }).ToArray());
    }

    private static FrontendGraph ToFrontendGraph(GraphVisual graph)
    {
        return new FrontendGraph(
            graph.Nodes.Select(node => new FrontendGraphNode(node.Id, node.Label, node.Group)).ToArray(),
            graph.Edges.Select(edge => new FrontendGraphEdge(edge.Source, edge.Target, edge.Label)).ToArray());
    }

    private static async Task<IReadOnlyDictionary<string, string>> LabelsByIdAsync(CozoOm om, IEnumerable<string> typeNames)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var type in typeNames)
        {
            foreach (var entity in await om.FindByTypeAsync(type, new FindByTypeOptions(Exact: true)))
            {
                labels[entity.Id] = entity.Label;
            }
        }

        return labels;
    }

    private static IReadOnlyList<FrontendTreeNode> ToFrontendTreeNodes(TreeVisual tree, IReadOnlyDictionary<string, string> labels)
    {
        FrontendTreeNode Build(string id)
        {
            var children = tree.ChildrenById.TryGetValue(id, out var edges) ? edges.Select(edge => Build(edge.ToId)).ToArray() : [];
            return new FrontendTreeNode(id, labels.TryGetValue(id, out var label) ? label : id, children.Length == 0 ? null : children);
        }

        return [Build(tree.RootId)];
    }

    private static OmAnalyticsDirection Direction(string value) =>
        string.Equals(value, "incoming", StringComparison.OrdinalIgnoreCase) ? OmAnalyticsDirection.Incoming : OmAnalyticsDirection.Outgoing;

    private static OmValueType ValueType(string value) => Enum.TryParse<OmValueType>(value, ignoreCase: true, out var parsed) ? parsed : OmValueType.Json;

    private static string StringAt(Dictionary<string, JsonElement> row, string key)
    {
        return row.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : row.TryGetValue(key, out value) ? value.ToString() : "";
    }

    private static object? ValueAt(Dictionary<string, JsonElement> row, string key)
    {
        return row.TryGetValue(key, out var value) ? JsonToObject(value) : null;
    }

    private static object? JsonToObject(JsonElement? element)
    {
        if (!element.HasValue) return null;
        return element.Value.ValueKind switch
        {
            JsonValueKind.String => element.Value.GetString(),
            JsonValueKind.Number when element.Value.TryGetInt64(out var longValue) => longValue,
            JsonValueKind.Number when element.Value.TryGetDouble(out var doubleValue) => doubleValue,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => JsonSerializer.Deserialize<object?>(element.Value.GetRawText(), new JsonSerializerOptions(JsonSerializerDefaults.Web))
        };
    }

    private static double ToDouble(object? value) => value switch
    {
        int x => x,
        long x => x,
        float x => x,
        double x => x,
        decimal x => (double)x,
        JsonElement { ValueKind: JsonValueKind.Number } x when x.TryGetDouble(out var d) => d,
        _ => 0
    };
}

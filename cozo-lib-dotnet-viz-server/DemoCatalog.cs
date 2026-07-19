using System.Text.Json;

namespace Cozo.DotNet.VizServer;

public static class DemoCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IReadOnlyList<DemoDefinition> All { get; } =
    [
        BuildProcurement(),
        BuildHr(),
        BuildCrm(),
        BuildResourceGraph(),
        BuildApprovalFlow(),
        BuildOrgTimeline()
    ];

    public static DemoDefinition? Find(string id) => All.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.Ordinal));

    private static DemoDefinition BuildProcurement()
    {
        var types = Types("Supplier", "PurchaseOrder", "LineItem", "Warehouse", "Contract");
        var attrs = Attrs(
            A("Supplier", "rating", "Number"), A("Supplier", "country", "String"), A("Supplier", "contact_email", "String"),
            A("PurchaseOrder", "total_amount", "Number"), A("PurchaseOrder", "status", "String"), A("PurchaseOrder", "order_date", "String"),
            A("LineItem", "unit_price", "Number"), A("LineItem", "quantity", "Number"), A("LineItem", "sku", "String"),
            A("Warehouse", "capacity", "Number"), A("Warehouse", "location", "String"),
            A("Contract", "risk_score", "Number"), A("Contract", "terms_json", "Json"), A("Contract", "valid_until", "String"));
        var rels = Rels(
            R("placed_with", "PurchaseOrder", "Supplier"), R("has_line_item", "PurchaseOrder", "LineItem"),
            R("fulfilled_by", "LineItem", "Warehouse"), R("covered_by", "PurchaseOrder", "Contract"),
            R("supplies", "Supplier", "Warehouse"));
        var batch = Batch(
            [
                E("s:acme", "Supplier", "先达公司"), E("s:globex", "Supplier", "环宇工业"), E("s:initech", "Supplier", "启泰有限公司"),
                E("po:1001", "PurchaseOrder", "采购单-1001"), E("po:1002", "PurchaseOrder", "采购单-1002"), E("po:1003", "PurchaseOrder", "采购单-1003"),
                E("li:a", "LineItem", "钢梁 x200"), E("li:b", "LineItem", "铜线 x500"), E("li:c", "LineItem", "电路板 x1000"), E("li:d", "LineItem", "橡胶垫圈 x300"),
                E("wh:east", "Warehouse", "华东仓储中心"), E("wh:west", "Warehouse", "华西仓储中心"),
                E("ct:master", "Contract", "主供应协议"), E("ct:spot", "Contract", "现货采购合同")
            ],
            [
                P("s:acme", "rating", 4.5), P("s:acme", "country", "US"), P("s:globex", "rating", 3.8), P("s:globex", "country", "DE"), P("s:initech", "rating", 2.1), P("s:initech", "country", "CN"),
                P("po:1001", "total_amount", 54000), P("po:1001", "status", "approved"), P("po:1002", "total_amount", 12750), P("po:1002", "status", "pending"), P("po:1003", "total_amount", 87200), P("po:1003", "status", "approved"),
                P("li:a", "unit_price", 270), P("li:a", "quantity", 200), P("li:b", "unit_price", 25.5), P("li:b", "quantity", 500), P("li:c", "unit_price", 87.2), P("li:c", "quantity", 1000), P("li:d", "unit_price", 4.25), P("li:d", "quantity", 300),
                P("wh:east", "capacity", 50000), P("wh:west", "capacity", 35000), P("ct:master", "risk_score", 15), P("ct:master", "terms_json", new { duration_months = 24, auto_renew = true }), P("ct:spot", "risk_score", 42), P("ct:spot", "terms_json", new { duration_months = 3, auto_renew = false })
            ],
            [
                L("po:1001", "placed_with", "s:acme"), L("po:1002", "placed_with", "s:globex"), L("po:1003", "placed_with", "s:initech"),
                L("po:1001", "has_line_item", "li:a"), L("po:1001", "has_line_item", "li:b"), L("po:1002", "has_line_item", "li:c"), L("po:1003", "has_line_item", "li:d"),
                L("li:a", "fulfilled_by", "wh:east"), L("li:b", "fulfilled_by", "wh:west"), L("li:c", "fulfilled_by", "wh:east"), L("li:d", "fulfilled_by", "wh:west"),
                L("po:1001", "covered_by", "ct:master"), L("po:1003", "covered_by", "ct:spot"), L("s:acme", "supplies", "wh:east"), L("s:globex", "supplies", "wh:west")
            ]);
        return Demo("procurement", "采购管理", types, attrs, rels, batch,
            TablePlan("dslQuery", "高价值行项目", "LineItem", "unit_price", 20),
            Impact("po:1001", ["has_line_item", "fulfilled_by", "placed_with", "covered_by"], "outgoing"),
            Tree("s:acme", ["supplies", "placed_with"]),
            Risk("Contract", "risk_score", 1));
    }

    private static DemoDefinition BuildHr()
    {
        var types = Types("Employee", "Department", "Position", "Skill", "ReviewCycle");
        var attrs = Attrs(A("Employee", "salary", "Number"), A("Employee", "level", "String"), A("Department", "budget", "Number"), A("ReviewCycle", "year", "Number"), A("ReviewCycle", "status", "String"));
        var rels = Rels(R("reports_to", "Employee", "Employee"), R("works_in", "Employee", "Department"), R("fills_position", "Employee", "Position"), R("requires_skill", "Position", "Skill"), R("reviewed_in", "Employee", "ReviewCycle"));
        var batch = Batch(
            [E("emp:alice", "Employee", "陈晓琳"), E("emp:bob", "Employee", "马志远"), E("emp:carol", "Employee", "王思雨"), E("emp:dave", "Employee", "金大伟"), E("dept:eng", "Department", "工程部"), E("pos:lead", "Position", "技术负责人"), E("sk:sys", "Skill", "系统设计"), E("rc:q1", "ReviewCycle", "2026 Q1")],
            [P("emp:alice", "salary", 180000), P("emp:bob", "salary", 145000), P("emp:carol", "salary", 120000), P("emp:dave", "salary", 98000), P("dept:eng", "budget", 1000000), P("rc:q1", "year", 2026), P("rc:q1", "status", "closed")],
            [L("emp:bob", "reports_to", "emp:alice"), L("emp:carol", "reports_to", "emp:alice"), L("emp:dave", "reports_to", "emp:bob"), L("emp:alice", "works_in", "dept:eng"), L("emp:alice", "fills_position", "pos:lead"), L("pos:lead", "requires_skill", "sk:sys"), L("emp:alice", "reviewed_in", "rc:q1")]);
        var definition = Demo("hr", "人力资源", types, attrs, rels, batch,
            TablePlan("dslQuery", "高薪员工查询", "Employee", "salary", 140000),
            Impact("emp:alice", ["reports_to"], "incoming"),
            Tree("emp:alice", ["fills_position", "requires_skill"]),
            Risk("Employee", "salary", 10000));

        return definition with
        {
            Queries = definition.Queries.Concat([new DemoQuery("employees", "Employees", "List employees", "List employee rows", "?[id, label] := *om_entity{id, type_name: \"Employee\", label}", "table")]).ToArray(),
            Plans = definition.Plans.Concat([new KeyValuePair<string, DemoQueryPlan>("employees", new DemoQueryPlan("table", TypeName: "Employee", AttrName: "salary"))]).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal)
        };
    }

    private static DemoDefinition BuildCrm()
    {
        var types = Types("SalesRep", "Account", "Contact", "Lead", "Opportunity", "Activity");
        var attrs = Attrs(A("Opportunity", "amount", "Number"), A("Opportunity", "stage", "String"), A("Lead", "score", "Number"), A("Activity", "kind", "String"));
        var rels = Rels(R("owned_by", "Opportunity", "SalesRep"), R("has_opportunity", "Account", "Opportunity"), R("has_contact", "Account", "Contact"), R("has_activity", "Opportunity", "Activity"), R("converted_to", "Lead", "Opportunity"));
        var batch = Batch(
            [E("rep:li", "SalesRep", "李雷"), E("rep:wang", "SalesRep", "王芳"), E("acct:acme", "Account", "先达客户"), E("acct:globex", "Account", "环宇客户"), E("ct:alice", "Contact", "Alice"), E("ct:bob", "Contact", "Bob"), E("lead:web-ship", "Lead", "官网线索-航运"), E("opp:acme-renew", "Opportunity", "ACME 续费 2026"), E("opp:globex-new", "Opportunity", "Globex 新签"), E("act:call", "Activity", "电话跟进"), E("act:demo", "Activity", "现场演示")],
            [P("opp:acme-renew", "amount", 120000), P("opp:acme-renew", "stage", "proposal"), P("opp:globex-new", "amount", 85000), P("opp:globex-new", "stage", "negotiation"), P("lead:web-ship", "score", 72), P("act:call", "kind", "call"), P("act:demo", "kind", "demo")],
            [L("acct:acme", "has_opportunity", "opp:acme-renew"), L("acct:globex", "has_opportunity", "opp:globex-new"), L("acct:acme", "has_contact", "ct:alice"), L("acct:acme", "has_contact", "ct:bob"), L("opp:acme-renew", "owned_by", "rep:li"), L("opp:globex-new", "owned_by", "rep:wang"), L("opp:acme-renew", "has_activity", "act:call"), L("opp:acme-renew", "has_activity", "act:demo"), L("lead:web-ship", "converted_to", "opp:acme-renew")]);
        return Demo("crm", "CRM 销售", types, attrs, rels, batch,
            TablePlan("dslQuery", "高金额商机查询", "Opportunity", "amount", 60000, "stage", "closed_lost"),
            Impact("acct:acme", ["has_opportunity", "owned_by", "has_activity", "has_contact"], "outgoing"),
            Tree("acct:acme", ["has_contact", "has_opportunity", "owned_by", "has_activity"]),
            Risk("Opportunity", "amount", 1000));
    }

    private static DemoDefinition BuildResourceGraph()
    {
        var types = new[] { new DemoTypeDef("Resource"), new DemoTypeDef("ExecutableResource", ParentType: "Resource"), new DemoTypeDef("ApiService", ParentType: "ExecutableResource"), new DemoTypeDef("Worker", ParentType: "ExecutableResource"), new DemoTypeDef("Dataset", ParentType: "Resource") };
        var attrs = Attrs(A("Resource", "resource_key", "String"), A("ApiService", "endpoint_count", "Number"), A("Worker", "throughput", "Number"), A("Dataset", "size_mb", "Number"));
        var rels = Rels(R("depends_on", "ExecutableResource", "ExecutableResource"), R("contained_in", "Resource", "Resource"), R("produces", "ExecutableResource", "Dataset"));
        var batch = Batch(
            [E("api:gateway", "ApiService", "Gateway API"), E("api:catalog", "ApiService", "Catalog API"), E("worker:indexer", "Worker", "Index Builder"), E("data:catalog", "Dataset", "Catalog Dataset"), E("data:index", "Dataset", "Search Index"), E("scope:platform", "Resource", "Platform Scope")],
            [P("api:gateway", "resource_key", "gateway-api"), P("api:gateway", "endpoint_count", 18), P("api:catalog", "resource_key", "catalog-api"), P("api:catalog", "endpoint_count", 11), P("worker:indexer", "resource_key", "index-builder"), P("worker:indexer", "throughput", 1200), P("data:catalog", "resource_key", "catalog-data"), P("data:catalog", "size_mb", 640), P("data:index", "resource_key", "search-index"), P("data:index", "size_mb", 380)],
            [L("api:gateway", "depends_on", "api:catalog"), L("api:catalog", "produces", "data:catalog"), L("worker:indexer", "produces", "data:index"), L("api:gateway", "contained_in", "scope:platform"), L("api:catalog", "contained_in", "scope:platform"), L("worker:indexer", "contained_in", "scope:platform"), L("data:catalog", "contained_in", "scope:platform"), L("data:index", "contained_in", "scope:platform")]);
        return Demo("resource-graph", "通用资源图（继承）", types, attrs, rels, batch,
            TablePlan("dslQuery", "多态资源列表", "Resource", "resource_key", null),
            Impact("api:gateway", ["depends_on", "produces"], "outgoing"),
            Tree("scope:platform", ["contained_in"]),
            Risk("Dataset", "size_mb", 10));
    }

    private static DemoDefinition BuildApprovalFlow()
    {
        var types = Types("Department", "Approver", "ApprovalRequest");
        var attrs = Attrs(A("ApprovalRequest", "status", "String"), A("ApprovalRequest", "effective_status", "String"), A("ApprovalRequest", "amount", "Number"), A("ApprovalRequest", "risk_score", "Number"), A("ApprovalRequest", "requires_review", "Bool"));
        var rels = Rels(R("belongs_to_dept", "ApprovalRequest", "Department"), R("assigned_to", "ApprovalRequest", "Approver"));
        var batch = Batch(
            [E("dept:finance", "Department", "财务部"), E("dept:it", "Department", "信息技术部"), E("appr:alice", "Approver", "Alice"), E("appr:bob", "Approver", "Bob"), E("req:1001", "ApprovalRequest", "采购申请 #1001"), E("req:1002", "ApprovalRequest", "采购申请 #1002"), E("req:1003", "ApprovalRequest", "采购申请 #1003")],
            [P("req:1001", "status", "draft"), P("req:1001", "effective_status", "draft"), P("req:1001", "amount", 25000), P("req:1001", "risk_score", 30), P("req:1001", "requires_review", false), P("req:1002", "status", "submitted"), P("req:1002", "effective_status", "submitted"), P("req:1002", "amount", 92000), P("req:1002", "risk_score", 86), P("req:1002", "requires_review", true), P("req:1003", "status", "approved"), P("req:1003", "effective_status", "approved"), P("req:1003", "amount", 12000), P("req:1003", "risk_score", 10), P("req:1003", "requires_review", false)],
            [L("req:1002", "belongs_to_dept", "dept:finance"), L("req:1002", "assigned_to", "appr:bob"), L("req:1003", "belongs_to_dept", "dept:it"), L("req:1003", "assigned_to", "appr:alice")]);
        return Demo("approval-flow", "审批流", types, attrs, rels, batch,
            Action("approveLowRisk", "执行 approve（低风险）", "approve"),
            Action("validateFinanceDept", "校验财务部跨实体约束", "validate"),
            Action("timelineAfterApprove", "时间轴：submit + approve", "timeline"),
            TablePlan("requestsWithComputed", "审批申请（含派生属性）", "ApprovalRequest", "amount", null),
            Action("submitDraft", "执行 submit（含跨实体约束）", "submit"));
    }

    private static DemoDefinition BuildOrgTimeline()
    {
        var types = Types("Department", "Employee", "Team");
        var attrs = Attrs(A("Department", "cost_center", "String"), A("Employee", "title", "String"), A("Team", "focus", "String"));
        var rels = Rels(R("belongs_to", "Employee", "Department"), R("manages", "Employee", "Team"));
        var batch = Batch(
            [E("dept:eng", "Department", "Engineering"), E("dept:product", "Department", "Product"), E("dept:ops", "Department", "Operations"), E("emp:alice", "Employee", "Alice"), E("emp:bob", "Employee", "Bob"), E("emp:carol", "Employee", "Carol"), E("team:platform", "Team", "Platform Team"), E("team:growth", "Team", "Growth Team")],
            [P("dept:eng", "cost_center", "ENG"), P("dept:product", "cost_center", "PRD"), P("emp:alice", "title", "Engineering Manager"), P("emp:bob", "title", "Engineer"), P("emp:carol", "title", "Product Manager"), P("team:platform", "focus", "platform"), P("team:growth", "focus", "growth")],
            [L("emp:alice", "belongs_to", "dept:eng"), L("emp:bob", "belongs_to", "dept:eng"), L("emp:carol", "belongs_to", "dept:product"), L("emp:alice", "manages", "team:platform"), L("emp:carol", "manages", "team:growth")]);
        return Demo("org-timeline", "组织时间轴", types, attrs, rels, batch,
            Temporal("snapshot_2024_06", "快照：2024-06-01", "2024-06-01T00:00:00Z"),
            Temporal("snapshot_2024_12", "快照：2024-12-01", "2024-12-01T00:00:00Z"),
            Temporal("dept_headcount_2024_12", "部门人数：2024-12-01", "2024-12-01T00:00:00Z", "headcount"),
            Temporal("alice_timeline", "时间轴：Alice 调动", "2024-12-01T00:00:00Z", "timeline"));
    }

    private static DemoDefinition Demo(string id, string label, IReadOnlyList<DemoTypeDef> types, IReadOnlyList<DemoAttributeDef> attrs, IReadOnlyList<DemoRelationDef> rels, IReadOnlyList<DemoTable> batch, params (DemoQuery Query, DemoQueryPlan Plan)[] queries)
    {
        var seed = new DemoSeed(types, attrs, rels, batch);
        var tables = SchemaTables(types, attrs, rels).Concat(batch).ToArray();
        return new DemoDefinition(id, label, tables, queries.Select(q => q.Query).ToArray(), seed, queries.ToDictionary(q => q.Query.Id, q => q.Plan, StringComparer.Ordinal));
    }

    private static (DemoQuery Query, DemoQueryPlan Plan) TablePlan(string id, string label, string typeName, string attrName, double? min, string? excludeAttr = null, string? excludeString = null) =>
        (new DemoQuery(id, label, "Table query", label, $"table {typeName}.{attrName}", "table"), new DemoQueryPlan("table", TypeName: typeName, AttrName: attrName, MinNumber: min, ExcludeString: excludeString));

    private static (DemoQuery Query, DemoQueryPlan Plan) Impact(string rootId, IReadOnlyList<string> relNames, string direction) =>
        (new DemoQuery("impactAnalysis", "影响分析", "Graph impact analysis", "Graph impact analysis", "await om.impactAnalysis(...)", "graph"), new DemoQueryPlan("impact", RootId: rootId, RelNames: relNames, Direction: direction));

    private static (DemoQuery Query, DemoQueryPlan Plan) Tree(string rootId, IReadOnlyList<string> relNames) =>
        (new DemoQuery("ownershipTree", "所有权树", "Tree ownership view", "Tree ownership view", "await om.ownershipTree(...)", "tree"), new DemoQueryPlan("tree", RootId: rootId, RelNames: relNames));

    private static (DemoQuery Query, DemoQueryPlan Plan) Risk(string typeName, string attrName, double degreeWeight) =>
        (new DemoQuery("riskHotspot", "风险热点", "Risk ranking", "Risk ranking", "await om.riskHotspot(...)", "table"), new DemoQueryPlan("risk", TypeName: typeName, AttrName: attrName, DegreeWeight: degreeWeight));

    private static (DemoQuery Query, DemoQueryPlan Plan) Action(string id, string label, string action) =>
        (new DemoQuery(id, label, "Action demo", label, $"action {action}", "table"), new DemoQueryPlan("action", Action: action));

    private static (DemoQuery Query, DemoQueryPlan Plan) Temporal(string id, string label, string asOf, string action = "snapshot") =>
        (new DemoQuery(id, label, "Temporal demo", label, $"as-of {asOf}", "table"), new DemoQueryPlan("temporal", Action: action, AsOf: asOf));

    private static IReadOnlyList<DemoTable> SchemaTables(IReadOnlyList<DemoTypeDef> types, IReadOnlyList<DemoAttributeDef> attrs, IReadOnlyList<DemoRelationDef> rels) =>
    [
        Table("类型定义", ["typeName", "parent_type", "mixins", "description"], types.Select(t => Row(("typeName", t.TypeName), ("parent_type", t.ParentType ?? ""), ("mixins", ""), ("description", t.Description))).ToArray()),
        Table("属性定义", ["typeName", "attrName", "valueType", "required", "description"], attrs.Select(a => Row(("typeName", a.TypeName), ("attrName", a.AttrName), ("valueType", a.ValueType), ("required", a.Required), ("description", a.Description))).ToArray()),
        Table("关系定义", ["relName", "fromType", "toType", "directed", "description"], rels.Select(r => Row(("relName", r.RelName), ("fromType", r.FromType), ("toType", r.ToType), ("directed", r.Directed), ("description", r.Description))).ToArray())
    ];

    private static IReadOnlyList<DemoTable> Batch(IReadOnlyList<(string Id, string TypeName, string Label)> entities, IReadOnlyList<(string EntityId, string AttrName, object? Value)> properties, IReadOnlyList<(string FromId, string RelName, string ToId, object? Props)> edges) =>
    [
        Table("entities", ["id", "typeName", "label"], entities.Select(e => Row(("id", e.Id), ("typeName", e.TypeName), ("label", e.Label))).ToArray()),
        Table("properties", ["entityId", "attrName", "value"], properties.Select(p => Row(("entityId", p.EntityId), ("attrName", p.AttrName), ("value", p.Value))).ToArray()),
        Table("edges", ["fromId", "relName", "toId", "props"], edges.Select(e => Row(("fromId", e.FromId), ("relName", e.RelName), ("toId", e.ToId), ("props", e.Props))).ToArray())
    ];

    private static IReadOnlyList<DemoTypeDef> Types(params string[] typeNames) => typeNames.Select(t => new DemoTypeDef(t)).ToArray();
    private static IReadOnlyList<DemoAttributeDef> Attrs(params DemoAttributeDef[] attrs) => attrs;
    private static IReadOnlyList<DemoRelationDef> Rels(params DemoRelationDef[] rels) => rels;
    private static DemoAttributeDef A(string typeName, string attrName, string valueType) => new(typeName, attrName, valueType);
    private static DemoRelationDef R(string relName, string fromType, string toType) => new(relName, fromType, toType);
    private static (string Id, string TypeName, string Label) E(string id, string typeName, string label) => (id, typeName, label);
    private static (string EntityId, string AttrName, object? Value) P(string entityId, string attrName, object? value) => (entityId, attrName, value);
    private static (string FromId, string RelName, string ToId, object? Props) L(string fromId, string relName, string toId, object? props = null) => (fromId, relName, toId, props ?? new Dictionary<string, object?>());

    private static DemoTable Table(string name, IReadOnlyList<string> columns, IReadOnlyList<Dictionary<string, JsonElement>> rows) => new(name, columns, rows);

    private static Dictionary<string, JsonElement> Row(params (string Key, object? Value)[] values)
    {
        return values.ToDictionary(item => item.Key, item => JsonSerializer.SerializeToElement(item.Value, JsonOptions), StringComparer.Ordinal);
    }
}

using System.Text.Json;
using Cozo.DotNet;
using Cozo.DotNet.Datalog;
using Cozo.DotNet.Datalog.Cozo;
using Cozo.DotNet.Om.CodeKnowledge;
using Cozo.DotNet.Om.Depa;
using Cozo.DotNet.Om.Analytics;
using Cozo.DotNet.Om;
using Cozo.DotNet.Om.Query;
using Cozo.DotNet.Om.Batch;
using Cozo.DotNet.Om.Contracts.Models;
using Cozo.DotNet.Om.Inputs;
using Cozo.DotNet.Om.Runtime;

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static string? AsString(JsonElement? value)
{
    return value?.ValueKind == JsonValueKind.String ? value.Value.GetString() : null;
}

static double? AsNumber(JsonElement? value)
{
    return value?.ValueKind == JsonValueKind.Number && value.Value.TryGetDouble(out var number) ? number : null;
}

static bool? AsBool(JsonElement? value)
{
    return value?.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.Value.GetBoolean() : null;
}

static JsonElement Rows(JsonDocument document)
{
    return document.RootElement.GetProperty("rows");
}

static async Task ExpectCozoExceptionAsync(Func<Task> action, string messagePart, string assertMessage)
{
    try
    {
        await action();
    }
    catch (CozoException ex) when (ex.Message.Contains(messagePart, StringComparison.OrdinalIgnoreCase))
    {
        return;
    }

    throw new InvalidOperationException(assertMessage);
}

var datalogSource = """
calls("a", "b").
calls("b", "c").
reachable(X, Y) :- calls(X, Y).
reachable(X, Z) :- reachable(X, Y), calls(Y, Z).
?- reachable($start, Target), Target != "blocked".
""";

var parse = PortableDatalogParser.Parse(datalogSource);
Assert(parse.Success && parse.Program is not null, "Portable Datalog parser should parse facts, rules, recursive rules, parameters, and comparisons");
var parsedProgram = parse.Program!;
Assert(parsedProgram.Rules.Count == 4 && parsedProgram.Query is not null, "Portable Datalog parser should preserve rules and query");

var validation = new PortableDatalogValidator().Validate(parsedProgram);
Assert(validation.Success && validation.Program is not null, "Portable Datalog validator should accept safe recursive rules");
var validatedProgram = validation.Program!;
Assert(validatedProgram.Query!.Projection.Contains("Target"), "Portable Datalog normalized query should project variables from query atoms");

var unsafeParse = PortableDatalogParser.Parse("bad(X) :- calls(Y, Z). ?- bad(X).");
Assert(unsafeParse.Success && unsafeParse.Program is not null, "unsafe program should still parse");
var unsafeValidation = new PortableDatalogValidator().Validate(unsafeParse.Program!);
Assert(!unsafeValidation.Success && unsafeValidation.Diagnostics.Any(d => d.Code == "DL3001"), "validator should reject unsafe head variables");
var malformedParse = PortableDatalogParser.Parse("? calls(X).");
Assert(!malformedParse.Success && malformedParse.Diagnostics.Any(d => d.Code == "DL1001"), "parser should report malformed query markers without hanging");

var compilerSource = """
reachable(X, Y) :- calls(X, Y).
reachable(X, Z) :- reachable(X, Y), calls(Y, Z).
?- reachable($start, Target), Target != "blocked".
""";
var compilerValidation = new PortableDatalogValidator().ParseAndValidate(compilerSource);
Assert(compilerValidation.Success && compilerValidation.Program is not null, "compiler input should validate");
var compilerProgram = compilerValidation.Program!;
var compiler = new CozoDatalogCompiler();
var compileResult = compiler.Compile(
    compilerProgram,
    new CozoDatalogCompileOptions(
        StoredRelations: new Dictionary<string, CozoStoredRelationMapping>
        {
            ["calls"] = new("calls", "code_calls", ["from", "to"])
        },
        Parameters: new Dictionary<string, object?> { ["start"] = "a\"quoted\nvalue" }));
Assert(compileResult.Success, "Cozo compiler should compile supported Portable Datalog IR");
Assert(compileResult.Script.Contains("*code_calls{from: X, to: Y}", StringComparison.Ordinal), "Cozo compiler should map stored relations by named fields");
Assert(compileResult.Script.Contains("reachable[X, Z] := reachable[X, Y], *code_calls{from: Y, to: Z}", StringComparison.Ordinal), "Cozo compiler should compile recursive rules");
Assert(compileResult.Script.Contains("?[Target] := reachable[$start, Target], Target != \"blocked\"", StringComparison.Ordinal), "Cozo compiler should compile parameterized query without inlining parameter values");
Assert((string)compileResult.Parameters["start"]! == "a\"quoted\nvalue", "Cozo compiler should preserve parameter dictionary");
var unsupportedCompile = compiler.Compile(
    compilerProgram,
    new CozoDatalogCompileOptions(
        StoredRelations: new Dictionary<string, CozoStoredRelationMapping>
        {
            ["calls"] = new("calls", "code_calls", ["from"])
        }));
Assert(!unsupportedCompile.Success && unsupportedCompile.Script.Length == 0, "Cozo compiler should not emit partial executable script when backend diagnostics contain errors");

using var db = new CozoDb(engine: "mem", path: "");
var om = new CozoOm(db);

await om.InitSchemaAsync();
await om.InitSchemaAsync();

await om.DefineTypeAsync("Person", "Person");
await om.DefineTypeAsync("Employee", "Employee", parentType: "Person");
await om.DefineTypeAsync("Department", "Department");
await om.DefineTypeAsync("UndirectedSource", "Undirected source");
await om.DefineTypeAsync("UndirectedTarget", "Undirected target");
await om.DefineAttributeAsync("Person", "name", OmValueType.String, required: true);
await om.DefineAttributeAsync("Person", "age", OmValueType.Number);
await om.DefineRelationAsync("works_in", "Person", "Department");
await om.DefineRelationAsync("paired_with", "UndirectedSource", "UndirectedTarget", directed: false);
await om.DefineAttributeAliasAsync("Person", "full_name", "name");
await om.WriteSchemaSnapshotAsync(2, "base test schema");
await om.DefineTypeAsync("TransientType", "Should be removed by rollback");
await om.RollbackSchemaAsync(2, strict: true);
var migration = await om.ApplySchemaMigrationAsync(new SchemaMigrationSpec(
    "mig:add-project",
    FromVersion: 2,
    ToVersion: 3,
    Label: "project schema",
    Steps:
    [
        JsonSerializer.SerializeToElement(new { kind = "addType", typeName = "Project", description = "Project" }),
        JsonSerializer.SerializeToElement(new { kind = "addAttribute", typeName = "Project", attrName = "code", valueType = "String", required = true }),
        JsonSerializer.SerializeToElement(new { kind = "addRelation", relName = "assigned_project", fromType = "Person", toType = "Project" })
    ]));
Assert(migration is { FromVersion: 2, ToVersion: 3, StepsApplied: 3 }, "schema migration should report applied steps");
Assert((await om.GetSchemaStateAsync()).CurrentVersion == 3, "schema migration should advance current version");
await om.CreateEntityAsync("proj1", "Project", "Project 1");
await om.SetPropertyAsync("proj1", "code", "P-001");

await ExpectCozoExceptionAsync(
    () => om.CreateEntityAsync("missing:create", "MissingType", "Missing"),
    "Type 'MissingType' does not exist",
    "createEntity should reject unknown type names");
await ExpectCozoExceptionAsync(
    () => om.UpsertEntityAsync("missing:upsert", "MissingType", "Missing"),
    "Type 'MissingType' does not exist",
    "upsertEntity should reject unknown type names");

await ExpectCozoExceptionAsync(
    () => om.DefineTypeAsync("Orphan", "Orphan", parentType: "NonExistent"),
    "Parent type 'NonExistent' does not exist",
    "defineType should reject missing parent_type");
await om.DefineTypeAsync("CycleA", "Cycle A");
await om.DefineTypeAsync("CycleB", "Cycle B", parentType: "CycleA");
await ExpectCozoExceptionAsync(
    () => om.DefineTypeAsync("CycleA", "Cycle A", parentType: "CycleB"),
    "Circular inheritance detected",
    "defineType should reject circular inheritance");
await om.DefineTypeAsync("SelfCycle", "Self cycle");
await ExpectCozoExceptionAsync(
    () => om.DefineTypeAsync("SelfCycle", "Self cycle", parentType: "SelfCycle"),
    "Circular inheritance detected",
    "defineType should reject self-referencing parent");

var employeeAncestors = await om.GetAncestorsAsync("Employee");
Assert(employeeAncestors.SequenceEqual(["Person"]), "getAncestors should return nearest-to-farthest parent chain");
var personDescendants = await om.GetDescendantsAsync("Person");
Assert(personDescendants.Contains("Employee"), "getDescendants should include child types");
Assert(await om.IsSubtypeOfAsync("Employee", "Person"), "isSubtypeOf should accept inherited type relation");
var hierarchy = await om.GetTypeHierarchyAsync();
Assert(hierarchy.Roots.Contains("Person") && hierarchy.Types["Employee"].ParentType == "Person", "getTypeHierarchy should expose roots and parent links");

await om.DefineTypeAliasAsync("Staff", "Person");
await om.DefineRelationAliasAsync("member_of", "works_in");
Assert(await om.ResolveTypeAsync("Staff") == "Person", "resolveType should canonicalize type aliases");
Assert(await om.ResolveRelationAsync("member_of") == "works_in", "resolveRelation should canonicalize relation aliases");
Assert(await om.ResolveAttributeAsync("Person", "full_name") == "name", "resolveAttribute should canonicalize attribute aliases");
await om.CreateEntityAsync("alias-rel-person", "Employee", "Alias Relation Person");
await om.CreateEntityAsync("alias-rel-dept", "Department", "Alias Relation Department");
await om.LinkEntitiesAsync("alias-rel-person", "member_of", "alias-rel-dept");
Assert((await om.GetNeighborsAsync("alias-rel-person", "works_in", OmDirection.Outgoing)).Outgoing.Any(n => n.EntityId == "alias-rel-dept"), "relation alias should work for link writes");

await om.DefineTypeAsync("AliasCycleEntity", "Alias cycle entity");
await om.DefineAttributeAliasAsync("AliasCycleEntity", "a", "b");
await om.DefineAttributeAliasAsync("AliasCycleEntity", "b", "a");
await ExpectCozoExceptionAsync(
    () => om.ResolveAttributeAsync("AliasCycleEntity", "a"),
    "cycle",
    "resolveAttribute should reject alias cycles");

await om.DefineTypeAsync("AliasCanonicalType", "Alias canonical type");
await om.DefineAttributeAsync("AliasCanonicalType", "org_unit", OmValueType.String);
await om.DefineTypeAliasAsync("AliasStoredType", "AliasCanonicalType");
db.Run(
    """
    ?[id, type_name, label] <- [[$id, $type_name, $label]]
    :insert om_entity {id => type_name, label}
    """,
    new { id = "alias:type-view", type_name = "AliasStoredType", label = "Alias Type View" });
await om.SetPropertyAsync("alias:type-view", "org_unit", "PeopleOps");
var aliasTypeView = await om.GetEntityViewAsync("alias:type-view");
Assert(aliasTypeView is not null && aliasTypeView.TypeName == "AliasCanonicalType", "getEntityView should return canonical typeName for alias-stored entities");

await om.DefineMixinAsync("Auditable", "Auditable mixin");
await om.DefineAttributeAsync("Auditable", "created_by", OmValueType.String);
await om.DefineTypeAsync("AuditedAsset", "Audited asset", mixins: ["Auditable"]);
var auditedDefs = await om.GetAttributeDefinitionsAsync("AuditedAsset");
Assert(auditedDefs.TryGetValue("created_by", out var createdByDef) && createdByDef.ValueType == OmValueType.String, "mixin attribute should be part of effective definitions");
await om.DefineTypeAsync("AuditedAsset", "Audited asset updated");
var auditedDefsAfterRedefine = await om.GetAttributeDefinitionsAsync("AuditedAsset");
Assert(auditedDefsAfterRedefine.ContainsKey("created_by"), "redefining a type without options should preserve mixins");
await om.CreateEntityAsync("audited:1", "AuditedAsset", "Audited 1");
await om.SetPropertyAsync("audited:1", "created_by", "admin");
Assert(AsString(await om.GetPropertyAsync("audited:1", "created_by")) == "admin", "mixin-contributed attribute should be writable");

await om.DefineTypeAsync("DescBase", "Description base");
await om.DefineAttributeAsync("DescBase", "status", OmValueType.String, description: "Base status");
await om.DefineTypeAsync("DescChild", "Description child", parentType: "DescBase");
await om.DefineAttributeAsync("DescChild", "status", OmValueType.String, description: "Child status");
await om.DefineTypeAsync("DescGrandchild", "Description grandchild", parentType: "DescChild");
Assert((await om.GetAttributeDefinitionsAsync("DescBase"))["status"].Description == "Base status", "attribute descriptions should be stored");
Assert((await om.GetAttributeDefinitionsAsync("DescGrandchild"))["status"].Description == "Child status", "nearest inherited attribute description should win");

await om.DefineTypeAsync("OverrideBase", "Override base");
await om.DefineAttributeAsync("OverrideBase", "name", OmValueType.String, required: true);
await om.DefineAttributeAsync("OverrideBase", "location", OmValueType.String);
await om.DefineTypeAsync("OverrideChild", "Override child", parentType: "OverrideBase");
await om.DefineAttributeAsync("OverrideChild", "location", OmValueType.String, required: true);
var overrideDefs = await om.GetAttributeDefinitionsAsync("OverrideChild");
Assert(overrideDefs["location"].Required, "child should be able to tighten optional inherited attr to required");
var loosenRejected = false;
try
{
    await om.DefineAttributeAsync("OverrideChild", "name", OmValueType.String, required: false);
}
catch (CozoException ex) when (ex.Message.Contains("Cannot loosen required", StringComparison.Ordinal))
{
    loosenRejected = true;
}

Assert(loosenRejected, "child should not loosen inherited required attribute");
var typeChangeRejected = false;
try
{
    await om.DefineAttributeAsync("OverrideChild", "name", OmValueType.Number, required: true);
}
catch (CozoException ex) when (ex.Message.Contains("Cannot change value_type", StringComparison.Ordinal))
{
    typeChangeRejected = true;
}

Assert(typeChangeRejected, "child should not change inherited attribute value type");

await om.DefineTypeAsync("PreserveParent", "Preserve parent");
await om.DefineTypeAsync("PreserveChild", "Preserve child", parentType: "PreserveParent");
await om.DefineTypeAsync("PreserveChild", "Preserve child updated");
Assert(await om.IsSubtypeOfAsync("PreserveChild", "PreserveParent"), "redefining an existing type without parent should preserve parent_type");

await om.DefineTypeAsync("AggAsset", "Aggregate asset");
await om.DefineTypeAsync("AggServer", "Aggregate server", parentType: "AggAsset");
await om.DefineAttributeAsync("AggAsset", "value", OmValueType.Number);
await om.CreateEntityAsync("agg:asset", "AggAsset", "Agg Asset");
await om.SetPropertyAsync("agg:asset", "value", 10);
await om.CreateEntityAsync("agg:server", "AggServer", "Agg Server");
await om.SetPropertyAsync("agg:server", "value", 20);
Assert(await om.AggregateByTypeAsync("AggAsset", "value", "sum") == 30, "aggregateByType should include descendants by default");
Assert(await om.AggregateByTypeAsync("AggAsset", "value", "avg") == 15, "aggregateByType should support avg");
Assert(await om.AggregateByTypeAsync("AggAsset", "value", "min") == 10, "aggregateByType should support min");
Assert(await om.AggregateByTypeAsync("AggAsset", "value", "max") == 20, "aggregateByType should support max");
Assert(await om.AggregateByTypeAsync("AggAsset", "value", "count") == 2, "aggregateByType should support count");
Assert(await om.AggregateByTypeAsync("AggAsset", "value", "sum", new FindByTypeOptions(Exact: true)) == 10, "aggregateByType exact mode should only sum exact type rows");
Assert(await om.AggregateByTypeAsync("AggAsset", "value", "count", new FindByTypeOptions(Exact: true)) == 1, "aggregateByType exact mode should only count exact type rows");

await om.DefineTypeAsync("AliasFallbackEntity", "Alias fallback entity");
await om.DefineAttributeAsync("AliasFallbackEntity", "canonical_name", OmValueType.String, required: true);
await om.DefineAttributeAliasAsync("AliasFallbackEntity", "legacy_name", "canonical_name");
await om.CreateEntityAsync("alias:fallback", "AliasFallbackEntity", "Alias fallback");
db.Run(
    """
    ?[entity_id, attr_name, valid_time, value, tx_time] <- [[$entity_id, $attr_name, "2000-01-01T00:00:00Z", $value, "2000-01-01T00:00:00Z"]]
    :put om_property {entity_id, attr_name, valid_time => value, tx_time}
    """,
    new { entity_id = "alias:fallback", attr_name = "legacy_name", value = "Legacy Value" });
Assert(AsString(await om.GetPropertyAsync("alias:fallback", "canonical_name")) == "Legacy Value", "canonical attr read should fall back to legacy alias-stored row");
await om.SetPropertyAsync("alias:fallback", "canonical_name", "Canonical Value");
Assert(AsString(await om.GetPropertyAsync("alias:fallback", "canonical_name")) == "Canonical Value", "canonical attr row should win when both canonical and alias rows exist");
Assert((await om.ValidateEntityAsync("alias:fallback")).Valid, "required validation should canonicalize alias-stored properties");

await om.DefineTypeAsync("ValidityHolder", "Validity holder");
await om.DefineAttributeAsync("ValidityHolder", "valid_from", OmValueType.Validity);
await om.CreateEntityAsync("validity:1", "ValidityHolder", "Validity 1");
var validityIso = "2026-01-01T00:00:00Z";
await om.SetPropertyAsync("validity:1", "valid_from", validityIso);
using var validityRows = db.Run(
    """
    ?[ts] :=
      *om_property{ entity_id: $entity_id, attr_name: $attr_name, value: v },
      ts = to_int(v)
    """,
    new { entity_id = "validity:1", attr_name = "valid_from" });
Assert(Rows(validityRows)[0][0].GetInt64() == DateTimeOffset.Parse(validityIso).ToUnixTimeMilliseconds() * 1000, "Validity attribute should be stored as Cozo validity value");

await om.CreateEntityAsync("p1", "Employee", "Alice");
await om.CreateEntityAsync("d1", "Department", "Engineering");
await om.CreateEntityAsync("undir:source", "UndirectedSource", "Undirected Source");
await om.CreateEntityAsync("undir:target", "UndirectedTarget", "Undirected Target");
await om.LinkEntitiesAsync("undir:target", "paired_with", "undir:source");
Assert((await om.GetNeighborsAsync("undir:target", "paired_with", OmDirection.Outgoing)).Outgoing.Count == 1, "undirected relation should allow reverse endpoint typing");
var invalidBeforeName = await om.ValidateEntityAsync("p1");
Assert(!invalidBeforeName.Valid && invalidBeforeName.Errors.Any(e => e.Contains("name", StringComparison.Ordinal)), "required property should be enforced");

await om.SetPropertyAsync("p1", "full_name", "Alice");
await om.SetPropertyAsync("p1", "age", 42);
Assert(AsString(await om.GetPropertyAsync("p1", "name")) == "Alice", "property read should return current value");
await om.SetPropertyAsync("p1", "name", "Alice 2024", new WriteOptions(ValidTime: "2024-01-01T00:00:00Z"));
await om.SetPropertyAsync("p1", "name", "Alice 2025", new WriteOptions(ValidTime: "2025-01-01T00:00:00Z"));
Assert(AsString(await om.GetPropertyAsOfAsync("p1", "name", "2024-06-01T00:00:00Z")) == "Alice 2024", "property asOf should read historical value");

await om.LinkEntitiesAsync("p1", "works_in", "d1");
var neighbors = await om.GetNeighborsAsync("p1", "works_in", OmDirection.Outgoing);
Assert(neighbors.Outgoing.Count == 1 && neighbors.Outgoing[0].EntityId == "d1", "outgoing relation should be visible");
await om.CreateEntityAsync("p_temporal", "Person", "Temporal");
await om.SetPropertyAsync("p_temporal", "name", "Temporal");
await om.CreateEntityAsync("d_temporal", "Department", "Temporal Dept");
await om.LinkEntitiesAsync("p_temporal", "works_in", "d_temporal", options: new WriteOptions(ValidTime: "2024-01-01T00:00:00Z"));
Assert((await om.GetNeighborsAsOfAsync("p_temporal", "works_in", "2024-06-01T00:00:00Z", OmDirection.Outgoing)).Outgoing.Count == 1, "edge asOf should see asserted edge");
await om.UnlinkEntitiesAsync("p_temporal", "works_in", "d_temporal", new WriteOptions(ValidTime: "2025-01-01T00:00:00Z"));
Assert((await om.GetNeighborsAsOfAsync("p_temporal", "works_in", "2025-06-01T00:00:00Z", OmDirection.Outgoing)).Outgoing.Count == 0, "edge asOf should honor retract");

await om.DefineTypeAsync("ViewEmployee", "View employee");
await om.DefineTypeAsync("ViewDepartment", "View department");
await om.DefineAttributeAsync("ViewEmployee", "department", OmValueType.String);
await om.DefineAttributeAsync("ViewEmployee", "base_score", OmValueType.Number);
await om.DefineRelationAsync("view_works_in", "ViewEmployee", "ViewDepartment");
await om.CreateEntityAsync("view:emp", "ViewEmployee", "View Emp");
await om.CreateEntityAsync("view:dept", "ViewDepartment", "View Dept");
await om.SetPropertyAsync("view:emp", "department", "Engineering", new WriteOptions(ValidTime: "2024-01-01T00:00:00Z"));
await om.SetPropertyAsync("view:emp", "department", "Product", new WriteOptions(ValidTime: "2025-01-01T00:00:00Z"));
await om.SetPropertyAsync("view:emp", "base_score", 7, new WriteOptions(ValidTime: "2024-01-01T00:00:00Z"));
await om.LinkEntitiesAsync("view:emp", "view_works_in", "view:dept", options: new WriteOptions(ValidTime: "2024-01-01T00:00:00Z"));
await om.UnlinkEntitiesAsync("view:emp", "view_works_in", "view:dept", new WriteOptions(ValidTime: "2025-01-01T00:00:00Z"));
await om.DefineComputedAsync("ViewEmployee", "dept_code");
om.RegisterComputed("ViewEmployee", "dept_code", async ctx =>
{
    var department = AsString(await ctx.GetPropertyAsync("department"));
    return department is null ? null : department[..Math.Min(3, department.Length)].ToUpperInvariant();
});
var viewAsOf = await om.GetEntityViewAsOfAsync("view:emp", "2024-06-01T00:00:00Z");
Assert(viewAsOf is not null
       && AsString(viewAsOf.Properties["department"]) == "Engineering"
       && AsString(viewAsOf.Properties["dept_code"]) == "ENG"
       && viewAsOf.Outgoing.Any(edge => edge.ToId == "view:dept"),
    "GetEntityViewAsOfAsync should include historical properties, computed values, and outgoing edges");
var viewNow = await om.GetEntityViewAsync("view:emp");
Assert(viewNow is not null
       && AsString(viewNow.Properties["department"]) == "Product"
       && viewNow.Outgoing.Count == 0,
    "GetEntityViewAsync should keep NOW semantics after adding asOf view");

await om.DefineTypeAsync("SearchAsset", "Search asset");
await om.DefineTypeAsync("SearchServer", "Search server", parentType: "SearchAsset");
await om.DefineAttributeAsync("SearchAsset", "status", OmValueType.String);
await om.DefineAttributeAsync("SearchAsset", "tier", OmValueType.String);
await om.CreateEntityAsync("search:asset", "SearchAsset", "Search Asset");
await om.CreateEntityAsync("search:server", "SearchServer", "Search Server");
await om.SetPropertyAsync("search:asset", "status", "active");
await om.SetPropertyAsync("search:asset", "tier", "gold");
await om.SetPropertyAsync("search:server", "status", "active");
await om.SetPropertyAsync("search:server", "tier", "silver");
var richSearch = await om.FindByTypeWithPropertiesAsync("SearchAsset", new Dictionary<string, object?> { ["status"] = "active" });
Assert(richSearch.Count == 2
       && richSearch.Any(entry => entry.Id == "search:server" && AsString(entry.Properties["tier"]) == "silver"),
    "FindByTypeWithPropertiesAsync should filter by property and return canonical properties");
var exactRichSearch = await om.FindByTypeWithPropertiesAsync("SearchAsset", new Dictionary<string, object?> { ["status"] = "active" }, new FindByTypeOptions(Exact: true));
Assert(exactRichSearch.Count == 1 && exactRichSearch[0].Id == "search:asset", "rich type search exact mode should exclude descendants");

await om.DefineTypeAsync("ValidationPerson", "Validation person");
await om.DefineTypeAsync("ValidationDepartment", "Validation department");
await om.DefineAttributeAsync("ValidationPerson", "name", OmValueType.String, required: true);
await om.DefineRelationAsync("validation_member_of", "ValidationPerson", "ValidationDepartment");
await om.CreateEntityAsync("validation:person", "ValidationPerson", "Validation Person");
await om.CreateEntityAsync("validation:dept", "ValidationDepartment", "Validation Dept");
var missingRequired = await om.ValidateRequiredPropertiesAsync("validation:person");
Assert(missingRequired.SequenceEqual(["name"]), "ValidateRequiredPropertiesAsync should return missing canonical attribute names");
await ExpectCozoExceptionAsync(
    () => om.ValidatePropertyTypeAsync("validation:person", "name", 123),
    "expects String",
    "ValidatePropertyTypeAsync should reject mismatched value type without writing");
await ExpectCozoExceptionAsync(
    () => om.ValidateRelationAsync("validation:dept", "validation_member_of", "validation:person"),
    "cannot link",
    "ValidateRelationAsync should reject invalid endpoint typing without writing");
await ExpectCozoExceptionAsync(
    () => om.FinalizeEntityAsync("validation:person"),
    "Missing required property",
    "FinalizeEntityAsync should fail incomplete entities");
await om.SetPropertyAsync("validation:person", "name", "Valid");
await om.FinalizeEntityAsync("validation:person");

await om.DefineTypeAsync("ComputedBase", "Computed base");
await om.DefineTypeAsync("ComputedChild", "Computed child", parentType: "ComputedBase");
await om.DefineComputedAsync("ComputedBase", "risk_score");
om.RegisterComputed("ComputedBase", "risk_score", _ => ValueTask.FromResult<object?>(1));
await om.CreateEntityAsync("computed:child", "ComputedChild", "Computed Child");
Assert(AsNumber(await om.GetPropertyAsync("computed:child", "risk_score")) == 1, "child should inherit parent computed property");
Assert((await om.GetEntityViewAsync("computed:child"))!.Properties.ContainsKey("risk_score"), "entity view should include inherited computed property");
await om.DefineComputedAsync("ComputedChild", "risk_score");
om.RegisterComputed("ComputedChild", "risk_score", _ => ValueTask.FromResult<object?>(2));
Assert(AsNumber(await om.GetPropertyAsync("computed:child", "risk_score")) == 2, "child computed property should override parent computed property");

await om.DefineTypeAsync("ConstraintEmployee", "Constraint employee");
await om.DefineAttributeAsync("ConstraintEmployee", "status", OmValueType.String);
await om.DefineAttributeAsync("ConstraintEmployee", "end_date", OmValueType.String);
await om.DefineConstraintAsync(
    "ConstraintEmployee",
    "active_has_no_end_date",
    "conditional",
    async ctx => AsString(await ctx.GetPropertyAsync("status")) == "active",
    async ctx =>
    {
        var endDate = AsString(await ctx.GetPropertyAsync("end_date"));
        return string.IsNullOrWhiteSpace(endDate);
    },
    "end_date must be empty when status=active");
await om.CreateEntityAsync("constraint:emp", "ConstraintEmployee", "Constraint Emp");
await om.SetPropertyAsync("constraint:emp", "status", "active");
await ExpectCozoExceptionAsync(
    () => om.SetPropertyAsync("constraint:emp", "end_date", "2026-12-31"),
    "active_has_no_end_date",
    "conditional constraint should reject invalid property writes");
Assert(await om.GetPropertyAsync("constraint:emp", "end_date") is null, "failed conditional constraint write should rollback property");

await om.DefineTypeAsync("ConstraintDepartment", "Constraint department");
await om.DefineTypeAsync("ConstraintStaff", "Constraint staff");
await om.DefineRelationAsync("constraint_heads", "ConstraintDepartment", "ConstraintStaff");
await om.DefineConstraintAsync(
    "ConstraintDepartment",
    "at_most_one_head",
    "cross-entity",
    _ => ValueTask.FromResult(true),
    async ctx => (await ctx.GetNeighborsAsync("constraint_heads", OmDirection.Outgoing)).Outgoing.Count <= 1,
    "department can have at most one head");
await om.CreateEntityAsync("constraint:dept", "ConstraintDepartment", "Constraint Dept");
await om.CreateEntityAsync("constraint:staff1", "ConstraintStaff", "Constraint Staff 1");
await om.CreateEntityAsync("constraint:staff2", "ConstraintStaff", "Constraint Staff 2");
await om.LinkEntitiesAsync("constraint:dept", "constraint_heads", "constraint:staff1");
await ExpectCozoExceptionAsync(
    () => om.LinkEntitiesAsync("constraint:dept", "constraint_heads", "constraint:staff2"),
    "at_most_one_head",
    "cross-entity constraint should reject invalid edge writes");
Assert((await om.GetNeighborsAsync("constraint:dept", "constraint_heads", OmDirection.Outgoing)).Outgoing.Count == 1, "failed cross-entity constraint write should rollback edge");

await om.DefineTypeAsync("RiskAsset", "Risk asset");
await om.DefineAttributeAsync("RiskAsset", "base_risk", OmValueType.Number);
await om.DefineAttributeAsync("RiskAsset", "requires_review", OmValueType.Bool);
await om.DefineComputedAsync("RiskAsset", "risk_score");
om.RegisterComputed("RiskAsset", "risk_score", async ctx => (AsNumber(await ctx.GetPropertyAsync("base_risk")) ?? 0) * 10);
await om.DefineConstraintAsync(
    "RiskAsset",
    "high_risk_requires_review",
    "computed-dep",
    async ctx => (AsNumber(await ctx.GetPropertyAsync("risk_score")) ?? 0) > 80,
    async ctx => AsBool(await ctx.GetPropertyAsync("requires_review")) == true,
    "requires_review must be true when risk_score > 80");
await om.CreateEntityAsync("risk:asset", "RiskAsset", "Risk Asset");
await om.SetPropertyAsync("risk:asset", "base_risk", 9, new WriteOptions(SkipConstraints: true));
var riskValidation = await om.ValidateEntityAsync("risk:asset");
Assert(!riskValidation.Valid && riskValidation.Errors.Any(error => error.Contains("high_risk_requires_review", StringComparison.Ordinal)), "computed-dep constraint should see computed property values");
await om.SetPropertyAsync("risk:asset", "requires_review", true);
Assert((await om.ValidateEntityAsync("risk:asset")).Valid, "computed-dep constraint should pass after dependent property is set");

await om.DefineTypeAsync("InheritedConstraintBase", "Inherited constraint base");
await om.DefineTypeAsync("InheritedConstraintChild", "Inherited constraint child", parentType: "InheritedConstraintBase");
await om.DefineAttributeAsync("InheritedConstraintBase", "status", OmValueType.String);
await om.DefineAttributeAsync("InheritedConstraintBase", "end_date", OmValueType.String);
await om.DefineConstraintAsync(
    "InheritedConstraintBase",
    "inherited_active_has_no_end_date",
    "conditional",
    async ctx => AsString(await ctx.GetPropertyAsync("status")) == "active",
    async ctx => string.IsNullOrWhiteSpace(AsString(await ctx.GetPropertyAsync("end_date"))),
    "end_date must be empty when inherited status=active");
await om.CreateEntityAsync("constraint:child", "InheritedConstraintChild", "Inherited Constraint Child");
await om.SetPropertyAsync("constraint:child", "status", "active");
await ExpectCozoExceptionAsync(
    () => om.SetPropertyAsync("constraint:child", "end_date", "2026-12-31"),
    "inherited_active_has_no_end_date",
    "subtype should inherit parent conditional constraints");

var people = await om.FindByTypeAsync("Person");
Assert(people.Any(e => e.Id == "p1" && e.TypeName == "Employee"), "parent type query should include child entities");
var transientFailed = false;
try
{
    await om.CreateEntityAsync("transient", "TransientType", "Transient");
}
catch (CozoException)
{
    transientFailed = true;
}

Assert(transientFailed, "schema rollback should remove metadata added after snapshot");

await om.DefineConstraintAsync("Person", "reject_bad", "custom", "bad entity");
om.RegisterValidator("Person", "reject_bad", ctx =>
    ValueTask.FromResult(ctx.EntityId == "p_bad" ? "custom failed" : null));
await om.CreateEntityAsync("p_bad", "Person", "Bad");
await om.SetPropertyAsync("p_bad", "name", "Bad", new WriteOptions(SkipConstraints: true));
var customValidation = await om.ValidateEntityAsync("p_bad");
Assert(!customValidation.Valid && customValidation.Errors.Contains("custom failed"), "custom in-memory validator should run");

var actionLog = new List<string>();
await om.DefineMutationAsync(
    "Person",
    "setNameFromAction",
    async (ctx, parameters) =>
    {
        actionLog.Add("mutation");
        await ctx.SetPropertyAsync("name", parameters["name"]);
    },
    "Set name from action");
await om.DefineActionAsync(
    "Person",
    "rename",
    (ctx, parameters) =>
    {
        actionLog.Add("action");
        return ValueTask.FromResult<IReadOnlyList<MutationSpec>>(
        [
            new MutationSpec("setNameFromAction", new Dictionary<string, object?> { ["name"] = parameters["name"] })
        ]);
    },
    "Rename person");
await om.AddInterceptorAsync(
    "Person",
    "rename",
    "before",
    ctx =>
    {
        actionLog.Add("before");
        if (!ctx.Params.ContainsKey("name"))
        {
            throw new InvalidOperationException("name is required");
        }

        return ValueTask.CompletedTask;
    },
    "Require name");
await om.AddInterceptorAsync(
    "Person",
    "rename",
    "after",
    _ =>
    {
        actionLog.Add("after");
        return ValueTask.CompletedTask;
    },
    "Record after");
await om.ExecuteActionAsync("p1", "rename", new Dictionary<string, object?> { ["name"] = "Alice Action" });
Assert(string.Join(",", actionLog) == "before,action,mutation,after", "action pipeline should run before/action/mutation/after in order");
Assert(AsString(await om.GetPropertyAsync("p1", "name")) == "Alice Action", "action mutation should update property");

var rollbackActionLog = new List<string>();
await om.DefineMutationAsync(
    "Person",
    "setNameBeforeFail",
    async (ctx, parameters) =>
    {
        rollbackActionLog.Add("mutation");
        await ctx.SetPropertyAsync("name", parameters["name"]);
    },
    "Set name before failing after interceptor");
await om.DefineActionAsync(
    "Person",
    "renameThenFail",
    (_, _) =>
    {
        rollbackActionLog.Add("action");
        return ValueTask.FromResult<IReadOnlyList<MutationSpec>>(
        [
            new MutationSpec("setNameBeforeFail", new Dictionary<string, object?> { ["name"] = "Should Roll Back" })
        ]);
    },
    "Rename and fail");
await om.AddInterceptorAsync(
    "Person",
    "renameThenFail",
    "after",
    _ =>
    {
        rollbackActionLog.Add("after");
        throw new InvalidOperationException("after failed");
    },
    "Fail after");
var actionRolledBack = false;
try
{
    await om.ExecuteActionAsync("p1", "renameThenFail");
}
catch (InvalidOperationException ex) when (ex.Message.Contains("after failed", StringComparison.Ordinal))
{
    actionRolledBack = true;
}

Assert(actionRolledBack, "action pipeline should surface after interceptor failure");
Assert(string.Join(",", rollbackActionLog) == "action,mutation,after", "failing action should run until failing after interceptor");
Assert(AsString(await om.GetPropertyAsync("p1", "name")) == "Alice Action", "failing action should rollback mutation writes");

await om.SeedPermissionMetadataAsync();
await om.SeedPermissionMetadataAsync(new PermissionSeedInput(
    Actions: [new PermissionActionSeed("inspect", "Inspect resource")],
    Policies: [new PermissionPolicySeed("allow_admin_inspect_project", "allow", "inspect", "Project")],
    AbacRules: [new PermissionAbacRuleSeed("allow_admin_inspect_project", "subject.id", "=", "literal:admin")],
    PathRules: [new PermissionPathRuleSeed("allow_admin_inspect_project", "subject->project")]));
Assert((await om.CheckAccessAsync(new CheckAccessInput("admin", "inspect", "proj1"))).Allow, "permission seed input should load policies and abac rules");
Assert(!(await om.CheckAccessAsync(new CheckAccessInput("u1", "inspect", "proj1"))).Allow, "permission seed abac mismatch should deny access");
await om.DefinePermissionPolicyAsync("allow_person_read", "allow", "read", "Person");
await om.AddPermissionPathRuleAsync("allow_person_read", "subject->person");
Assert((await om.CheckAccessAsync(new CheckAccessInput("u1", "read", "p1"))).Allow, "allow policy should grant access");
var readExplanation = (await om.CheckAccessAsync(new CheckAccessInput("u1", "read", "p1"))).Explanation.GetRawText();
Assert(readExplanation.Contains("subject", StringComparison.Ordinal) && readExplanation.Contains("person", StringComparison.Ordinal), "permission explanation should include witness path");
await om.DefinePermissionPolicyAsync("allow_person_name_inspect", "allow", "inspect", "Person.name");
await om.AddPermissionAbacRuleAsync("allow_person_name_inspect", "subject.id", "=", "literal:admin");
Assert((await om.CheckAccessAsync(new CheckAccessInput("admin", "inspect", "p1", FieldName: "name"))).Allow, "field policy should grant matching field");
Assert(!(await om.CheckAccessAsync(new CheckAccessInput("u1", "inspect", "p1", FieldName: "name"))).Allow, "ABAC mismatch should deny field access");
await om.DefinePermissionPolicyAsync("deny_person_read", "deny", "read", "Person");
Assert(!(await om.CheckAccessAsync(new CheckAccessInput("u1", "read", "p1"))).Allow, "deny policy should override allow");

await om.DefineTypeAsync("Order", "Order");
await om.DefineTypeAsync("Shipment", "Shipment");
await om.DefineAttributeAsync("Shipment", "carrier", OmValueType.String);
await om.DefineRelationAsync("has_shipment", "Order", "Shipment");
await om.CreateEntityAsync("o1", "Order", "Order 1");
await om.DefineExistentialRuleAsync(
    "order_has_shipment",
    new ExistentialRuleSpec(
        new ExistentialForEachSpec("Order"),
        new ExistentialExistsSpec("has_shipment", ExistentialDirection.Out, "Shipment"),
        new ExistentialMaterializeSpec("shipment:{fromId}", new Dictionary<string, JsonElement>
        {
            ["carrier"] = JsonSerializer.SerializeToElement("pending")
        }),
        ExistentialRuleMode.Materialize,
        "order must have shipment"));

var violations = await om.CheckExistentialRulesAsync();
Assert(violations.Count == 1 && violations[0].EntityId == "o1", "existential check should report missing edge");

var chase = await om.ApplyExistentialRulesAsync(new ApplyExistentialRulesInput(MaxIterations: 3));
Assert(chase.Created.Count == 1 && chase.ReachedFixpoint, "existential materialization should create one Skolem edge and reach fixpoint");
Assert((await om.CheckExistentialRulesAsync()).Count == 0, "existential materialization should be idempotent after apply");
Assert((await om.ApplyExistentialRulesAsync(new ApplyExistentialRulesInput(MaxIterations: 3))).Created.Count == 0, "second existential apply should not duplicate Skolem objects");

var shipmentNeighbors = await om.GetNeighborsAsync("o1", "has_shipment", OmDirection.Outgoing);
Assert(shipmentNeighbors.Outgoing.Count == 1 && shipmentNeighbors.Outgoing[0].TypeName == "Shipment", "Skolem shipment should be linked");

await om.DefineTypeAsync("BatchPerson", "Batch person");
await om.DefineTypeAsync("BatchDepartment", "Batch department");
await om.DefineAttributeAsync("BatchPerson", "name", OmValueType.String, required: true);
await om.DefineAttributeAsync("BatchDepartment", "title", OmValueType.String, required: true);
await om.DefineRelationAsync("batch_works_in", "BatchPerson", "BatchDepartment");

var batchResult = await om.IngestBatchAsync(new OmBatchInput(
    Entities:
    [
        new OmBatchEntity("bp1", "BatchPerson", "Batch Alice"),
        new OmBatchEntity("bd1", "BatchDepartment", "Batch Engineering")
    ],
    Properties:
    [
        new OmBatchProperty("bp1", "name", "Batch Alice"),
        new OmBatchProperty("bd1", "title", "Batch Engineering")
    ],
    Edges:
    [
        new OmBatchEdge("bp1", "batch_works_in", "bd1", new { source = "test" })
    ]));
Assert(batchResult is { Entities: 2, Properties: 2, Edges: 1, ValidatedEntities: 2 }, "batch ingest should return Node-compatible counts");
Assert(AsString(await om.GetPropertyAsync("bp1", "name")) == "Batch Alice", "batch property should be written");
Assert((await om.GetNeighborsAsync("bp1", "batch_works_in", OmDirection.Outgoing)).Outgoing.Count == 1, "batch edge should be written");

var requiredFailed = false;
try
{
    await om.IngestBatchAsync(new OmBatchInput(Entities: [new OmBatchEntity("bp_missing", "BatchPerson", "Missing required")]));
}
catch (CozoException ex) when (ex.Message.Contains("name", StringComparison.Ordinal))
{
    requiredFailed = true;
}

Assert(requiredFailed, "batch ingest should validate touched entities by default");
var batchRolledBack = false;
try
{
    await om.GetEntityTypeAsync("bp_missing");
}
catch (CozoException)
{
    batchRolledBack = true;
}

Assert(batchRolledBack, "failed batch ingest should rollback created entities");

var skippedResult = await om.IngestBatchAsync(
    new OmBatchInput(Entities: [new OmBatchEntity("bp_skip", "BatchPerson", "Skip required")]),
    new OmBatchOptions(ValidateRequired: false));
Assert(skippedResult.ValidatedEntities == 1 && (await om.GetEntityTypeAsync("bp_skip")) == "BatchPerson", "batch ingest can skip required validation while preserving Node-compatible count");

await om.DefineTypeAsync("AnalyticsNode", "Analytics node");
await om.DefineAttributeAsync("AnalyticsNode", "risk", OmValueType.Number);
await om.DefineRelationAsync("analytics_link", "AnalyticsNode", "AnalyticsNode");
await om.IngestBatchAsync(new OmBatchInput(
    Entities:
    [
        new OmBatchEntity("an_root", "AnalyticsNode", "Root"),
        new OmBatchEntity("an_child", "AnalyticsNode", "Child"),
        new OmBatchEntity("an_other", "AnalyticsNode", "Other")
    ],
    Properties:
    [
        new OmBatchProperty("an_root", "risk", 10),
        new OmBatchProperty("an_child", "risk", 50),
        new OmBatchProperty("an_other", "risk", 30)
    ],
    Edges:
    [
        new OmBatchEdge("an_root", "analytics_link", "an_child"),
        new OmBatchEdge("an_root", "analytics_link", "an_other")
    ]));

var impact = await om.ImpactAnalysisAsync(new ImpactAnalysisInput(
    "an_root",
    ["analytics_link"],
    MaxDepth: 2,
    Direction: OmAnalyticsDirection.Outgoing));
Assert(impact.Template == "impactAnalysis" && impact.Stats.ImpactedCount == 2, "impact analysis should return graph stats");
Assert(impact.Data.Visual.Graph.Nodes.Any(n => n.Id == "an_root" && n.Flags.IsRoot), "impact analysis visual graph should flag root");
Assert(impact.Data.Visual.Graph.Edges.Count >= 2, "impact analysis visual graph should include edges");

var ownership = await om.OwnershipTreeAsync(new OwnershipTreeInput("an_root", ["analytics_link"], MaxDepth: 2));
Assert(ownership.Template == "ownershipTree" && ownership.Data.Visual.Tree.RootId == "an_root", "ownership tree should return root tree visual");
Assert(ownership.Data.Visual.Tree.ChildrenById["an_root"].Count == 2, "ownership tree should group children by root id");
Assert(ownership.Data.Visual.Graph.Nodes.Count >= 3, "ownership tree should include graph visual");

var hotspots = await om.RiskHotspotAsync(new RiskHotspotInput("AnalyticsNode", "risk", TopK: 2, DegreeWeight: 1));
Assert(hotspots.Template == "riskHotspot" && hotspots.Stats.ReturnedCount == 2, "risk hotspot should return ranking stats");
Assert(hotspots.Data.Visual.Ranking[0].Score >= hotspots.Data.Visual.Ranking[1].Score, "risk hotspot ranking should sort descending");
Assert(hotspots.Data.Visual.Ranking[0].Id == "an_child" && hotspots.Data.Visual.Ranking[1].Id == "an_other", "risk hotspot should rank by risk plus degree");

await om.InitCodeKnowledgeAsync();
var ckResult = await om.IndexCodeKnowledgeAsync(new CodeKnowledgeBatch(
    Repositories: [new CodeRepositoryFact("repo", "/repo", "Repo", "abc123")],
    Files:
    [
        new CodeFileFact("file:order", "repo", "src/OrderService.cs", "csharp", "h1", "2026-01-01T00:00:00Z"),
        new CodeFileFact("file:docs", "repo", "docs/orders.md", "markdown", "h2", "2026-01-01T00:00:00Z")
    ],
    Symbols:
    [
        new CodeSymbolFact("sym:OrderService.Create", "file:order", "Create", "method", 10, 20, "Create(Order order)"),
        new CodeSymbolFact("sym:OrderRepository.Save", "file:order", "Save", "method", 30, 40, "Save(Order order)"),
        new CodeSymbolFact("sym:Undocumented", "file:order", "Undocumented", "method", 50, 60, "Undocumented()")
    ],
    Relations:
    [
        new CodeRelationFact("sym:OrderService.Create", "sym:OrderRepository.Save", "calls", "file:order", 14, "Create calls Save"),
        new CodeRelationFact("sym:OrderService.Create", "doc:orders-create", "documents", "file:docs", 3, "docs describe Create"),
        new CodeRelationFact("sym:OrderService.Create", "concept:order", "mentions", "file:docs", 4, "mentions order concept")
    ],
    DocBlocks: [new CodeDocBlockFact("doc:orders-create", "file:docs", "orders-create", "Create order docs", "dh1", "2026-01-01T00:00:00Z")],
    Concepts: [new CodeConceptFact("concept:order", "Order", "Order concept")],
    // process-extraction track: syntax-level entry-point candidates ship through the batch (:put).
    EntryPoints: [new CodeEntryPointFact("sym:OrderService.Create", "http_route", "detector=indexing;registrar=MapPost")]));
Assert(ckResult is { Repositories: 1, Files: 2, Symbols: 3, Relations: 3, DocBlocks: 1, Concepts: 1, EntryPoints: 1 }, "code knowledge indexing should report fact counts");
using (var batchEntryRows = db.Run("""?[symbol_id, metadata] := *ck_entry_point{ symbol_id, kind: "http_route", metadata }"""))
{
    var batchEntries = Rows(batchEntryRows);
    Assert(batchEntries.GetArrayLength() == 1
            && batchEntries[0][0].GetString() == "sym:OrderService.Create"
            && batchEntries[0][1].GetString() == "detector=indexing;registrar=MapPost",
        "batch EntryPoints should be written to ck_entry_point with metadata intact");
}

var registry = CozoOmCodeKnowledgeExtensions.CreateCodeKnowledgeRegistry();
var queryEngine = om.CreateQueryEngine(registry);
var named = await queryEngine.ExecuteNamedAsync(new NamedQueryInput(
    "code.symbolContext",
    new Dictionary<string, object?> { ["symbolId"] = "sym:OrderService.Create" }));
Assert(named.Success && named.Table.Rows.Count == 1, "NamedQuery should return symbol context table");
Assert(named.Table.Rows[0]["Name"].GetString() == "Create", "NamedQuery table should include symbol name");

var invalidNamed = await queryEngine.ExecutePortableAsync(new PortableDatalogQueryInput(
    "? bad(",
    new Dictionary<string, CozoStoredRelationMapping>()));
Assert(!invalidNamed.Success && invalidNamed.Diagnostics.Count > 0, "invalid Portable Datalog query should return diagnostics");

var symbolContext = await om.FindSymbolContextAsync("sym:OrderService.Create");
Assert(symbolContext.Symbol is not null && symbolContext.Symbol.Name == "Create", "symbol context facade should return symbol");
Assert(symbolContext.Outgoing.Any(r => r.Kind == CodeEdgeKinds.Calls), "symbol context facade should include outgoing calls (legacy 'calls' normalized to CALLS)");
Assert(symbolContext.Docs.Count == 1 && symbolContext.Docs[0].DocId == "doc:orders-create", "symbol context facade should include docs");

var impactOfChange = await om.ImpactOfChangeAsync("sym:OrderService.Create");
Assert(impactOfChange.ImpactedIds.Contains("sym:OrderRepository.Save"), "impact facade should include transitive impacted symbol");
var impactGraph = await queryEngine.ExecuteNamedAsync(new NamedQueryInput(
    "code.impactOfChange",
    new Dictionary<string, object?> { ["symbolId"] = "sym:OrderService.Create" }));
Assert(impactGraph.Success && impactGraph.Graph is { Edges.Count: > 0 }, "NamedQuery graph result should map Source/Target/Kind columns");

var docsForCode = await om.DocsForCodeAsync("sym:OrderService.Create");
Assert(!docsForCode.Missing && docsForCode.Docs.Count == 1, "docs facade should return linked docs");
var missingDocs = await om.DocsForCodeAsync("sym:Undocumented");
Assert(missingDocs.Missing, "docs facade should report missing docs");

var relationExplanation = await om.ExplainRelationAsync("sym:OrderService.Create", "sym:OrderRepository.Save");
Assert(relationExplanation.Relations.Any(r => r.Kind == CodeEdgeKinds.Calls && r.Evidence.Contains("Create calls", StringComparison.Ordinal)), "relation explanation should include evidence");

var conceptTrace = await om.TraceConceptAsync("concept:order");
Assert(conceptTrace.Mentions.Count == 1 && conceptTrace.Mentions[0].FromId == "sym:OrderService.Create", "concept trace should return mention relation");

var wikiPlan = await om.BuildWikiPlanAsync();
Assert(wikiPlan.Pages.Any(p => p.SourceFileIds.Contains("file:order")), "wiki plan should include source file page");
Assert(wikiPlan.MissingDocs.Contains("sym:Undocumented"), "wiki plan should report missing docs");

// --- CodeKnowledge v2 schema init / idempotency / v1 detection / reindex (T1.1) ---

static HashSet<string> CkRelationNames(CozoDb db)
{
    using var doc = db.Run("::relations");
    var names = new HashSet<string>(StringComparer.Ordinal);
    foreach (var row in Rows(doc).EnumerateArray())
    {
        var name = row[0].ValueKind == JsonValueKind.String ? row[0].GetString() : null;
        if (name is not null)
        {
            names.Add(name);
        }
    }

    return names;
}

static int CkSchemaVersion(CozoDb db)
{
    using var doc = db.Run("""?[value] := *ck_meta{ key: "schema_version", value }""");
    var rows = Rows(doc);
    if (rows.GetArrayLength() == 0)
    {
        return -1;
    }

    var value = rows[0][0];
    return value.ValueKind switch
    {
        JsonValueKind.Number => value.GetInt32(),
        JsonValueKind.String when int.TryParse(value.GetString(), out var parsed) => parsed,
        _ => -1
    };
}

static HashSet<string> CkColumns(CozoDb db, string relation)
{
    using var doc = db.Run($"::columns {relation}");
    var columns = new HashSet<string>(StringComparer.Ordinal);
    foreach (var row in Rows(doc).EnumerateArray())
    {
        var name = row[0].ValueKind == JsonValueKind.String ? row[0].GetString() : null;
        if (name is not null)
        {
            columns.Add(name);
        }
    }

    return columns;
}

string[] ckV2Relations =
[
    "ck_meta", "ck_repo", "ck_file", "ck_symbol", "ck_edge",
    "ck_entry_point", "ck_community", "ck_member", "ck_process", "ck_process_step",
    "ck_doc_block", "ck_concept", "ck_diagnostic", "ck_owner", "ck_wiki_page"
];

// T1.1-AC1: empty database → v2 relations + schema_version=2; re-init is idempotent.
using (var ckV2Db = new CozoDb(engine: "mem", path: ""))
{
    var ckV2Om = new CozoOm(ckV2Db);
    await ckV2Om.InitCodeKnowledgeAsync();
    var ckNames = CkRelationNames(ckV2Db);
    foreach (var rel in ckV2Relations)
    {
        Assert(ckNames.Contains(rel), $"v2 init on empty db should create {rel}");
    }

    Assert(CkSchemaVersion(ckV2Db) == 2, "v2 init should set ck_meta.schema_version = 2");
    var ckSymbolCols = CkColumns(ckV2Db, "ck_symbol");
    foreach (var col in new[] { "parent_id", "lang", "visibility", "exported", "sym_key", "doc_id", "resolver" })
    {
        Assert(ckSymbolCols.Contains(col), $"v2 ck_symbol should have column {col}");
    }

    var ckEdgeCols = CkColumns(ckV2Db, "ck_edge");
    foreach (var col in new[] { "from_id", "to_id", "kind", "file_id", "line", "confidence", "resolver", "evidence" })
    {
        Assert(ckEdgeCols.Contains(col), $"v2 ck_edge should have column {col}");
    }

    ckV2Db.Run(
        """
        ?[from_id, to_id, kind, file_id, line, confidence, resolver, evidence] <-
          [["sym:a", "sym:b", "CALLS", "file:a", 12, 0.3, "regex", "a calls b"]]
        :put ck_edge {from_id, to_id, kind, file_id, line => confidence, resolver, evidence}
        """);
    await ckV2Om.InitCodeKnowledgeAsync();
    Assert(CkSchemaVersion(ckV2Db) == 2, "repeated v2 init should keep schema_version = 2");
    using var ckEdgeRows = ckV2Db.Run(
        """?[confidence, resolver] := *ck_edge{ from_id: "sym:a", to_id: "sym:b", kind: "CALLS", file_id: "file:a", line: 12, confidence, resolver }""");
    Assert(Rows(ckEdgeRows).GetArrayLength() == 1, "repeated v2 init should be idempotent and preserve existing data");
    Assert(Rows(ckEdgeRows)[0][1].GetString() == "regex", "preserved ck_edge row should keep resolver value");
}

// T1.1-AC2: legacy v1 tables (ck_relation without ck_meta) → error with reindex guidance; reindex rebuilds v2.
using (var ckV1Db = new CozoDb(engine: "mem", path: ""))
{
    ckV1Db.Run(":create ck_relation {from_id, to_id, kind => file_id, line, evidence}");
    ckV1Db.Run(":create ck_symbol {symbol_id => file_id, name, kind, start_line, end_line, signature}");
    ckV1Db.Run(
        """
        ?[from_id, to_id, kind, file_id, line, evidence] <- [["sym:x", "sym:y", "calls", "file:x", 1, "legacy"]]
        :put ck_relation {from_id, to_id, kind => file_id, line, evidence}
        """);
    var ckV1Om = new CozoOm(ckV1Db);
    await ExpectCozoExceptionAsync(
        () => ckV1Om.InitCodeKnowledgeAsync(),
        "reindex",
        "v1 legacy schema should trigger an error containing reindex guidance");
    Assert(!CkRelationNames(ckV1Db).Contains("ck_meta"), "failed v1 detection should not silently mix v1 and v2 schema");

    await ckV1Om.InitCodeKnowledgeAsync(reindex: true);
    var rebuiltNames = CkRelationNames(ckV1Db);
    foreach (var rel in ckV2Relations)
    {
        Assert(rebuiltNames.Contains(rel), $"reindex should recreate v2 relation {rel}");
    }

    Assert(CkSchemaVersion(ckV1Db) == 2, "reindex should set ck_meta.schema_version = 2");
    Assert(CkColumns(ckV1Db, "ck_symbol").Contains("sym_key"), "reindex should replace v1 ck_symbol with v2 columns");
    Assert(!rebuiltNames.Contains("ck_relation"), "reindex should drop the legacy ck_relation relation (and its data) entirely");
}

// --- CodeKnowledge v2 edge facts write path (T1.2) ---

// T1.2-AC1: edge facts with confidence/resolver are stored and queryable; call sites with the
// same (from, to, kind) but different (file_id, line) are all preserved (delta.xml requirements/edge-facts).
using (var ckEdgeDb = new CozoDb(engine: "mem", path: ""))
{
    var ckEdgeOm = new CozoOm(ckEdgeDb);
    await ckEdgeOm.InitCodeKnowledgeAsync();
    Assert(!CkRelationNames(ckEdgeDb).Contains("ck_relation"), "v2 init should no longer create the transitional ck_relation relation");

    var edgeResult = await ckEdgeOm.IndexCodeKnowledgeAsync(new CodeKnowledgeBatch(
        Symbols:
        [
            new CodeSymbolFact(
                "sym:caller", "file:main", "Caller", "method", 5, 15, "Caller()",
                ParentId: "sym:Program", Lang: "csharp", Visibility: "public", Exported: true,
                SymKey: "csharp:Demo.Program.Caller/0", DocId: "M:Demo.Program.Caller", Resolver: "regex")
        ],
        Edges:
        [
            new CodeEdgeFact("sym:caller", "sym:callee", CodeEdgeKinds.Calls, "file:main", 12, 0.3, "regex", "Caller() calls Callee() at line 12"),
            new CodeEdgeFact("sym:caller", "sym:callee", CodeEdgeKinds.Calls, "file:main", 27, 0.3, "regex", "Caller() calls Callee() at line 27")
        ],
        Relations:
        [
            new CodeRelationFact("file:main", "sym:caller", "defines", "file:main", 5, "legacy relation path")
        ]));
    Assert(edgeResult is { Symbols: 1, Edges: 2, Relations: 1 }, "index result should count edges and converted legacy relations");

    // edge-with-confidence: ck_edge keeps confidence and resolver.
    // callsite-granularity: both CALLS rows for the same (from, to, kind) survive, keyed by (file_id, line).
    using var ckCallRows = ckEdgeDb.Run(
        """
        ?[line, confidence, resolver, evidence] :=
          *ck_edge{ from_id: "sym:caller", to_id: "sym:callee", kind: "CALLS", file_id: "file:main", line, confidence, resolver, evidence }
        :sort line
        """);
    Assert(Rows(ckCallRows).GetArrayLength() == 2, "both call sites of the same (from,to,kind) should be preserved");
    Assert(Rows(ckCallRows)[0][0].GetInt32() == 12 && Rows(ckCallRows)[1][0].GetInt32() == 27, "call sites should be distinguished by line");
    Assert(Math.Abs(Rows(ckCallRows)[0][1].GetDouble() - 0.3) < 1e-9, "ck_edge should persist confidence");
    Assert(Rows(ckCallRows)[0][2].GetString() == "regex", "ck_edge should persist resolver");

    // Legacy Relations are converted to ck_edge rows with confidence=0.3 / resolver=regex,
    // and legacy v1 kinds are normalized (defines → CONTAINS).
    using var ckConvertedRows = ckEdgeDb.Run(
        """
        ?[confidence, resolver, evidence] :=
          *ck_edge{ from_id: "file:main", to_id: "sym:caller", kind: "CONTAINS", file_id: "file:main", line: 5, confidence, resolver, evidence }
        """);
    Assert(Rows(ckConvertedRows).GetArrayLength() == 1, "legacy Relations entries should be written to ck_edge");
    Assert(Math.Abs(Rows(ckConvertedRows)[0][0].GetDouble() - 0.3) < 1e-9, "converted legacy relation should get confidence 0.3");
    Assert(Rows(ckConvertedRows)[0][1].GetString() == "regex", "converted legacy relation should get resolver regex");
    Assert(Rows(ckConvertedRows)[0][2].GetString() == "legacy relation path", "converted legacy relation should keep evidence");

    // ck_symbol v2 value columns round-trip through IndexCodeKnowledgeAsync.
    using var ckSymbolRows = ckEdgeDb.Run(
        """
        ?[parent_id, lang, visibility, exported, sym_key, doc_id, resolver] :=
          *ck_symbol{ symbol_id: "sym:caller", parent_id, lang, visibility, exported, sym_key, doc_id, resolver }
        """);
    Assert(Rows(ckSymbolRows).GetArrayLength() == 1, "v2 symbol should be stored");
    Assert(Rows(ckSymbolRows)[0][0].GetString() == "sym:Program", "ck_symbol should persist parent_id");
    Assert(Rows(ckSymbolRows)[0][1].GetString() == "csharp", "ck_symbol should persist lang");
    Assert(Rows(ckSymbolRows)[0][3].ValueKind == JsonValueKind.True, "ck_symbol should persist exported");
    Assert(Rows(ckSymbolRows)[0][4].GetString() == "csharp:Demo.Program.Caller/0", "ck_symbol should persist sym_key");
    Assert(Rows(ckSymbolRows)[0][5].GetString() == "M:Demo.Program.Caller", "ck_symbol should persist doc_id");
    Assert(Rows(ckSymbolRows)[0][6].GetString() == "regex", "ck_symbol should persist resolver");

    // Indexing updates ck_meta.indexed_at.
    using var ckIndexedAt = ckEdgeDb.Run("""?[value] := *ck_meta{ key: "indexed_at", value }""");
    Assert(Rows(ckIndexedAt).GetArrayLength() == 1 && (Rows(ckIndexedAt)[0][0].GetString() ?? "").Length > 0,
        "IndexCodeKnowledgeAsync should record ck_meta.indexed_at");
}

// --- CodeKnowledge query migration + minConfidence + fixed-rule projections (T2.1) ---

// T2.1-AC1: symbol_context/impact run on ck_edge with contract-compatible results and
// minConfidence filtering (delta.xml requirements/query-compat: symbol-context-on-edges, impact-min-confidence).
using (var ckQueryDb = new CozoDb(engine: "mem", path: ""))
{
    var ckQueryOm = new CozoOm(ckQueryDb);
    await ckQueryOm.InitCodeKnowledgeAsync();
    await ckQueryOm.IndexCodeKnowledgeAsync(new CodeKnowledgeBatch(
        Files: [new CodeFileFact("file:svc", "repo", "src/Service.cs", "csharp", "h", "2026-01-01T00:00:00Z")],
        Symbols:
        [
            new CodeSymbolFact("sym:a", "file:svc", "A", "method", 1, 10, "A()"),
            new CodeSymbolFact("sym:b", "file:svc", "B", "method", 11, 20, "B()"),
            new CodeSymbolFact("sym:c", "file:svc", "C", "method", 21, 30, "C()")
        ],
        Edges:
        [
            new CodeEdgeFact("sym:a", "sym:b", CodeEdgeKinds.Calls, "file:svc", 5, 1.0, "roslyn", "A calls B"),
            new CodeEdgeFact("sym:b", "sym:c", CodeEdgeKinds.Calls, "file:svc", 15, 0.3, "regex", "B calls C"),
            new CodeEdgeFact("sym:a", "doc:svc", CodeEdgeKinds.DocLinks, "file:svc", 0, 0.3, "regex", "doc links A"),
            new CodeEdgeFact("sym:a", "concept:svc", CodeEdgeKinds.Mentions, "file:svc", 0, 0.3, "regex", "mentions Service")
        ],
        DocBlocks: [new CodeDocBlockFact("doc:svc", "file:svc", "svc", "Service docs", "dh", "2026-01-01T00:00:00Z")],
        Concepts: [new CodeConceptFact("concept:svc", "Service", "Service concept")]));

    // symbol-context-on-edges: v1 contract fields preserved, confidence added.
    var ckContext = await ckQueryOm.FindSymbolContextAsync("sym:a");
    Assert(ckContext.Symbol is not null && ckContext.Symbol.Name == "A", "v2 symbol context should resolve symbol from ck_symbol");
    var ckOutgoingCall = ckContext.Outgoing.FirstOrDefault(r => r.ToId == "sym:b");
    Assert(ckOutgoingCall is not null && ckOutgoingCall.Kind == CodeEdgeKinds.Calls, "symbol context should expose outgoing CALLS edges from ck_edge");
    Assert(Math.Abs(ckOutgoingCall!.Confidence - 1.0) < 1e-9, "symbol context relations should carry ck_edge confidence");
    Assert(ckContext.Docs.Count == 1 && ckContext.Docs[0].DocId == "doc:svc", "symbol context should resolve docs via DOC_LINKS edges");
    var ckIncoming = (await ckQueryOm.FindSymbolContextAsync("sym:b")).Incoming.FirstOrDefault(r => r.FromId == "sym:a");
    Assert(ckIncoming is not null && Math.Abs(ckIncoming.Confidence - 1.0) < 1e-9, "incoming relations should also carry confidence");

    // impact default: minConfidence=0.0 keeps the low-confidence transitive continuation.
    var ckImpactAll = await ckQueryOm.ImpactOfChangeAsync("sym:a");
    Assert(ckImpactAll.ImpactedIds.Contains("sym:b") && ckImpactAll.ImpactedIds.Contains("sym:c"),
        "impact with default minConfidence should include low-confidence transitive targets");
    Assert(ckImpactAll.Edges.Any(e => e.ToId == "sym:b" && Math.Abs(e.Confidence - 1.0) < 1e-9),
        "impact edges should carry ck_edge confidence");

    // impact-min-confidence: only confidence >= 0.8 hops survive.
    var ckImpactHigh = await ckQueryOm.ImpactOfChangeAsync("sym:a", minConfidence: 0.8);
    Assert(ckImpactHigh.ImpactedIds.Contains("sym:b"), "impact with minConfidence=0.8 should keep the confidence 1.0 edge");
    Assert(!ckImpactHigh.ImpactedIds.Contains("sym:c"), "impact with minConfidence=0.8 should drop the confidence 0.3 continuation");

    // Remaining facades on v2 kinds.
    var ckDocs = await ckQueryOm.DocsForCodeAsync("sym:a");
    Assert(!ckDocs.Missing && ckDocs.Docs[0].DocId == "doc:svc", "docs_for_code should read DOC_LINKS edges");
    var ckTrace = await ckQueryOm.TraceConceptAsync("concept:svc");
    Assert(ckTrace.Mentions.Count == 1 && ckTrace.Mentions[0].FromId == "sym:a", "trace_concept should read MENTIONS edges");
    var ckExplain = await ckQueryOm.ExplainRelationAsync("sym:a", "sym:b");
    Assert(ckExplain.Relations.Any(r => r.Kind == CodeEdgeKinds.Calls && Math.Abs(r.Confidence - 1.0) < 1e-9),
        "explain_relation should surface ck_edge kind and confidence");

    // Legacy v1 kinds are normalized when converting Relations → ck_edge (defines → CONTAINS).
    await ckQueryOm.IndexCodeKnowledgeAsync(new CodeKnowledgeBatch(
        Relations: [new CodeRelationFact("file:svc", "sym:a", "defines", "file:svc", 1, "legacy defines")]));
    using var ckNormalizedRows = ckQueryDb.Run(
        """?[evidence] := *ck_edge{ from_id: "file:svc", to_id: "sym:a", kind: "CONTAINS", evidence }""");
    Assert(Rows(ckNormalizedRows).GetArrayLength() == 1, "legacy v1 kind 'defines' should be normalized to CONTAINS on conversion");
}

// T2.1-AC2: call_graph/import_graph/cluster_input projections; cluster_input is consumable by the
// CommunityDetectionLouvain fixed rule and import_graph by StronglyConnectedComponents
// (delta.xml requirements/query-compat: louvain-projection; design.md §5).
using (var ckGraphDb = new CozoDb(engine: "mem", path: ""))
{
    var ckGraphOm = new CozoOm(ckGraphDb);
    await ckGraphOm.InitCodeKnowledgeAsync();
    await ckGraphOm.IndexCodeKnowledgeAsync(new CodeKnowledgeBatch(
        Symbols:
        [
            new CodeSymbolFact("sym:a1", "file:m1", "Alpha", "method", 1, 1, "Alpha()"),
            new CodeSymbolFact("sym:a2", "file:m1", "AlphaHelper", "method", 2, 2, "AlphaHelper()"),
            new CodeSymbolFact("sym:b1", "file:m2", "Beta", "method", 1, 1, "Beta()"),
            new CodeSymbolFact("sym:b2", "file:m2", "BetaHelper", "method", 2, 2, "BetaHelper()")
        ],
        Edges:
        [
            // Two dense CALLS clusters with one weak cross link.
            new CodeEdgeFact("sym:a1", "sym:a2", CodeEdgeKinds.Calls, "file:m1", 3, 1.0, "roslyn", ""),
            new CodeEdgeFact("sym:a2", "sym:a1", CodeEdgeKinds.Calls, "file:m1", 4, 1.0, "roslyn", ""),
            new CodeEdgeFact("sym:b1", "sym:b2", CodeEdgeKinds.Calls, "file:m2", 3, 1.0, "roslyn", ""),
            new CodeEdgeFact("sym:b2", "sym:b1", CodeEdgeKinds.Calls, "file:m2", 4, 1.0, "roslyn", ""),
            new CodeEdgeFact("sym:a1", "sym:b1", CodeEdgeKinds.Calls, "file:m1", 9, 0.3, "regex", ""),
            // IMPORTS cycle m1 → m2 → m3 → m1 plus an acyclic tail m4 → m5.
            new CodeEdgeFact("file:m1", "file:m2", CodeEdgeKinds.Imports, "file:m1", 1, 0.3, "regex", ""),
            new CodeEdgeFact("file:m2", "file:m3", CodeEdgeKinds.Imports, "file:m2", 1, 0.3, "regex", ""),
            new CodeEdgeFact("file:m3", "file:m1", CodeEdgeKinds.Imports, "file:m3", 1, 0.3, "regex", ""),
            new CodeEdgeFact("file:m4", "file:m5", CodeEdgeKinds.Imports, "file:m4", 1, 0.3, "regex", "")
        ]));

    // call_graph projection honors $min_confidence.
    using var ckCallGraphHigh = ckGraphDb.Run(
        CodeGraphProjections.CallGraph + "\n?[from, to] := call_graph[from, to]",
        new Dictionary<string, object?> { ["min_confidence"] = 0.8 });
    Assert(Rows(ckCallGraphHigh).GetArrayLength() == 4, "call_graph projection should filter edges below $min_confidence");
    using var ckCallGraphAll = ckGraphDb.Run(
        CodeGraphProjections.CallGraph + "\n?[from, to] := call_graph[from, to]",
        new Dictionary<string, object?> { ["min_confidence"] = 0.0 });
    Assert(Rows(ckCallGraphAll).GetArrayLength() == 5, "call_graph projection with min_confidence 0.0 should keep all CALLS edges");

    // import_graph projection contains exactly the IMPORTS edges.
    using var ckImportGraph = ckGraphDb.Run(
        CodeGraphProjections.ImportGraph + "\n?[from, to] := import_graph[from, to]");
    Assert(Rows(ckImportGraph).GetArrayLength() == 4, "import_graph projection should contain exactly the IMPORTS edges");

    // louvain-projection: cluster_input feeds CommunityDetectionLouvain; result persists to ck_community/ck_member.
    var ckCommunities = await ckGraphOm.ComputeCommunitiesAsync(new CommunityDetectionOptions(MinCommunitySize: 1));
    Assert(ckCommunities.Communities.Count >= 1, "louvain over cluster_input should produce at least one community");
    Assert(ckCommunities.MemberCommunity.Count >= 4, "louvain should assign the symbols to communities");
    Assert(ckCommunities.Communities.All(c => c.Algo == "louvain"), "persisted communities should record algo=louvain");
    Assert(ckCommunities.MemberCommunity["sym:a1"] == ckCommunities.MemberCommunity["sym:a2"],
        "densely connected CALLS pair should land in the same community");
    using var ckCommunityRows = ckGraphDb.Run("?[community_id, label, cohesion, symbol_count, algo] := *ck_community{community_id, label, cohesion, symbol_count, algo}");
    Assert(Rows(ckCommunityRows).GetArrayLength() == ckCommunities.Communities.Count, "communities should be persisted to ck_community");
    using var ckMemberRows = ckGraphDb.Run("?[symbol_id, community_id] := *ck_member{symbol_id, community_id}");
    Assert(Rows(ckMemberRows).GetArrayLength() == ckCommunities.MemberCommunity.Count, "memberships should be persisted to ck_member");

    // SCC over import_graph: exactly the m1/m2/m3 cycle (size > 1 components only).
    var ckCycles = await ckGraphOm.DetectCyclesAsync();
    Assert(ckCycles.Count == 1, "StronglyConnectedComponents over import_graph should find exactly one cycle");
    Assert(ckCycles[0].Kind == CodeEdgeKinds.Imports, "default DetectCyclesAsync should report IMPORTS cycles");
    Assert(ckCycles[0].Members.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(["file:m1", "file:m2", "file:m3"]),
        "detected import cycle should contain exactly the cyclic files");
}

// --- trace 与 cycles API (track add-llm-wiki-trace-and-check T1.1) ---

// T1.1-AC1 (delta.xml suites "trace" first 3 cases + "cycles" 2 cases at the API layer):
// known-path, no-path, min-confidence-cut, import-cycle, no-cycle; deterministic output.
using (var ckTraceDb = new CozoDb(engine: "mem", path: ""))
{
    var ckTraceOm = new CozoOm(ckTraceDb);
    await ckTraceOm.InitCodeKnowledgeAsync();
    await ckTraceOm.IndexCodeKnowledgeAsync(new CodeKnowledgeBatch(
        Symbols:
        [
            new CodeSymbolFact("sym:ta", "file:t", "A", "method", 1, 10, "A()"),
            new CodeSymbolFact("sym:tb", "file:t", "B", "method", 11, 20, "B()"),
            new CodeSymbolFact("sym:tc", "file:t", "C", "method", 21, 30, "C()"),
            new CodeSymbolFact("sym:tx", "file:t", "X", "method", 31, 40, "X()"),
            // D is isolated: no CALLS connectivity to/from A.
            new CodeSymbolFact("sym:td", "file:t", "D", "method", 41, 50, "D()")
        ],
        Edges:
        [
            // known-path chain A -> B -> C at confidence 0.9; A -> B has a second, lower-confidence
            // call site so hop assembly must pick the highest-confidence row per pair.
            new CodeEdgeFact("sym:ta", "sym:tb", CodeEdgeKinds.Calls, "file:t", 5, 0.9, "roslyn", "A calls B"),
            new CodeEdgeFact("sym:ta", "sym:tb", CodeEdgeKinds.Calls, "file:t", 8, 0.8, "regex", "A calls B again"),
            new CodeEdgeFact("sym:tb", "sym:tc", CodeEdgeKinds.Calls, "file:t", 15, 0.9, "roslyn", "B calls C"),
            // min-confidence-cut: A -> X only via a single confidence 0.5 edge.
            new CodeEdgeFact("sym:ta", "sym:tx", CodeEdgeKinds.Calls, "file:t", 9, 0.5, "regex", "A maybe calls X"),
            // import-cycle: tm1 -> tm3 -> tm2 -> tm1 (deliberately not in sorted order so the
            // canonical rotation is observable) plus an acyclic tail tm4 -> tm5.
            new CodeEdgeFact("file:tm1", "file:tm3", CodeEdgeKinds.Imports, "file:tm1", 1, 0.3, "regex", ""),
            new CodeEdgeFact("file:tm3", "file:tm2", CodeEdgeKinds.Imports, "file:tm3", 1, 0.3, "regex", ""),
            new CodeEdgeFact("file:tm2", "file:tm1", CodeEdgeKinds.Imports, "file:tm2", 1, 0.3, "regex", ""),
            new CodeEdgeFact("file:tm4", "file:tm5", CodeEdgeKinds.Imports, "file:tm4", 1, 0.3, "regex", "")
        ]));

    static string DescribeTrace(TraceResult result) =>
        $"found={result.Found};reason={result.Reason};hops="
        + string.Join(";", result.Hops.Select(h =>
            $"{h.FromId}({h.FromName})->{h.ToId}({h.ToName})@{h.FileId}:{h.Line}|{h.Kind}|{h.Confidence:R}"));

    static string DescribeCycles(IReadOnlyList<CodeCycle> cycles) =>
        string.Join(";", cycles.Select(c => $"{c.Kind}:{string.Join("->", c.Members)}"));

    // known-path: ordered hops A -> B -> C, each with file:line and confidence; per-pair
    // highest-confidence call site wins (line 5 at 0.9, not line 8 at 0.8).
    var traceKnown = await ckTraceOm.TraceCallPathAsync("sym:ta", "sym:tc");
    Assert(traceKnown.Found, "trace A->C should find the known path");
    Assert(traceKnown.Hops.Count == 2, "trace A->C should have exactly two hops");
    Assert(traceKnown.Hops[0].FromId == "sym:ta" && traceKnown.Hops[0].ToId == "sym:tb"
        && traceKnown.Hops[1].FromId == "sym:tb" && traceKnown.Hops[1].ToId == "sym:tc",
        "trace hops should be ordered A->B then B->C");
    Assert(traceKnown.Hops[0].FromName == "A" && traceKnown.Hops[0].ToName == "B" && traceKnown.Hops[1].ToName == "C",
        "trace hops should carry symbol names from ck_symbol");
    Assert(traceKnown.Hops[0].FileId == "file:t" && traceKnown.Hops[0].Line == 5
        && Math.Abs(traceKnown.Hops[0].Confidence - 0.9) < 1e-9,
        "trace hop should carry the highest-confidence call site of the pair (file:t line 5 at 0.9)");
    Assert(traceKnown.Hops[1].Line == 15 && traceKnown.Hops[1].Kind == CodeEdgeKinds.Calls,
        "trace hop should carry file:line and edge kind");

    // no-path: A and D have no call connectivity — empty hops with a reason, no exception.
    var traceNoPath = await ckTraceOm.TraceCallPathAsync("sym:ta", "sym:td");
    Assert(!traceNoPath.Found && traceNoPath.Hops.Count == 0 && traceNoPath.Reason.Length > 0,
        "trace to an unconnected symbol should report not found with a reason, without erroring");

    // min-confidence-cut: default MinConfidence=0.7 hides the 0.5 edge; 0.5 reveals it.
    var traceCutDefault = await ckTraceOm.TraceCallPathAsync("sym:ta", "sym:tx");
    Assert(!traceCutDefault.Found && traceCutDefault.Hops.Count == 0,
        "trace at default MinConfidence=0.7 should not use the confidence 0.5 edge");
    var traceCutLow = await ckTraceOm.TraceCallPathAsync("sym:ta", "sym:tx", new TraceOptions(MinConfidence: 0.5));
    Assert(traceCutLow.Found && traceCutLow.Hops.Count == 1 && Math.Abs(traceCutLow.Hops[0].Confidence - 0.5) < 1e-9,
        "trace at MinConfidence=0.5 should return the single-hop path over the 0.5 edge");

    // MaxDepth: the 2-hop shortest path is treated as no path when MaxDepth=1.
    var traceDepthCut = await ckTraceOm.TraceCallPathAsync("sym:ta", "sym:tc", new TraceOptions(MaxDepth: 1));
    Assert(!traceDepthCut.Found && traceDepthCut.Hops.Count == 0 && traceDepthCut.Reason.Contains("MaxDepth"),
        "trace with MaxDepth below the shortest path length should report not found");

    // import-cycle: deterministic canonical path — starts at the smallest member id and
    // follows the actual IMPORTS edges (tm1 -> tm3 -> tm2).
    var cyclesImport = await ckTraceOm.DetectCyclesAsync(CycleKind.Import);
    Assert(cyclesImport.Count == 1 && cyclesImport[0].Kind == CodeEdgeKinds.Imports,
        "check cycles=import should find exactly the tm1/tm2/tm3 IMPORTS cycle");
    Assert(cyclesImport[0].Members.SequenceEqual(["file:tm1", "file:tm3", "file:tm2"]),
        "cycle members should be rotated to start at the smallest id and follow edge order");

    // no-cycle: the CALLS graph (A->B->C, A->X) is acyclic — empty list, no error.
    var cyclesCallsNone = await ckTraceOm.DetectCyclesAsync(CycleKind.Calls);
    Assert(cyclesCallsNone.Count == 0, "check cycles=calls on an acyclic CALLS graph should return an empty list");

    // Both = import + calls cycles in one deterministic list.
    var cyclesBoth = await ckTraceOm.DetectCyclesAsync(CycleKind.Both);
    Assert(cyclesBoth.Count == 1 && cyclesBoth[0].Kind == CodeEdgeKinds.Imports,
        "check cycles=both should currently return only the IMPORTS cycle");

    // determinism: double runs of trace and cycles produce identical output.
    Assert(DescribeTrace(traceKnown) == DescribeTrace(await ckTraceOm.TraceCallPathAsync("sym:ta", "sym:tc")),
        "two consecutive trace runs should produce identical output");
    Assert(DescribeCycles(cyclesBoth) == DescribeCycles(await ckTraceOm.DetectCyclesAsync(CycleKind.Both)),
        "two consecutive cycle detection runs should produce identical output");

    // CALLS cycle (optional case): closing the chain C -> A makes {A,B,C} a CALLS cycle.
    await ckTraceOm.IndexCodeKnowledgeAsync(new CodeKnowledgeBatch(
        Edges: [new CodeEdgeFact("sym:tc", "sym:ta", CodeEdgeKinds.Calls, "file:t", 25, 0.9, "roslyn", "C calls A")]));
    var cyclesCalls = await ckTraceOm.DetectCyclesAsync(CycleKind.Calls);
    Assert(cyclesCalls.Count == 1 && cyclesCalls[0].Kind == CodeEdgeKinds.Calls
        && cyclesCalls[0].Members.SequenceEqual(["sym:ta", "sym:tb", "sym:tc"]),
        "check cycles=calls should detect the closed CALLS ring with canonical member order");
    Assert((await ckTraceOm.DetectCyclesAsync(CycleKind.Both)).Count == 2,
        "check cycles=both should now report the CALLS and the IMPORTS cycle");
}

// --- Community detection formalization: quality / determinism / budget (track add-llm-wiki-community-detection T1.1) ---

// T1.1-AC1 (delta.xml suite "community", first 5 cases): two-clusters cohesion=1.0, deterministic
// double run, label-common-prefix, min-size-noise, budget-truncate.
using (var ckCommunityDb = new CozoDb(engine: "mem", path: ""))
{
    var ckCommunityOm = new CozoOm(ckCommunityDb);
    await ckCommunityOm.InitCodeKnowledgeAsync();
    await ckCommunityOm.IndexCodeKnowledgeAsync(new CodeKnowledgeBatch(
        Symbols:
        [
            // Cluster Alpha (3 members, shared qualified-name prefix Demo.Alpha).
            new CodeSymbolFact("sym:ka1", "file:alpha", "One", "method", 1, 1, "One()", SymKey: "csharp:Demo.Alpha.One/0"),
            new CodeSymbolFact("sym:ka2", "file:alpha", "Two", "method", 2, 2, "Two()", SymKey: "csharp:Demo.Alpha.Two/0"),
            new CodeSymbolFact("sym:ka3", "file:alpha", "Three", "method", 3, 3, "Three()", SymKey: "csharp:Demo.Alpha.Three/0"),
            // Cluster Beta (3 members, shared qualified-name prefix Demo.Beta).
            new CodeSymbolFact("sym:kb1", "file:beta", "PayX", "method", 1, 1, "PayX()", SymKey: "csharp:Demo.Beta.PayX/0"),
            new CodeSymbolFact("sym:kb2", "file:beta", "PayY", "method", 2, 2, "PayY()", SymKey: "csharp:Demo.Beta.PayY/0"),
            new CodeSymbolFact("sym:kb3", "file:beta", "PayZ", "method", 3, 3, "PayZ()", SymKey: "csharp:Demo.Beta.PayZ/0"),
            // Isolated 2-member cluster below the default MinCommunitySize=3 threshold.
            new CodeSymbolFact("sym:kz1", "file:noise", "N1", "method", 1, 1, "N1()", SymKey: "csharp:Demo.Noise.N1/0"),
            new CodeSymbolFact("sym:kz2", "file:noise", "N2", "method", 2, 2, "N2()", SymKey: "csharp:Demo.Noise.N2/0")
        ],
        Edges:
        [
            // Two internally dense CALLS rings with no edges between them.
            new CodeEdgeFact("sym:ka1", "sym:ka2", CodeEdgeKinds.Calls, "file:alpha", 1, 1.0, "roslyn", ""),
            new CodeEdgeFact("sym:ka2", "sym:ka3", CodeEdgeKinds.Calls, "file:alpha", 2, 1.0, "roslyn", ""),
            new CodeEdgeFact("sym:ka3", "sym:ka1", CodeEdgeKinds.Calls, "file:alpha", 3, 1.0, "roslyn", ""),
            new CodeEdgeFact("sym:kb1", "sym:kb2", CodeEdgeKinds.Calls, "file:beta", 1, 1.0, "roslyn", ""),
            new CodeEdgeFact("sym:kb2", "sym:kb3", CodeEdgeKinds.Calls, "file:beta", 2, 1.0, "roslyn", ""),
            new CodeEdgeFact("sym:kb3", "sym:kb1", CodeEdgeKinds.Calls, "file:beta", 3, 1.0, "roslyn", ""),
            // Isolated small cluster (lower confidence, so a MinConfidence filter can drop it).
            new CodeEdgeFact("sym:kz1", "sym:kz2", CodeEdgeKinds.Calls, "file:noise", 1, 0.4, "regex", "")
        ]));

    static string DescribeCommunities(CommunityDetectionResult result) =>
        string.Join(
            ";",
            result.Communities.Select(c =>
                $"{c.CommunityId}|{c.Label}|{c.Cohesion:R}|{c.SymbolCount}|{c.Algo}"))
        + "//"
        + string.Join(";", result.MemberCommunity.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}"))
        + $"//noise={result.NoiseSymbols};truncated={result.TruncatedCommunities}";

    // deterministic: two consecutive runs on the same graph produce identical partitions,
    // ids, labels, cohesion and diagnostics.
    var communityRun1 = await ckCommunityOm.ComputeCommunitiesAsync();
    var communityRun2 = await ckCommunityOm.ComputeCommunitiesAsync();
    Assert(DescribeCommunities(communityRun1) == DescribeCommunities(communityRun2),
        "two consecutive community detection runs should produce identical output");

    // two-clusters: both dense clusters land in distinct communities with cohesion exactly 1.0.
    Assert(communityRun1.Communities.Count == 2, "two dense clusters (plus sub-threshold noise) should yield exactly two communities");
    Assert(communityRun1.MemberCommunity["sym:ka1"] == communityRun1.MemberCommunity["sym:ka2"]
        && communityRun1.MemberCommunity["sym:ka2"] == communityRun1.MemberCommunity["sym:ka3"],
        "alpha cluster members should share one community");
    Assert(communityRun1.MemberCommunity["sym:kb1"] == communityRun1.MemberCommunity["sym:kb2"]
        && communityRun1.MemberCommunity["sym:kb2"] == communityRun1.MemberCommunity["sym:kb3"],
        "beta cluster members should share one community");
    Assert(communityRun1.MemberCommunity["sym:ka1"] != communityRun1.MemberCommunity["sym:kb1"],
        "disconnected clusters should land in different communities");
    Assert(communityRun1.Communities.All(c => Math.Abs(c.Cohesion - 1.0) < 1e-9),
        "isolated clusters should have cohesion exactly 1.0");
    Assert(communityRun1.Communities.All(c => c.Algo == "louvain"), "communities should record algo=louvain");

    // canonical numbering: (size desc, smallest member symbolId asc) → alpha before beta.
    Assert(communityRun1.Communities[0].CommunityId == "community:001" && communityRun1.Communities[1].CommunityId == "community:002",
        "communities should be numbered community:001… canonically");
    Assert(communityRun1.MemberCommunity["sym:ka1"] == "community:001" && communityRun1.MemberCommunity["sym:kb1"] == "community:002",
        "size ties should be broken by smallest member symbolId");

    // label-common-prefix: members share Demo.Alpha / Demo.Beta qualified-name prefixes.
    Assert(communityRun1.Communities[0].Label == "Demo.Alpha", "alpha community label should be the common qualified-name prefix Demo.Alpha");
    Assert(communityRun1.Communities[1].Label == "Demo.Beta", "beta community label should be the common qualified-name prefix Demo.Beta");

    // min-size-noise: the 2-member cluster is dropped, counted, and not persisted.
    Assert(communityRun1.NoiseSymbols == 2, "sub-threshold cluster members should be counted as noise");
    Assert(!communityRun1.MemberCommunity.ContainsKey("sym:kz1") && !communityRun1.MemberCommunity.ContainsKey("sym:kz2"),
        "noise symbols should not appear in the member mapping");
    using var ckNoiseMemberRows = ckCommunityDb.Run(
        """?[symbol_id] := *ck_member{ symbol_id, community_id }, symbol_id = "sym:kz1" or symbol_id = "sym:kz2" """);
    Assert(Rows(ckNoiseMemberRows).GetArrayLength() == 0, "noise symbols should not be persisted to ck_member");
    using var ckKeptCommunityRows = ckCommunityDb.Run("?[community_id] := *ck_community{ community_id }");
    Assert(Rows(ckKeptCommunityRows).GetArrayLength() == 2, "only above-threshold communities should be persisted to ck_community");

    // Public query surface reads back the persisted state.
    var listedCommunities = await ckCommunityOm.ListCommunitiesAsync();
    Assert(listedCommunities.SequenceEqual(communityRun1.Communities), "ListCommunitiesAsync should return persisted communities in canonical order");
    var alphaMembers = await ckCommunityOm.GetCommunityMembersAsync("community:001");
    Assert(alphaMembers.SequenceEqual(["sym:ka1", "sym:ka2", "sym:ka3"]), "GetCommunityMembersAsync should return sorted members");
    var betaCommunity = await ckCommunityOm.FindSymbolCommunityAsync("sym:kb2");
    Assert(betaCommunity is not null && betaCommunity.CommunityId == "community:002" && betaCommunity.Label == "Demo.Beta",
        "FindSymbolCommunityAsync should resolve a member's community");
    Assert(await ckCommunityOm.FindSymbolCommunityAsync("sym:kz1") is null,
        "FindSymbolCommunityAsync should return null for noise symbols");

    // budget-truncate: MaxCommunities=1 keeps the canonical first community and counts the rest.
    var truncatedRun = await ckCommunityOm.ComputeCommunitiesAsync(new CommunityDetectionOptions(MaxCommunities: 1));
    Assert(truncatedRun.Communities.Count == 1 && truncatedRun.TruncatedCommunities == 1,
        "communities beyond the MaxCommunities budget should be truncated and counted");
    Assert(truncatedRun.Communities[0].Label == "Demo.Alpha", "budget truncation should keep communities by (size desc, smallest member id asc)");
    using var ckTruncatedRows = ckCommunityDb.Run("?[community_id] := *ck_community{ community_id }");
    Assert(Rows(ckTruncatedRows).GetArrayLength() == 1, "truncated communities should not be persisted");

    // MinConfidence propagates into the cluster_input projection: with MinConfidence=0.5 the
    // 0.4-confidence noise pair is excluded from the graph entirely, so even MinCommunitySize=1
    // cannot surface it, while the 1.0-confidence rings are unaffected.
    var minConfidenceRun = await ckCommunityOm.ComputeCommunitiesAsync(
        new CommunityDetectionOptions(MinConfidence: 0.5, MinCommunitySize: 1));
    Assert(minConfidenceRun.Communities.Count == 2,
        "MinConfidence=0.5 should keep exactly the two high-confidence rings");
    Assert(!minConfidenceRun.MemberCommunity.ContainsKey("sym:kz1") && !minConfidenceRun.MemberCommunity.ContainsKey("sym:kz2"),
        "edges below MinConfidence should be excluded from the cluster_input projection");

    // Strategy seam: unknown algorithms are rejected.
    var unsupportedAlgoRejected = false;
    try
    {
        await ckCommunityOm.ComputeCommunitiesAsync(new CommunityDetectionOptions(Algorithm: "leiden"));
    }
    catch (ArgumentException)
    {
        unsupportedAlgoRejected = true;
    }

    Assert(unsupportedAlgoRejected, "unsupported community detection algorithms should be rejected");
}

// --- Process extraction: entry points + bounded BFS (track add-llm-wiki-process-extraction T1.1) ---

// T1.1-AC1 (delta.xml suite "process", first 6 cases): main-entry, route-entry, min-steps-dropped,
// budget-and-truncate, cycle-safe, cross-community-marked.
using (var ckProcessDb = new CozoDb(engine: "mem", path: ""))
{
    var ckProcessOm = new CozoOm(ckProcessDb);
    await ckProcessOm.InitCodeKnowledgeAsync();
    await ckProcessOm.IndexCodeKnowledgeAsync(new CodeKnowledgeBatch(
        Files:
        [
            new CodeFileFact("file:prog", "repo:demo", "src/Program.cs", "csharp"),
            new CodeFileFact("file:api", "repo:demo", "src/Api.cs", "csharp")
        ],
        Symbols:
        [
            // Main chain: Main -> StepA -> StepB -> StepC (with a StepB -> StepA back edge = cycle).
            new CodeSymbolFact("sym:main", "file:prog", "Main", "method", 1, 1, "Main()", Visibility: "private", SymKey: "csharp:Demo.Program.Main/0"),
            new CodeSymbolFact("sym:pa", "file:prog", "StepA", "method", 2, 2, "StepA()", Visibility: "private", SymKey: "csharp:Demo.Program.StepA/0"),
            new CodeSymbolFact("sym:pb", "file:prog", "StepB", "method", 3, 3, "StepB()", Visibility: "private", SymKey: "csharp:Demo.Program.StepB/0"),
            new CodeSymbolFact("sym:pc", "file:prog", "StepC", "method", 4, 4, "StepC()", Visibility: "private", SymKey: "csharp:Demo.Program.StepC/0"),
            // Route handler chain (entry only via the manually seeded ck_entry_point candidate row).
            new CodeSymbolFact("sym:route", "file:api", "GetUsers", "method", 1, 1, "GetUsers()", Visibility: "internal", SymKey: "csharp:Demo.Api.GetUsers/0"),
            new CodeSymbolFact("sym:r1", "file:api", "LoadUsers", "method", 2, 2, "LoadUsers()", Visibility: "private", SymKey: "csharp:Demo.Api.LoadUsers/0"),
            new CodeSymbolFact("sym:r2", "file:api", "MapUsers", "method", 3, 3, "MapUsers()", Visibility: "private", SymKey: "csharp:Demo.Api.MapUsers/0"),
            // public_api entries: pub1 reaches a long chain, pub2 reaches only one callee (< MinSteps).
            new CodeSymbolFact("sym:pub1", "file:api", "PublicOp", "method", 4, 4, "PublicOp()", Visibility: "public", Exported: true, SymKey: "csharp:Demo.Api.PublicOp/0"),
            new CodeSymbolFact("sym:pub2", "file:api", "ShortOp", "method", 5, 5, "ShortOp()", Visibility: "public", Exported: true, SymKey: "csharp:Demo.Api.ShortOp/0")
        ],
        Edges:
        [
            new CodeEdgeFact("sym:main", "sym:pa", CodeEdgeKinds.Calls, "file:prog", 1, 1.0, "roslyn"),
            new CodeEdgeFact("sym:pa", "sym:pb", CodeEdgeKinds.Calls, "file:prog", 2, 1.0, "roslyn"),
            new CodeEdgeFact("sym:pb", "sym:pa", CodeEdgeKinds.Calls, "file:prog", 3, 1.0, "roslyn"), // cycle A -> B -> A
            new CodeEdgeFact("sym:pb", "sym:pc", CodeEdgeKinds.Calls, "file:prog", 4, 1.0, "roslyn"),
            new CodeEdgeFact("sym:pc", "sym:r2", CodeEdgeKinds.Calls, "file:prog", 5, 0.5, "treesitter"), // below default MinConfidence=0.7
            new CodeEdgeFact("sym:route", "sym:r1", CodeEdgeKinds.Calls, "file:api", 1, 1.0, "roslyn"),
            new CodeEdgeFact("sym:r1", "sym:r2", CodeEdgeKinds.Calls, "file:api", 2, 1.0, "roslyn"),
            new CodeEdgeFact("sym:pub1", "sym:pa", CodeEdgeKinds.Calls, "file:api", 4, 1.0, "roslyn"),
            new CodeEdgeFact("sym:pub2", "sym:r2", CodeEdgeKinds.Calls, "file:api", 5, 1.0, "roslyn")
        ]));

    // route-entry: P2's indexing pipeline writes syntax-level http_route/mcp_tool candidates into
    // ck_entry_point; T1.1 covers the extraction side by seeding the candidate row manually
    // (decisions.md #1). Extraction must preserve this row across its ck_entry_point rebuild.
    using (var seedRoute = ckProcessDb.Run(
        """
        ?[symbol_id, kind, metadata] <- [["sym:route", "http_route", "route=GET /users"]]
        :put ck_entry_point {symbol_id, kind => metadata}
        """)) { }
    // cross-community fixture: the Program.cs chain spans two communities; the Api.cs chain has no membership.
    using (var seedMembers = ckProcessDb.Run(
        """
        ?[symbol_id, community_id] <- [["sym:main", "community:001"], ["sym:pa", "community:001"], ["sym:pb", "community:002"], ["sym:pc", "community:002"]]
        :put ck_member {symbol_id => community_id}
        """)) { }

    static string DescribeProcesses(ProcessExtractionResult result) =>
        string.Join(";", result.EntryPoints.Select(e => $"{e.SymbolId}|{e.Kind}|{e.Metadata}"))
        + "//"
        + string.Join(";", result.Processes.Select(p => $"{p.ProcessId}|{p.Name}|{p.EntrySymbolId}|{p.EntryKind}|{p.ProcessType}|{p.StepCount}"))
        + $"//dropped={result.DroppedProcesses};truncatedSteps={result.TruncatedSteps};truncatedProcesses={result.TruncatedProcesses}";

    // deterministic: two consecutive runs (the second over already-persisted state, exercising the
    // ck_entry_point merge) produce identical output.
    var processRun1 = await ckProcessOm.ExtractProcessesAsync();
    var processRun2 = await ckProcessOm.ExtractProcessesAsync();
    Assert(DescribeProcesses(processRun1) == DescribeProcesses(processRun2),
        "two consecutive process extraction runs should produce identical output");

    // Entry detection + canonical ordering: main > http_route > public_api (symbolId asc within kind).
    Assert(processRun1.EntryPoints.Select(e => (e.SymbolId, e.Kind)).SequenceEqual(
        [("sym:main", "main"), ("sym:route", "http_route"), ("sym:pub1", "public_api"), ("sym:pub2", "public_api")]),
        "entry points should be detected (main graph-level, http_route from the seeded candidate, public_api from exported symbols) in canonical order");
    Assert(processRun1.EntryPoints[1].Metadata == "route=GET /users",
        "externally seeded http_route candidate metadata should be preserved across the ck_entry_point rebuild");

    // main-entry: kind=main process with steps numbered in BFS first-reach order.
    var mainProcess = processRun1.Processes[0];
    Assert(mainProcess.ProcessId == "process:001" && mainProcess.EntrySymbolId == "sym:main" && mainProcess.EntryKind == "main",
        "the main entry should produce the canonical first process");
    Assert(mainProcess.Name == "Main (Program.cs)", "process name should combine entry symbol name and file short name");
    var mainSteps = await ckProcessOm.GetProcessStepsAsync("process:001");
    Assert(mainSteps.Select(s => (s.Step, s.SymbolId)).SequenceEqual(
        [(0, "sym:main"), (1, "sym:pa"), (2, "sym:pb"), (3, "sym:pc")]),
        "main process steps should be numbered by BFS first-reach order starting at the entry (step 0)");
    Assert(mainSteps[0].ViaKind == "" && mainSteps.Skip(1).All(s => s.ViaKind == "CALLS"),
        "entry step should have empty via_kind and traversed steps via_kind=CALLS");

    // cycle-safe: the A->B->A back edge terminates; each symbol appears exactly once.
    Assert(mainSteps.Count == 4 && mainSteps.Select(s => s.SymbolId).Distinct(StringComparer.Ordinal).Count() == 4,
        "cyclic CALLS edges should terminate and produce each symbol at most once per process");

    // MinConfidence: the 0.5-confidence StepC -> MapUsers edge is not traversed at the default 0.7.
    Assert(mainProcess.StepCount == 4, "edges below MinConfidence should not extend the process");

    // cross-community-marked: the main chain spans community:001 and community:002.
    Assert(mainProcess.ProcessType == "main,cross_community",
        "processes whose steps span two or more communities should carry the cross_community marker");

    // route-entry process: no cross-community marker (no ck_member rows for the chain).
    var routeProcess = processRun1.Processes[1];
    Assert(routeProcess.ProcessId == "process:002" && routeProcess.EntrySymbolId == "sym:route"
        && routeProcess.EntryKind == "http_route" && routeProcess.ProcessType == "http_route" && routeProcess.StepCount == 3,
        "the seeded http_route candidate should be traversed as an http_route process");

    // public_api process (pub1 reaches the shared chain and crosses communities via ck_member rows).
    var pubProcess = processRun1.Processes[2];
    Assert(pubProcess.ProcessId == "process:003" && pubProcess.EntrySymbolId == "sym:pub1"
        && pubProcess.EntryKind == "public_api" && pubProcess.ProcessType == "public_api,cross_community" && pubProcess.StepCount == 4,
        "exported public methods should be traversed as public_api processes");

    // min-steps-dropped: pub2 reaches only 2 steps (< MinSteps=3) and is dropped and counted.
    Assert(processRun1.Processes.Count == 3 && processRun1.DroppedProcesses == 1,
        "entry points whose reachable chain is shorter than MinSteps should be dropped and counted");
    Assert(processRun1.TruncatedSteps == 0 && processRun1.TruncatedProcesses == 0,
        "no truncation diagnostics expected under default budgets");

    // Persistence: ck_process / ck_process_step / ck_entry_point reflect the run.
    using var ckProcessRows = ckProcessDb.Run("?[process_id] := *ck_process{ process_id }");
    Assert(Rows(ckProcessRows).GetArrayLength() == 3, "kept processes should be persisted to ck_process");
    using var ckStepRows = ckProcessDb.Run("?[process_id, step] := *ck_process_step{ process_id, step }");
    Assert(Rows(ckStepRows).GetArrayLength() == 4 + 3 + 4, "all steps of kept processes should be persisted to ck_process_step");
    using var ckEntryRows = ckProcessDb.Run("?[symbol_id, kind] := *ck_entry_point{ symbol_id, kind }");
    Assert(Rows(ckEntryRows).GetArrayLength() == 4, "detected + externally seeded entry points should be persisted to ck_entry_point");

    // Public query surface reads back the persisted state.
    var listedProcesses = await ckProcessOm.ListProcessesAsync();
    Assert(listedProcesses.SequenceEqual(processRun1.Processes), "ListProcessesAsync should return persisted processes in canonical order");
    var processesForShared = await ckProcessOm.FindProcessesForSymbolAsync("sym:pa");
    Assert(processesForShared.Select(p => p.ProcessId).SequenceEqual(["process:001", "process:003"]),
        "FindProcessesForSymbolAsync should return every process containing the symbol as a step");
    var processesForR2 = await ckProcessOm.FindProcessesForSymbolAsync("sym:r2");
    Assert(processesForR2.Select(p => p.ProcessId).SequenceEqual(["process:002"]),
        "FindProcessesForSymbolAsync should resolve step membership through ck_process_step");
    Assert((await ckProcessOm.GetProcessStepsAsync("process:missing")).Count == 0,
        "GetProcessStepsAsync should return an empty list for unknown processes");

    // budget-and-truncate (maxSteps): MaxStepsPerProcess=3 cuts the two 4-step chains and counts them.
    var maxStepsRun = await ckProcessOm.ExtractProcessesAsync(new ProcessExtractionOptions(MaxStepsPerProcess: 3));
    Assert(maxStepsRun.Processes[0].StepCount == 3 && maxStepsRun.TruncatedSteps == 2,
        "processes exceeding MaxStepsPerProcess should be truncated and counted");
    Assert((await ckProcessOm.GetProcessStepsAsync("process:001")).Select(s => s.SymbolId).SequenceEqual(["sym:main", "sym:pa", "sym:pb"]),
        "truncated processes should persist exactly the first MaxStepsPerProcess steps");

    // budget-and-truncate (maxProcesses): MaxProcesses=2 keeps main + http_route and cuts the
    // public_api tail without traversing it.
    var budgetRun = await ckProcessOm.ExtractProcessesAsync(new ProcessExtractionOptions(MaxProcesses: 2));
    Assert(budgetRun.Processes.Count == 2 && budgetRun.TruncatedProcesses == 2 && budgetRun.DroppedProcesses == 0,
        "entry points beyond the MaxProcesses budget should be truncated from the public_api tail and counted");
    Assert(budgetRun.Processes.Select(p => p.EntryKind).SequenceEqual(["main", "http_route"]),
        "budget truncation should keep entries by kind priority main > http_route > mcp_tool > public_api");
    using var ckBudgetRows = ckProcessDb.Run("?[process_id] := *ck_process{ process_id }");
    Assert(Rows(ckBudgetRows).GetArrayLength() == 2, "budget-truncated processes should not be persisted");

    // MinConfidence override: at 0.5 the StepC -> MapUsers edge becomes traversable.
    var lowConfidenceRun = await ckProcessOm.ExtractProcessesAsync(new ProcessExtractionOptions(MinConfidence: 0.5));
    Assert(lowConfidenceRun.Processes[0].StepCount == 5,
        "lowering MinConfidence should extend traversal across lower-confidence CALLS edges");

    // EntryPointKinds filters traversal only; detection still persists all entry points.
    var mainOnlyRun = await ckProcessOm.ExtractProcessesAsync(new ProcessExtractionOptions(EntryPointKinds: ["main"]));
    Assert(mainOnlyRun.Processes.Count == 1 && mainOnlyRun.Processes[0].EntryKind == "main" && mainOnlyRun.EntryPoints.Count == 4,
        "EntryPointKinds should restrict traversal to the selected kinds without narrowing entry-point detection");

    // Zero-process run: filtering to a kind with no entries clears ck_process/ck_process_step
    // via an empty :replace without erroring.
    var emptyRun = await ckProcessOm.ExtractProcessesAsync(new ProcessExtractionOptions(EntryPointKinds: ["mcp_tool"]));
    Assert(emptyRun.Processes.Count == 0 && emptyRun.EntryPoints.Count == 4,
        "a traversal-kind filter with no matching entries should yield zero processes");
    using var ckEmptyRows = ckProcessDb.Run("?[process_id] := *ck_process{ process_id }");
    Assert(Rows(ckEmptyRows).GetArrayLength() == 0, "a zero-process run should clear previously persisted processes");

    // Top-level-statement main: a symbol whose sym_key contains <Main>$ is detected as main.
    using (var seedTopLevel = ckProcessDb.Run(
        """
        ?[symbol_id, file_id, name, kind, visibility, sym_key] <- [["sym:toplevel", "file:prog", "<Main>$", "method", "private", "csharp:Demo.Program.<Main>$/0"]]
        :put ck_symbol {symbol_id => file_id, name, kind, visibility, sym_key}
        """)) { }
    var topLevelRun = await ckProcessOm.ExtractProcessesAsync();
    Assert(topLevelRun.EntryPoints.Any(e => e.SymbolId == "sym:toplevel" && e.Kind == "main"),
        "top-level-statement symbols (sym_key containing <Main>$) should be detected as main entries");
}

// --- DeepImpact 与 SymbolContext 富化 (track deepen-llm-wiki-context-impact T1.2) ---

// T1.2-AC1 (delta.xml suite "deep" first 5 cases at the API layer): impact-layered,
// impact-min-confidence, risk-rating, layer-bounded, context-with-processes; plus direction
// semantics (decisions #1), affected-process bounding and run-to-run determinism.
using (var ckDeepDb = new CozoDb(engine: "mem", path: ""))
{
    var ckDeepOm = new CozoOm(ckDeepDb);
    await ckDeepOm.InitCodeKnowledgeAsync();

    // Fixture:
    // - chain: dc calls db, db calls da (conf 1.0) → impact-layered / direction cases.
    // - weak chain: ww calls wa at confidence 0.5 → impact-min-confidence case.
    // - hotspot: 12 callers of sym:hot (conf 1.0) + 3 seeded processes containing it → CRITICAL,
    //   layer-bounded; sym:hot2 has 10 callers but no processes → HIGH; sym:low has 2 → LOW.
    var deepSymbols = new List<CodeSymbolFact>
    {
        new("sym:da", "file:deep", "A", "method", 1, 10, "A()"),
        new("sym:db", "file:deep", "B", "method", 11, 20, "B()"),
        new("sym:dc", "file:deep", "C", "method", 21, 30, "C()"),
        new("sym:wa", "file:deep", "WeakTarget", "method", 31, 40, "WeakTarget()"),
        new("sym:ww", "file:deep", "WeakCaller", "method", 41, 50, "WeakCaller()"),
        new("sym:hot", "file:deep", "Hot", "method", 51, 60, "Hot()"),
        new("sym:hot2", "file:deep", "Hot2", "method", 61, 70, "Hot2()"),
        new("sym:low", "file:deep", "LowRisk", "method", 71, 80, "LowRisk()"),
    };
    var deepEdges = new List<CodeEdgeFact>
    {
        new("sym:dc", "sym:db", CodeEdgeKinds.Calls, "file:deep", 25, 1.0, "roslyn", "C calls B"),
        new("sym:db", "sym:da", CodeEdgeKinds.Calls, "file:deep", 15, 1.0, "roslyn", "B calls A"),
        new("sym:ww", "sym:wa", CodeEdgeKinds.Calls, "file:deep", 45, 0.5, "treesitter", "weak call"),
    };
    for (var i = 1; i <= 12; i++)
    {
        deepSymbols.Add(new CodeSymbolFact($"sym:hc{i:00}", "file:deep", $"HotCaller{i:00}", "method", 100 + i * 10, 105 + i * 10, $"HotCaller{i:00}()"));
        deepEdges.Add(new CodeEdgeFact($"sym:hc{i:00}", "sym:hot", CodeEdgeKinds.Calls, "file:deep", 100 + i * 10, 1.0, "roslyn", ""));
    }

    for (var i = 1; i <= 10; i++)
    {
        deepSymbols.Add(new CodeSymbolFact($"sym:h2c{i:00}", "file:deep", $"Hot2Caller{i:00}", "method", 300 + i * 10, 305 + i * 10, $"Hot2Caller{i:00}()"));
        deepEdges.Add(new CodeEdgeFact($"sym:h2c{i:00}", "sym:hot2", CodeEdgeKinds.Calls, "file:deep", 300 + i * 10, 1.0, "roslyn", ""));
    }

    for (var i = 1; i <= 2; i++)
    {
        deepSymbols.Add(new CodeSymbolFact($"sym:lc{i:00}", "file:deep", $"LowCaller{i:00}", "method", 500 + i * 10, 505 + i * 10, $"LowCaller{i:00}()"));
        deepEdges.Add(new CodeEdgeFact($"sym:lc{i:00}", "sym:low", CodeEdgeKinds.Calls, "file:deep", 500 + i * 10, 1.0, "roslyn", ""));
    }

    await ckDeepOm.IndexCodeKnowledgeAsync(new CodeKnowledgeBatch(
        Files: [new CodeFileFact("file:deep", "repo:deep", "src/Deep.cs", "csharp")],
        Symbols: deepSymbols,
        Edges: deepEdges));

    // Seed 3 processes containing sym:hot (hc01 also appears in the first → union dedupes),
    // plus community membership for sym:hot.
    using (var seedDeepProcesses = ckDeepDb.Run(
        """
        ?[process_id, name, entry_symbol_id, entry_kind, process_type, step_count] <- [
          ["process:d01", "Flow1", "sym:hc01", "main", "main", 2],
          ["process:d02", "Flow2", "sym:hc02", "public_api", "public_api", 2],
          ["process:d03", "Flow3", "sym:hc03", "public_api", "public_api", 2]]
        :put ck_process {process_id => name, entry_symbol_id, entry_kind, process_type, step_count}
        """)) { }
    using (var seedDeepSteps = ckDeepDb.Run(
        """
        ?[process_id, step, symbol_id, via_kind] <- [
          ["process:d01", 0, "sym:hc01", ""], ["process:d01", 1, "sym:hot", "CALLS"],
          ["process:d02", 0, "sym:hc02", ""], ["process:d02", 1, "sym:hot", "CALLS"],
          ["process:d03", 0, "sym:hc03", ""], ["process:d03", 1, "sym:hot", "CALLS"]]
        :put ck_process_step {process_id, step => symbol_id, via_kind}
        """)) { }
    using (var seedDeepCommunity = ckDeepDb.Run(
        """
        ?[community_id, label, cohesion, symbol_count, algo] <- [["community:d01", "Demo.Hot", 1.0, 13, "louvain"]]
        :put ck_community {community_id => label, cohesion, symbol_count, algo}
        """)) { }
    using (var seedDeepMembers = ckDeepDb.Run(
        """
        ?[symbol_id, community_id] <- [["sym:hot", "community:d01"]]
        :put ck_member {symbol_id => community_id}
        """)) { }

    // impact-layered (delta case 2): impact A, direction=up, maxDepth=2 → layer1={B}, layer2={C}.
    var layered = await ckDeepOm.DeepImpactAsync("sym:da", new DeepImpactOptions(Direction: ImpactDirection.Up, MaxDepth: 2));
    Assert(layered.Layers.Count == 2, "up-impact of the chain head should produce exactly two layers");
    Assert(layered.Layers[0].Depth == 1 && layered.Layers[0].Symbols.Select(s => s.SymbolId).SequenceEqual(["sym:db"]),
        "layer 1 should contain exactly the direct caller B");
    Assert(layered.Layers[1].Depth == 2 && layered.Layers[1].Symbols.Select(s => s.SymbolId).SequenceEqual(["sym:dc"]),
        "layer 2 should contain exactly the transitive caller C");
    Assert(layered.Layers[0].Symbols[0].Name == "B" && layered.Layers[0].Symbols[0].Path == "src/Deep.cs"
        && layered.Layers[0].Symbols[0].Line == 11 && Math.Abs(layered.Layers[0].Symbols[0].Confidence - 1.0) < 1e-9,
        "layer symbols should be decorated with name/file/line and the best traversal-edge confidence");
    Assert(layered.Risk == RiskLevel.Low, "one direct caller should rate LOW");

    // direction=down (decisions #1): impact C downwards → my callees, layer1={B}, layer2={A}.
    var downward = await ckDeepOm.DeepImpactAsync("sym:dc", new DeepImpactOptions(Direction: ImpactDirection.Down));
    Assert(downward.Direction == ImpactDirection.Down
        && downward.Layers.Count == 2
        && downward.Layers[0].Symbols.Select(s => s.SymbolId).SequenceEqual(["sym:db"])
        && downward.Layers[1].Symbols.Select(s => s.SymbolId).SequenceEqual(["sym:da"]),
        "down-impact should walk CALLS edges forward (my transitive callees)");

    // impact-min-confidence (delta case 3): the 0.5-confidence path is excluded at 0.8.
    var weakDefault = await ckDeepOm.DeepImpactAsync("sym:wa");
    Assert(weakDefault.Layers.Count == 1 && weakDefault.Layers[0].Symbols.Single().SymbolId == "sym:ww",
        "default MinConfidence=0.0 should traverse the 0.5-confidence caller edge");
    var weakFiltered = await ckDeepOm.DeepImpactAsync("sym:wa", new DeepImpactOptions(MinConfidence: 0.8));
    Assert(weakFiltered.Layers.Count == 0 && weakFiltered.Risk == RiskLevel.Low,
        "MinConfidence=0.8 should exclude the path that only exists via a 0.5-confidence edge");

    // risk-rating (delta case 4): 12 direct callers + >= 3 processes → CRITICAL; 2 callers → LOW;
    // 10 callers without process participation → HIGH (CRITICAL gate needs the flows).
    var hotImpact = await ckDeepOm.DeepImpactAsync("sym:hot");
    Assert(hotImpact.Layers[0].Total == 12 && hotImpact.Risk == RiskLevel.Critical,
        "a symbol with 10+ direct callers participating in >= 3 execution flows should rate CRITICAL");
    var hot2Impact = await ckDeepOm.DeepImpactAsync("sym:hot2");
    Assert(hot2Impact.Layers[0].Total == 10 && hot2Impact.Risk == RiskLevel.High,
        "10+ direct callers without >= 3 execution flows should stay HIGH");
    var lowImpact = await ckDeepOm.DeepImpactAsync("sym:low");
    Assert(lowImpact.Layers[0].Total == 2 && lowImpact.Risk == RiskLevel.Low,
        "a symbol with 2 direct callers should rate LOW");

    // MEDIUM band boundary: 4-9 direct callers.
    Assert((await ckDeepOm.DeepImpactAsync("sym:hot", new DeepImpactOptions(MaxPerLayer: 4))).Risk == RiskLevel.Critical,
        "risk should be computed from the real layer total, not the truncated symbol list");

    // layer-bounded (delta case 5): MaxPerLayer=5 truncates the 12-caller layer, keeping the real total.
    var bounded = await ckDeepOm.DeepImpactAsync("sym:hot", new DeepImpactOptions(MaxPerLayer: 5));
    Assert(bounded.Layers[0].Symbols.Count == 5 && bounded.Layers[0].Total == 12 && bounded.Layers[0].Truncated == 7,
        "layers exceeding MaxPerLayer should be truncated and carry the truncation count with the real total");
    Assert(bounded.Layers[0].Symbols.Select(s => s.SymbolId).SequenceEqual(
        ["sym:hc01", "sym:hc02", "sym:hc03", "sym:hc04", "sym:hc05"]),
        "truncated layers should keep the first MaxPerLayer symbols in sorted order");

    // AffectedProcesses: flows of root + layer members, deduplicated (hc01 is both a layer member
    // and an entry of process:d01), canonical order, bounded at 20.
    Assert(hotImpact.AffectedProcesses.Select(p => p.ProcessId).SequenceEqual(["process:d01", "process:d02", "process:d03"])
        && hotImpact.TruncatedProcesses == 0,
        "affected processes should be the deduplicated flows of the root and layer members in canonical order");
    Assert(lowImpact.AffectedProcesses.Count == 0,
        "symbols outside every execution flow should report no affected processes");

    // MaxDepth clamps to 1..16.
    var clamped = await ckDeepOm.DeepImpactAsync("sym:da", new DeepImpactOptions(MaxDepth: 0));
    Assert(clamped.Layers.Count == 1, "MaxDepth below 1 should clamp to a single layer");

    // determinism: two runs produce identical output.
    static string DescribeDeepImpact(DeepImpactResult result) =>
        $"{result.RootId}|{result.Direction}|{result.Risk}|{result.TruncatedProcesses}//"
        + string.Join(";", result.Layers.Select(l =>
            $"{l.Depth}:{l.Total}:{l.Truncated}:" + string.Join(",", l.Symbols.Select(s => $"{s.SymbolId}@{s.Path}:{s.Line}~{s.Confidence}"))))
        + "//" + string.Join(",", result.AffectedProcesses.Select(p => p.ProcessId));
    Assert(DescribeDeepImpact(await ckDeepOm.DeepImpactAsync("sym:hot", new DeepImpactOptions(MaxPerLayer: 5)))
        == DescribeDeepImpact(await ckDeepOm.DeepImpactAsync("sym:hot", new DeepImpactOptions(MaxPerLayer: 5))),
        "two DeepImpactAsync runs over the same graph should produce identical output");

    // context-with-processes (delta case 1): symbol_context carries execution flows + community
    // + per-kind edge counts (add-only fields).
    var hotContext = await ckDeepOm.FindSymbolContextAsync("sym:hot");
    Assert(hotContext.Processes.Select(p => p.ProcessId).SequenceEqual(["process:d01", "process:d02", "process:d03"]),
        "symbol context should list the execution flows the symbol participates in (canonical order)");
    Assert(hotContext.CommunityId == "community:d01" && hotContext.CommunityLabel == "Demo.Hot",
        "symbol context should carry the symbol's community id and label");
    Assert(hotContext.IncomingByKind.GetValueOrDefault("CALLS") == 12 && hotContext.OutgoingByKind.Count == 0,
        "symbol context should aggregate incoming/outgoing edge counts per kind");

    // Empty enrichment sources stay empty without throwing (add-only, non-breaking).
    var plainContext = await ckDeepOm.FindSymbolContextAsync("sym:da");
    Assert(plainContext.Processes.Count == 0 && plainContext.CommunityId == "" && plainContext.CommunityLabel == "",
        "symbols outside processes/communities should report empty enrichment fields");
    Assert(plainContext.IncomingByKind.GetValueOrDefault("CALLS") == 1 && plainContext.OutgoingByKind.Count == 0,
        "per-kind counts should reflect the direct relations");
}

// === DEPA ontology & configuration (track add-llm-wiki-depa-ontology, T1.1) ===
{
    using var depaDb = new CozoDb(engine: "mem", path: "");
    var depaOm = new CozoOm(depaDb);

    // --- init idempotent (delta case init-idempotent): two runs, no error, no duplicates ---
    await depaOm.InitDepaOntologyAsync();
    await depaOm.InitDepaOntologyAsync();

    string[] expectedDepaTypes =
    [
        "depa_capsule", "depa_contract", "depa_impl", "depa_reducer", "depa_projection",
        "depa_fact_source", "depa_runtime_param", "depa_runtime_carrier", "depa_entry",
        "depa_effect_api", "depa_violation",
    ];
    var depaTypes = await depaOm.GetDescendantsAsync("depa_node");
    Assert(depaTypes.Count == expectedDepaTypes.Length && expectedDepaTypes.All(depaTypes.Contains),
        "depa_node should have exactly the 11 DEPA subtypes after double init (idempotent, no duplicates)");
    Assert(await depaOm.IsSubtypeOfAsync("depa_violation", "depa_node"),
        "every depa_* type should inherit from the depa_node root");

    // Common attributes live on the root and are inherited; specific attributes on the subtype.
    var violationAttrs = await depaOm.GetAttributeDefinitionsAsync("depa_violation");
    foreach (var attr in new[] { "symbol_id", "sym_key", "path", "line", "assigned_by", "confidence", "rule_id", "verdict", "dimension", "message", "evidence_json" })
    {
        Assert(violationAttrs.ContainsKey(attr), $"depa_violation should expose inherited common + own attribute '{attr}'");
    }
    var factSourceAttrs = await depaOm.GetAttributeDefinitionsAsync("depa_fact_source");
    Assert(factSourceAttrs.ContainsKey("grade") && factSourceAttrs.ContainsKey("grade_id") && factSourceAttrs.ContainsKey("expected_owner"),
        "depa_fact_source should carry grade/grade_id/expected_owner");
    var capsuleAttrs = await depaOm.GetAttributeDefinitionsAsync("depa_capsule");
    Assert(capsuleAttrs.ContainsKey("name") && capsuleAttrs.ContainsKey("root_path") && capsuleAttrs.ContainsKey("internals_path"),
        "depa_capsule should carry name/root_path/internals_path");

    string[] expectedDepaRelations =
    [
        "capsule_contains", "capsule_exposes", "capsule_depends_on", "contract_implemented_by",
        "impl_uses_contract", "entry_delegates_to", "runtime_carries", "fn_takes",
        "projection_derived_from", "reducer_folds", "fact_written_by",
        "effect_leaks_through", "backwrites", "violates",
    ];
    foreach (var rel in expectedDepaRelations)
    {
        Assert(await depaOm.ResolveRelationAsync(rel) == rel, $"DEPA relation '{rel}' should be defined");
    }

    var depaRules = (await depaOm.ListExistentialRulesAsync()).Select(r => r.RuleName).ToArray();
    string[] expectedDepaRules =
    [
        "capsule_must_expose_entry", "contract_must_have_impl", "factsource_must_have_writer",
        "projection_must_have_upstream", "violation_must_have_subject",
    ];
    Assert(depaRules.SequenceEqual(expectedDepaRules),
        "exactly the five DEPA existential rules should exist after double init (idempotent)");

    // --- existential rules behave per design §2.3 (Check mode) ---
    await depaOm.UpsertEntityAsync("depa:capsule:Sample", "depa_capsule", "Sample");
    await depaOm.SetPropertyAsync("depa:capsule:Sample", "name", "Sample");
    await depaOm.UpsertEntityAsync("depa:contract:IEffect", "depa_contract", "IEffect");
    await depaOm.SetPropertyAsync("depa:contract:IEffect", "contract_kind", "effect");
    await depaOm.UpsertEntityAsync("depa:contract:Dto", "depa_contract", "Dto");
    await depaOm.SetPropertyAsync("depa:contract:Dto", "contract_kind", "types");
    await depaOm.UpsertEntityAsync("depa:factsource:High", "depa_fact_source", "High");
    await depaOm.SetPropertyAsync("depa:factsource:High", "grade", 2);
    await depaOm.UpsertEntityAsync("depa:factsource:View", "depa_fact_source", "View");
    await depaOm.SetPropertyAsync("depa:factsource:View", "grade", 6);

    var depaCheck = await depaOm.CheckExistentialRulesAsync();
    Assert(depaCheck.Any(v => v.Rule == "capsule_must_expose_entry" && v.EntityId == "depa:capsule:Sample"),
        "capsule without an exposed entry should violate capsule_must_expose_entry");
    Assert(depaCheck.Any(v => v.Rule == "contract_must_have_impl" && v.EntityId == "depa:contract:IEffect"),
        "effect contract without impl should violate contract_must_have_impl");
    Assert(!depaCheck.Any(v => v.Rule == "contract_must_have_impl" && v.EntityId == "depa:contract:Dto"),
        "contract_must_have_impl should only apply where contract_kind=effect");
    Assert(depaCheck.Any(v => v.Rule == "factsource_must_have_writer" && v.EntityId == "depa:factsource:High"),
        "grade<=3 fact source without writer should violate factsource_must_have_writer");
    Assert(!depaCheck.Any(v => v.Rule == "factsource_must_have_writer" && v.EntityId == "depa:factsource:View"),
        "factsource_must_have_writer should not apply above grade 3");

    await depaOm.UpsertEntityAsync("depa:entry:Run", "depa_entry", "Run");
    await depaOm.LinkEntitiesAsync("depa:capsule:Sample", "capsule_exposes", "depa:entry:Run");
    await depaOm.UpsertEntityAsync("depa:violation:v1", "depa_violation", "v1");
    await depaOm.LinkEntitiesAsync("depa:violation:v1", "violates", "depa:capsule:Sample");
    var depaRecheck = await depaOm.CheckExistentialRulesAsync();
    Assert(!depaRecheck.Any(v => v.Rule == "capsule_must_expose_entry" && v.EntityId == "depa:capsule:Sample"),
        "capsule with a capsule_exposes edge should satisfy capsule_must_expose_entry");
    Assert(!depaRecheck.Any(v => v.Rule == "violation_must_have_subject" && v.EntityId == "depa:violation:v1"),
        "violates edge to any depa_node subtype should satisfy violation_must_have_subject");

    // --- built-in whitelist glob matching (design §4.1), table driven ---
    var builtIn = DepaEffectCatalog.BuiltIn;
    (string Target, string? Category, string? Direction)[] globCases =
    [
        ("System.IO.File.ReadAllText", "file_io", "read"), // System.IO.File.Read* wins over System.IO.**
        ("System.IO.File.WriteAllText", "file_io", "both"),
        ("System.Net.Http.HttpClient.GetAsync", "network", "both"),
        ("System.Data.SqlClient.SqlCommand.ExecuteReader", "db", "both"),
        ("Microsoft.Data.Sqlite.SqliteConnection.Open", "db", "both"),
        ("System.Diagnostics.Process.Start", "process", "both"),
        ("System.Console.WriteLine", "console", "write"),
        ("System.Environment.GetEnvironmentVariable", "env", "read"),
        ("System.Random.Next", "nondeterminism", "read"),
        ("System.DateTime.Now", "nondeterminism", "read"),
        ("System.DateTime.UtcNow", null, null),
        ("MyCompany.Domain.Service.Do", null, null),
    ];
    foreach (var globCase in globCases)
    {
        var hit = DepaEffectCatalog.Match(builtIn, globCase.Target);
        Assert(hit?.Category == globCase.Category && hit?.Direction == globCase.Direction,
            $"built-in whitelist should classify '{globCase.Target}' as {globCase.Category ?? "<no match>"}/{globCase.Direction ?? "-"} (got {hit?.Category ?? "<null>"}/{hit?.Direction ?? "-"})");
    }
    Assert(DepaEffectCatalog.GlobMatch("A.*.C", "A.B.C") && !DepaEffectCatalog.GlobMatch("A.*.C", "A.B.X.C"),
        "'*' should match exactly one dotted segment");
    Assert(DepaEffectCatalog.GlobMatch("A.**.C", "A.B.X.C") && DepaEffectCatalog.GlobMatch("A.**", "A.B.C.D"),
        "'**' should match any number of dotted segments");
    Assert(!DepaEffectCatalog.GlobMatch("A.*", "A.B.C") && !DepaEffectCatalog.GlobMatch("A.B", "A.B.C"),
        "patterns without '**' should not match deeper targets");

    // --- user effects file: append + override, first hit wins (design §4.1) ---
    var depaTmp = Directory.CreateTempSubdirectory("depa-tests-");
    try
    {
        var effectsPath = Path.Combine(depaTmp.FullName, "depa-effects.json");
        File.WriteAllText(effectsPath, """
        {
          "version": 1,
          "effects": [
            { "pattern": "Cozo.DotNet.ICozoOmStore.**", "category": "exempt_contract" },
            { "pattern": "System.Console.**", "category": "exempt_contract", "direction": "both" },
            { "pattern": "MyIo.**", "category": "file_io", "direction": "write" }
          ]
        }
        """);
        var userEffects = DepaEffectCatalog.LoadUserEffects(effectsPath);
        Assert(userEffects.Count == 3 && userEffects[0].Direction == "both",
            "user effects should load in array order with direction defaulting to 'both'");
        Assert(DepaEffectCatalog.LoadUserEffects(Path.Combine(depaTmp.FullName, "missing.json")).Count == 0,
            "missing depa-effects.json should yield an empty list, not an error");

        var merged = DepaEffectCatalog.Merge(userEffects);
        Assert(DepaEffectCatalog.Match(merged, "System.Console.WriteLine")?.Category == "exempt_contract",
            "user entry with same pattern should override the built-in (user entries match first)");
        Assert(DepaEffectCatalog.Match(merged, "Cozo.DotNet.ICozoOmStore.RunAsync")?.Category == "exempt_contract",
            "user exempt_contract entries should classify framework contract calls as exempt");
        var myIo = DepaEffectCatalog.Match(merged, "MyIo.Disk.Write");
        Assert(myIo?.Category == "file_io" && myIo.Direction == "write",
            "appended user entries should classify their own targets");
        var readHit = DepaEffectCatalog.Match(merged, "System.IO.File.ReadAllText");
        Assert(readHit?.Category == "file_io" && readHit.Direction == "read",
            "built-in entries not overridden by the user should still match after the user block");
        Assert(merged.Count == DepaEffectCatalog.BuiltIn.Count + 2,
            "merge should dedupe identical patterns (override) and append new ones");

        // --- depa_effect_api materialization (SyncEffectApisAsync), idempotent upsert ---
        var builtinSynced = await depaOm.SyncEffectApisAsync();
        Assert(builtinSynced == DepaEffectCatalog.BuiltIn.Count
            && (await depaOm.FindByTypeAsync("depa_effect_api")).Count == DepaEffectCatalog.BuiltIn.Count,
            "sync without a user file should materialize exactly the built-in whitelist");

        var synced = await depaOm.SyncEffectApisAsync(effectsPath);
        var effectApis = await depaOm.FindByTypeAsync("depa_effect_api");
        Assert(synced == DepaEffectCatalog.BuiltIn.Count + 2 && effectApis.Count == DepaEffectCatalog.BuiltIn.Count + 2,
            "sync with a user file should upsert overridden patterns in place and append new ones");
        const string consoleApiId = "depa:effectapi:System.Console.**";
        Assert(AsString(await depaOm.GetPropertyAsync(consoleApiId, "category")) == "exempt_contract"
            && AsString(await depaOm.GetPropertyAsync(consoleApiId, "target_pattern")) == "System.Console.**"
            && AsString(await depaOm.GetPropertyAsync(consoleApiId, "assigned_by")) == "config",
            "materialized depa_effect_api should carry target_pattern/category/assigned_by, with user override applied");
        Assert(AsString(await depaOm.GetPropertyAsync("depa:effectapi:MyIo.**", "direction")) == "write",
            "materialized user entry should carry its direction");

        var syncedAgain = await depaOm.SyncEffectApisAsync(effectsPath);
        Assert(syncedAgain == synced && (await depaOm.FindByTypeAsync("depa_effect_api")).Count == effectApis.Count,
            "repeated sync should be an idempotent upsert (no duplicate entities)");

        // --- depa-map.json parsing (design §4.3): full / missing / partial ---
        var mapPath = Path.Combine(depaTmp.FullName, "depa-map.json");
        File.WriteAllText(mapPath, """
        {
          "capsules": [
            { "name": "Om.Core", "rootPath": "src/Om.Core", "internalsGlob": "src/Om.Core/Internals/**" },
            { "name": "Om.Depa", "rootPath": "src/Om.Depa" }
          ],
          "contractPackages": ["src/Om.Core/Contracts"],
          "runtimeCarrierTypes": ["CozoOmRuntime"],
          "factSources": [
            { "symbolOrPath": "CozoOmRuntime.SchemaCache", "grade": 6, "expectedOwner": "depa:impl:SchemaLogic" },
            { "symbolOrPath": "om_entity", "grade": 1 }
          ]
        }
        """);
        var depaMap = DepaMapConfig.Load(mapPath);
        Assert(depaMap.Capsules.Count == 2
            && depaMap.Capsules[0].Name == "Om.Core" && depaMap.Capsules[0].RootPath == "src/Om.Core"
            && depaMap.Capsules[0].InternalsGlob == "src/Om.Core/Internals/**"
            && depaMap.Capsules[1].InternalsGlob == "**/Internals/**",
            "depa-map capsules should parse name/rootPath and default internalsGlob to **/Internals/**");
        Assert(depaMap.ContractPackages.SequenceEqual(["src/Om.Core/Contracts"])
            && depaMap.RuntimeCarrierTypes.SequenceEqual(["CozoOmRuntime"]),
            "depa-map contractPackages/runtimeCarrierTypes should parse");
        Assert(depaMap.FactSources.Count == 2
            && depaMap.FactSources[0].SymbolOrPath == "CozoOmRuntime.SchemaCache"
            && depaMap.FactSources[0].Grade == 6
            && depaMap.FactSources[0].ExpectedOwner == "depa:impl:SchemaLogic"
            && depaMap.FactSources[1].Grade == 1 && depaMap.FactSources[1].ExpectedOwner == "",
            "depa-map factSources should parse grade/expectedOwner with expectedOwner defaulting to empty");

        var missingMap = DepaMapConfig.Load(Path.Combine(depaTmp.FullName, "missing-map.json"));
        Assert(missingMap.Capsules.Count == 0 && missingMap.ContractPackages.Count == 0
            && missingMap.RuntimeCarrierTypes.Count == 0 && missingMap.FactSources.Count == 0,
            "missing depa-map.json should yield an empty config, not an error");

        var partialMapPath = Path.Combine(depaTmp.FullName, "partial-map.json");
        File.WriteAllText(partialMapPath, """{ "capsules": [ { "name": "X", "rootPath": "src/X" } ] }""");
        var partialMap = DepaMapConfig.Load(partialMapPath);
        Assert(partialMap.Capsules.Count == 1 && partialMap.ContractPackages.Count == 0
            && partialMap.RuntimeCarrierTypes.Count == 0 && partialMap.FactSources.Count == 0,
            "partial depa-map.json should fill absent sections with empty lists");
    }
    finally
    {
        depaTmp.Delete(recursive: true);
    }
}

// === DEPA observation layer & scan (track add-llm-wiki-depa-ontology, T2.1) ===
{
    // --- ck_external_call write path (design §4.2): aggregation rows, built-in whitelist
    // pre-classification at write time, explicit categories preserved, idempotent :put ---
    {
        using var extDb = new CozoDb(engine: "mem", path: "");
        var extOm = new CozoOm(extDb);
        await extOm.InitCodeKnowledgeAsync();
        var extBatch = new CodeKnowledgeBatch(ExternalCalls:
        [
            new CodeExternalCallFact("sym:core", "System.IO.File.WriteAllText", 2, FirstFileId: "file:core", FirstLine: 10, Resolver: "roslyn"),
            new CodeExternalCallFact("sym:core", "Custom.Backend.Send", 1, Category: "network", FirstFileId: "file:core", FirstLine: 12, Resolver: "roslyn"),
            new CodeExternalCallFact("sym:core", "MyCompany.Domain.Helper.Do", 1, FirstFileId: "file:core", FirstLine: 14, Resolver: "roslyn"),
        ]);
        var extResult = await extOm.IndexCodeKnowledgeAsync(extBatch);
        Assert(extResult.ExternalCalls == 3, "index result should count the external-call summary rows (add-only)");

        var extRows = await extOm.Runtime.Store.RunAsync(
            "?[caller_id, target_key, count, category, first_file_id, first_line, resolver] := *ck_external_call{ caller_id, target_key, count, category, first_file_id, first_line, resolver }");
        Assert(extRows.Rows.Count == 3, "every (caller, target) summary should land as one ck_external_call row");
        var writeAll = extRows.Rows.Single(r => r[1].GetString() == "System.IO.File.WriteAllText");
        Assert(writeAll[0].GetString() == "sym:core" && writeAll[2].GetInt32() == 2
            && writeAll[3].GetString() == "file_io" && writeAll[4].GetString() == "file:core"
            && writeAll[5].GetInt32() == 10 && writeAll[6].GetString() == "roslyn",
            "unclassified rows should be pre-classified against the built-in whitelist at write time (file_io) with count and first-call-site evidence intact");
        Assert(extRows.Rows.Single(r => r[1].GetString() == "Custom.Backend.Send")[3].GetString() == "network",
            "an explicit category supplied by the producer should be preserved, not re-classified");
        Assert(extRows.Rows.Single(r => r[1].GetString() == "MyCompany.Domain.Helper.Do")[3].GetString() == "",
            "targets outside the whitelist should stay unclassified (empty category), not guessed");

        await extOm.IndexCodeKnowledgeAsync(extBatch);
        var extRowsAgain = await extOm.Runtime.Store.RunAsync("?[caller_id, target_key] := *ck_external_call{ caller_id, target_key }");
        Assert(extRowsAgain.Rows.Count == 3, "re-indexing the same batch should upsert ck_external_call rows in place (idempotent)");
    }

    // Shared ck fixture for the scan cases: one capsule-shaped source tree with a Contracts/
    // interface, its implementation, a public_api entry that delegates to the impl, and a
    // grade-1 field written by one method.
    static CodeKnowledgeBatch DepaScanFixture() => new(
        Repositories: [new CodeRepositoryFact("repo:scan", "/repo/scan")],
        Files:
        [
            new CodeFileFact("file:f1", "repo:scan", "src/Om.Core/Contracts/IStore.cs", "csharp"),
            new CodeFileFact("file:f2", "repo:scan", "src/Om.Core/StoreImpl.cs", "csharp"),
            new CodeFileFact("file:f3", "repo:scan", "src/Om.Core/Api.cs", "csharp"),
        ],
        Symbols:
        [
            new CodeSymbolFact("sym:istore", "file:f1", "IStore", "interface", 3, 8, SymKey: "csharp:Demo.IStore#0"),
            new CodeSymbolFact("sym:impl", "file:f2", "StoreImpl", "class", 3, 30, SymKey: "csharp:Demo.StoreImpl#0"),
            new CodeSymbolFact("sym:save", "file:f2", "Save", "method", 12, 18, SymKey: "csharp:Demo.StoreImpl.Save#1"),
            new CodeSymbolFact("sym:cache", "file:f2", "SchemaCache", "field", 5, 5, SymKey: "csharp:Demo.StoreImpl.SchemaCache#0"),
            new CodeSymbolFact("sym:run", "file:f3", "Run", "method", 6, 12, SymKey: "csharp:Demo.Api.Run#0"),
        ],
        Edges:
        [
            new CodeEdgeFact("sym:impl", "sym:istore", CodeEdgeKinds.Implements, "file:f2", 3, 0.9, "treesitter"),
            new CodeEdgeFact("sym:run", "sym:impl", CodeEdgeKinds.Calls, "file:f3", 8, 1.0, "roslyn", "semantic"),
            new CodeEdgeFact("sym:save", "sym:cache", CodeEdgeKinds.Accesses, "file:f2", 14, 0.9, "treesitter", "write"),
        ],
        EntryPoints: [new CodeEntryPointFact("sym:run", "public_api")]);

    var scanTmp = Directory.CreateTempSubdirectory("depa-scan-tests-");
    try
    {
        // --- config-annotation (delta case): depa-map.json declares capsule + contract package →
        // depa_capsule/depa_contract (assigned_by=config) + structural relations materialize ---
        var scanMapPath = Path.Combine(scanTmp.FullName, "depa-map.json");
        File.WriteAllText(scanMapPath, """
        {
          "capsules": [ { "name": "Om.Core", "rootPath": "src/Om.Core" } ],
          "contractPackages": ["src/Om.Core/Contracts"],
          "factSources": [ { "symbolOrPath": "SchemaCache", "grade": 1 } ]
        }
        """);

        using var scanDb = new CozoDb(engine: "mem", path: "");
        var scanOm = new CozoOm(scanDb);
        await scanOm.InitCodeKnowledgeAsync();
        await scanOm.IndexCodeKnowledgeAsync(DepaScanFixture());

        var scanResult = await scanOm.DepaScanAsync(new DepaScanOptions(MapPath: scanMapPath));
        // capsule + contract + fact source (config channel) plus the impl/entry entities derived
        // through them inherit config; the fact-writer impl inferred from a write ACCESSES edge is
        // the single heuristic-grade judgement.
        Assert(scanResult.AnnotatedFromConfig == 5 && scanResult.AnnotatedFromHeuristic == 1,
            $"config channel should own the declared annotations and their derivations (got config={scanResult.AnnotatedFromConfig}, heuristic={scanResult.AnnotatedFromHeuristic})");

        const string capsuleId = "depa:capsule:Om.Core";
        const string contractId = "depa:contract:csharp:Demo.IStore#0";
        const string implId = "depa:impl:csharp:Demo.StoreImpl#0";
        const string entryId = "depa:entry:csharp:Demo.Api.Run#0";
        const string factId = "depa:factsource:csharp:Demo.StoreImpl.SchemaCache#0";
        Assert(AsString(await scanOm.GetPropertyAsync(capsuleId, "assigned_by")) == "config"
            && AsString(await scanOm.GetPropertyAsync(capsuleId, "root_path")) == "src/Om.Core",
            "declared capsule should materialize as depa_capsule with assigned_by=config");
        Assert(AsString(await scanOm.GetPropertyAsync(contractId, "assigned_by")) == "config"
            && AsString(await scanOm.GetPropertyAsync(contractId, "contract_kind")) == "effect"
            && AsString(await scanOm.GetPropertyAsync(contractId, "symbol_id")) == "sym:istore"
            && AsString(await scanOm.GetPropertyAsync(contractId, "path")) == "src/Om.Core/Contracts/IStore.cs",
            "contract-package interface should materialize as depa_contract (config, effect kind, ck anchor)");
        Assert(AsString(await scanOm.GetPropertyAsync(factId, "grade_id")) == "authoritative_fact"
            && (await scanOm.GetPropertyAsync(factId, "grade"))?.GetDouble() == 1,
            "graded fact source should carry grade + the fact-source-truth grade_id vocabulary");

        var containsOut = await scanOm.GetNeighborsAsync(capsuleId, "capsule_contains", OmDirection.Outgoing);
        Assert(containsOut.Outgoing.Select(n => n.EntityId).ToHashSet()
                .IsSupersetOf([contractId, implId, entryId, factId]),
            "capsule_contains should cover every anchored member under the capsule root (contract/impl/entry/fact source)");
        Assert((await scanOm.GetNeighborsAsync(capsuleId, "capsule_exposes", OmDirection.Outgoing)).Outgoing.Single().EntityId == entryId
            && AsString(await scanOm.GetPropertyAsync(entryId, "entry_kind")) == "public_api",
            "ck_entry_point ∩ capsule members should materialize depa_entry + capsule_exposes");
        Assert((await scanOm.GetNeighborsAsync(contractId, "contract_implemented_by", OmDirection.Outgoing)).Outgoing.Single().EntityId == implId,
            "IMPLEMENTS observation should materialize contract_implemented_by onto a depa_impl");
        Assert((await scanOm.GetNeighborsAsync(entryId, "entry_delegates_to", OmDirection.Outgoing)).Outgoing.Single().EntityId == implId,
            "CALLS from the entry into a depa_impl should materialize entry_delegates_to");
        Assert((await scanOm.GetNeighborsAsync(factId, "fact_written_by", OmDirection.Outgoing)).Outgoing.Single().EntityId == "depa:impl:csharp:Demo.StoreImpl.Save#1",
            "write-evidence ACCESSES should materialize fact_written_by up to the writer impl");
        // With the ③ detection segment in place (track add-llm-wiki-depa-conformance-tools),
        // a fixture that satisfies all five existential rules still reports BLOCKED for the
        // detectors whose inputs it never declared — BLOCKED != PASS, never silently dropped.
        Assert(!scanResult.RuleFindings.Any(f => f.Verdict == "GAP"),
            "a fully-wired fixture should produce no GAP findings: "
            + string.Join("; ", scanResult.RuleFindings.Select(f => $"{f.RuleId}:{f.Verdict}")));
        // Batch-1 detectors (track expand-depa-detection-rules T2.1) join the BLOCKED set when
        // their inputs (cores/projections/config-params/carriers/factSources) are undeclared;
        // batch-2 detectors (T3.1) join it when recoveryPaths/layers are undeclared.
        string[] wiredBlocked =
        [
            "V-C1", "V-D3", "V-E1", "V-E2", "V-F1", "V-F2", "V-F3", "V-P2", "V-R1", "V-S1",
            "V-S2a", "V-S2b", "V-S3", "V-S4",
        ];
        Assert(scanResult.RuleFindings.Select(f => f.RuleId).OrderBy(r => r, StringComparer.Ordinal).SequenceEqual(wiredBlocked)
            && scanResult.RuleFindings.All(f => f.Verdict == "BLOCKED"),
            "detectors without declared inputs (cores/projections/config-params/carriers) should report BLOCKED with a reason, the rest should run: "
            + string.Join("; ", scanResult.RuleFindings.Select(f => $"{f.RuleId}:{f.Verdict}")));
        Assert(scanResult.DetectionVerdicts["V-D1"] == "PASS" && scanResult.DetectionVerdicts["V-L1"] == "PASS"
            && scanResult.DetectionVerdicts["V-L3"] == "PASS" && scanResult.DetectionVerdicts["V-E1"] == "BLOCKED",
            "runnable detectors with no hits should report PASS while input-missing detectors report BLOCKED");
        Assert(scanResult.Violations.Count == 0,
            "a compliant fixture should materialize zero depa_violation entities");

        // --- scan-idempotent (delta case): second scan over the same data — same output, no
        // duplicate entities or relations ---
        var scanAgain = await scanOm.DepaScanAsync(new DepaScanOptions(MapPath: scanMapPath));
        Assert(scanAgain.EntityCounts.OrderBy(p => p.Key, StringComparer.Ordinal).SequenceEqual(scanResult.EntityCounts.OrderBy(p => p.Key, StringComparer.Ordinal))
            && scanAgain.RelationCounts.OrderBy(p => p.Key, StringComparer.Ordinal).SequenceEqual(scanResult.RelationCounts.OrderBy(p => p.Key, StringComparer.Ordinal))
            && scanAgain.RuleFindings.SequenceEqual(scanResult.RuleFindings)
            && scanAgain.Violations.Count == scanResult.Violations.Count,
            "repeated depa_scan should report identical entity/relation counts, findings and violations");
        Assert((await scanOm.FindByTypeAsync("depa_contract")).Count == 1
            && (await scanOm.FindByTypeAsync("depa_capsule")).Count == 1
            && (await scanOm.GetNeighborsAsync(capsuleId, "capsule_contains", OmDirection.Outgoing)).Outgoing.Count == containsOut.Outgoing.Count
            && (await scanOm.GetNeighborsAsync(contractId, "contract_implemented_by", OmDirection.Outgoing)).Outgoing.Count == 1,
            "repeated depa_scan should upsert in place — no duplicate depa entities or relation rows");

        // --- heuristic-annotation (delta case): no depa-map.json, Contracts/ interface →
        // depa_contract with assigned_by=heuristic and confidence <= 0.7 ---
        using var heurDb = new CozoDb(engine: "mem", path: "");
        var heurOm = new CozoOm(heurDb);
        await heurOm.InitCodeKnowledgeAsync();
        await heurOm.IndexCodeKnowledgeAsync(DepaScanFixture());
        var heurResult = await heurOm.DepaScanAsync();
        Assert(heurResult.AnnotatedFromConfig == 0 && heurResult.AnnotatedFromHeuristic >= 1,
            "without a config file the heuristic fallback should still fire");
        Assert(AsString(await heurOm.GetPropertyAsync(contractId, "assigned_by")) == "heuristic"
            && (await heurOm.GetPropertyAsync(contractId, "confidence"))?.GetDouble() <= 0.7,
            "Contracts/-directory interface should be judged as depa_contract with assigned_by=heuristic and confidence <= 0.7");
        Assert((await heurOm.FindByTypeAsync("depa_capsule")).Count == 0,
            "heuristics must not invent capsules — only depa-map.json declares them");

        // --- blocked-not-guess (delta case): zero annotations → BLOCKED rows explaining the
        // missing inputs, zero materialized entities, zero guessed violations ---
        using var blockedDb = new CozoDb(engine: "mem", path: "");
        var blockedOm = new CozoOm(blockedDb);
        await blockedOm.InitCodeKnowledgeAsync();
        await blockedOm.IndexCodeKnowledgeAsync(new CodeKnowledgeBatch(
            Repositories: [new CodeRepositoryFact("repo:plain", "/repo/plain")],
            Files: [new CodeFileFact("file:p1", "repo:plain", "src/App/Widget.cs", "csharp")],
            Symbols: [new CodeSymbolFact("sym:widget", "file:p1", "Widget", "class", 1, 9, SymKey: "csharp:App.Widget#0")]));
        var blockedResult = await blockedOm.DepaScanAsync();
        Assert(blockedResult.AnnotatedFromConfig == 0 && blockedResult.AnnotatedFromHeuristic == 0
            && blockedResult.EntityCounts.Count == 0 && blockedResult.RelationCounts.Count == 0,
            "zero annotations should materialize nothing");
        string[] annotationDependentRules =
        [
            "capsule_must_expose_entry", "contract_must_have_impl",
            "projection_must_have_upstream", "factsource_must_have_writer",
        ];
        string[] detectionRules =
        [
            "V-A1", "V-C1", "V-D1", "V-D3", "V-E1", "V-E2", "V-E3", "V-F1", "V-F2", "V-F3",
            "V-G1", "V-L1", "V-L2", "V-L3", "V-L4", "V-L5", "V-L6", "V-P2", "V-R1", "V-S1",
            "V-S2a", "V-S2b", "V-S3", "V-S4",
        ];
        Assert(blockedResult.RuleFindings.Count == annotationDependentRules.Length + detectionRules.Length
            && annotationDependentRules.All(rule => blockedResult.RuleFindings.Any(f => f.RuleId == rule && f.Verdict == "BLOCKED"))
            && detectionRules.All(rule => blockedResult.RuleFindings.Any(f => f.RuleId == rule && f.Verdict == "BLOCKED"))
            && blockedResult.RuleFindings.All(f => f.Verdict == "BLOCKED" && f.Message.Contains("no depa annotations", StringComparison.Ordinal)),
            "annotation-dependent rules and all eight detectors should each report BLOCKED with the missing-input explanation (BLOCKED != PASS, no guessing)");
        Assert(detectionRules.All(rule => blockedResult.DetectionVerdicts[rule] == "BLOCKED") && blockedResult.Violations.Count == 0,
            "zero annotations should block every detector without guessing violations");
        Assert((await blockedOm.FindByTypeAsync("depa_violation")).Count == 0
            && (await blockedOm.FindByTypeAsync("depa_contract")).Count == 0
            && (await blockedOm.FindByTypeAsync("depa_capsule")).Count == 0,
            "a blocked scan must not produce speculative depa entities or violations");

        // === DEPA scan explicitness (track fix-om-depa-conformance-gaps, T2.1) ===

        // --- config-at-entry (delta case): the pipeline consumes pre-parsed objects only —
        // injected Map/Effects take precedence over the path fields, so a decoy MapPath and a
        // nonexistent EffectsPath are never read (file IO lives at the entry layer) ---
        var decoyMapPath = Path.Combine(scanTmp.FullName, "decoy-map.json");
        File.WriteAllText(decoyMapPath, """
        { "capsules": [ { "name": "DecoyCapsule", "rootPath": "decoy/nowhere" } ] }
        """);
        var injectedMap = DepaMapConfig.Load(scanMapPath); // parsed once by the caller, then injected
        using var injDb = new CozoDb(engine: "mem", path: "");
        var injOm = new CozoOm(injDb);
        await injOm.InitCodeKnowledgeAsync();
        await injOm.IndexCodeKnowledgeAsync(DepaScanFixture());
        var injResult = await injOm.DepaScanAsync(new DepaScanOptions(
            MapPath: decoyMapPath,
            EffectsPath: Path.Combine(scanTmp.FullName, "does-not-exist", "depa-effects.json"))
        {
            Map = injectedMap,
            Effects = [new DepaEffectRule("Injected.Only.**", "io_file")],
        });
        Assert(injResult.AnnotatedFromConfig == 5 && injResult.AnnotatedFromHeuristic == 1,
            "an injected pre-parsed map should drive the scan exactly like the file it was parsed from "
            + $"(got config={injResult.AnnotatedFromConfig}, heuristic={injResult.AnnotatedFromHeuristic})");
        Assert((await injOm.FindByTypeAsync("depa_capsule")).Count == 1
            && AsString(await injOm.GetPropertyAsync("depa:capsule:Om.Core", "assigned_by")) == "config",
            "the injected Map must win over MapPath — the decoy file's capsule must not materialize");
        Assert(AsString(await injOm.GetPropertyAsync("depa:effectapi:Injected.Only.**", "category")) == "io_file"
            && (await injOm.FindByTypeAsync("depa_effect_api")).Count == DepaEffectCatalog.BuiltIn.Count + 1,
            "the injected Effects list must win over the (nonexistent) EffectsPath and merge with the built-ins");

        // --- timeprovider-indexed-at (delta case): ck_meta.indexed_at flows from
        // Runtime.Options.TimeProvider, so an injected fixed clock is observable ---
        var fixedNow = new DateTimeOffset(2031, 1, 2, 3, 4, 5, TimeSpan.Zero);
        using var clockDb = new CozoDb(engine: "mem", path: "");
        var clockOm = new CozoOm(clockDb, new CozoOmOptions { TimeProvider = new FixedTimeProvider(fixedNow) });
        await clockOm.InitCodeKnowledgeAsync();
        await clockOm.IndexCodeKnowledgeAsync(DepaScanFixture());
        using var clockMeta = clockDb.Run("""?[value] := *ck_meta{ key: "indexed_at", value }""");
        Assert(Rows(clockMeta).GetArrayLength() == 1 && Rows(clockMeta)[0][0].GetString() == fixedNow.ToString("O"),
            "ck_meta.indexed_at should equal the injected TimeProvider's now "
            + $"(got {Rows(clockMeta)[0][0].GetString()}, expected {fixedNow:O})");
    }
    finally
    {
        scanTmp.Delete(recursive: true);
    }
}

// === DEPA red-light detection & violation materialization (track add-llm-wiki-depa-conformance-tools, T1.1) ===
{
    // ck fixture covering all eight detectors: capsule CapA (contract IStore + its impl, a
    // leaky core, a clean core, a graded ledger with two writers, a projection back-writing
    // its grade-2 upstream, a config-role options type with a Func field, a runtime carrier
    // with a business method) and capsule CapB (with an Internals/ file CapA reaches into).
    static CodeKnowledgeBatch DetectFixture() => new(
        Repositories: [new CodeRepositoryFact("repo:det", "/repo/det", Commit: "c0ffee01")],
        Files:
        [
            new CodeFileFact("file:c1", "repo:det", "src/CapA/Contracts/IStore.cs", "csharp"),
            new CodeFileFact("file:c2", "repo:det", "src/CapA/CoreLogic.cs", "csharp"),
            new CodeFileFact("file:c3", "repo:det", "src/CapA/StoreImpl.cs", "csharp"),
            new CodeFileFact("file:c4", "repo:det", "src/CapA/CleanCore.cs", "csharp"),
            new CodeFileFact("file:c5", "repo:det", "src/CapB/Internals/Secret.cs", "csharp"),
            new CodeFileFact("file:c6", "repo:det", "src/CapA/Ledger.cs", "csharp"),
            new CodeFileFact("file:c7", "repo:det", "src/CapA/JobOptions.cs", "csharp"),
            new CodeFileFact("file:c8", "repo:det", "src/CapA/AppRuntime.cs", "csharp"),
            new CodeFileFact("file:c9", "repo:det", "src/CapA/ReportView.cs", "csharp"),
            new CodeFileFact("file:c10", "repo:det", "src/CapA/Api.cs", "csharp"),
            new CodeFileFact("file:c11", "repo:det", "src/CapB/Api.cs", "csharp"),
            new CodeFileFact("file:c12", "repo:det", "src/CapB/State.cs", "csharp"),
        ],
        Symbols:
        [
            new CodeSymbolFact("sym:istore", "file:c1", "IStore", "interface", 3, 8, SymKey: "csharp:Det.IStore#0"),
            new CodeSymbolFact("sym:storeimpl", "file:c3", "StoreImpl", "class", 3, 40, SymKey: "csharp:Det.StoreImpl#0"),
            new CodeSymbolFact("sym:core", "file:c2", "CoreLogic", "class", 5, 120, SymKey: "csharp:Det.CoreLogic#0"),
            new CodeSymbolFact("sym:coredo", "file:c2", "Do", "method", 80, 110, SymKey: "csharp:Det.CoreLogic.Do#0"),
            new CodeSymbolFact("sym:clean", "file:c4", "CleanCore", "class", 5, 40, SymKey: "csharp:Det.CleanCore#0"),
            new CodeSymbolFact("sym:secret", "file:c5", "Secret", "class", 3, 30, SymKey: "csharp:Det.Secret#0"),
            new CodeSymbolFact("sym:gcache", "file:c12", "Cache", "field", 10, 10, Signature: "private static int Cache", SymKey: "csharp:Det.State.Cache#0"),
            new CodeSymbolFact("sym:ledger", "file:c6", "SchemaLedger", "field", 7, 7, SymKey: "csharp:Det.Ledger.SchemaLedger#0"),
            new CodeSymbolFact("sym:eventlog", "file:c6", "EventLog", "field", 9, 9, SymKey: "csharp:Det.Ledger.EventLog#0"),
            new CodeSymbolFact("sym:writer1", "file:c6", "WriteA", "method", 12, 16, SymKey: "csharp:Det.Ledger.WriteA#0"),
            new CodeSymbolFact("sym:writer2", "file:c6", "WriteB", "method", 20, 24, SymKey: "csharp:Det.Ledger.WriteB#0"),
            new CodeSymbolFact("sym:evwriter", "file:c6", "AppendEvent", "method", 30, 34, SymKey: "csharp:Det.Ledger.AppendEvent#0"),
            new CodeSymbolFact("sym:view", "file:c9", "ReportView", "class", 4, 60, SymKey: "csharp:Det.ReportView#0"),
            new CodeSymbolFact("sym:opts", "file:c7", "JobOptions", "class", 3, 20, SymKey: "csharp:Det.JobOptions#0"),
            new CodeSymbolFact("sym:ondone", "file:c7", "OnDone", "property", 8, 8, Signature: "public Func<int, int> OnDone { get; set; }", SymKey: "csharp:Det.JobOptions.OnDone#0"),
            new CodeSymbolFact("sym:retries", "file:c7", "Retries", "property", 10, 10, Signature: "public int Retries { get; set; }", SymKey: "csharp:Det.JobOptions.Retries#0"),
            new CodeSymbolFact("sym:optsparam", "file:c10", "opts", "parameter", 6, 6, SymKey: "csharp:Det.Api.Run.opts#0"),
            new CodeSymbolFact("sym:rt", "file:c8", "AppRuntime", "class", 3, 40, SymKey: "csharp:Det.AppRuntime#0"),
            new CodeSymbolFact("sym:recalc", "file:c8", "Recalculate", "method", 10, 16, SymKey: "csharp:Det.AppRuntime.Recalculate#0"),
            new CodeSymbolFact("sym:getx", "file:c8", "get_Total", "method", 20, 22, SymKey: "csharp:Det.AppRuntime.get_Total#0"),
            new CodeSymbolFact("sym:fwd", "file:c8", "Forward", "method", 25, 25, SymKey: "csharp:Det.AppRuntime.Forward#0"),
            new CodeSymbolFact("sym:run", "file:c10", "Run", "method", 6, 20, SymKey: "csharp:Det.Api.Run#1"),
            new CodeSymbolFact("sym:runb", "file:c11", "RunB", "method", 6, 20, SymKey: "csharp:Det.ApiB.RunB#0"),
        ],
        Edges:
        [
            new CodeEdgeFact("sym:storeimpl", "sym:istore", CodeEdgeKinds.Implements, "file:c3", 3, 0.9, "roslyn"),
            new CodeEdgeFact("sym:core", "sym:coredo", CodeEdgeKinds.Contains, "file:c2", 80, 1.0, "roslyn"),
            // V-L1: CapA core reaches into CapB's Internals/.
            new CodeEdgeFact("sym:coredo", "sym:secret", CodeEdgeKinds.Calls, "file:c2", 95, 1.0, "roslyn", "semantic"),
            // V-E2: core accesses a static mutable field in another capsule.
            new CodeEdgeFact("sym:coredo", "sym:gcache", CodeEdgeKinds.Accesses, "file:c2", 97, 0.9, "roslyn", "read"),
            // Compliant path: clean core only talks to the effect contract.
            new CodeEdgeFact("sym:clean", "sym:istore", CodeEdgeKinds.Calls, "file:c4", 12, 1.0, "roslyn", "semantic"),
            // V-D1: two writers of the grade-1 ledger.
            new CodeEdgeFact("sym:writer1", "sym:ledger", CodeEdgeKinds.Accesses, "file:c6", 14, 0.9, "roslyn", "write"),
            new CodeEdgeFact("sym:writer2", "sym:ledger", CodeEdgeKinds.Accesses, "file:c6", 22, 0.9, "roslyn", "write"),
            // Grade-2 event log: single legitimate writer + read upstream for the projection...
            new CodeEdgeFact("sym:evwriter", "sym:eventlog", CodeEdgeKinds.Accesses, "file:c6", 32, 0.9, "roslyn", "write"),
            new CodeEdgeFact("sym:view", "sym:eventlog", CodeEdgeKinds.Accesses, "file:c9", 20, 0.9, "roslyn", "read"),
            // ...and V-S1: the projection writes back into its grade-2 upstream.
            new CodeEdgeFact("sym:view", "sym:eventlog", CodeEdgeKinds.Accesses, "file:c9", 40, 0.9, "roslyn", "write"),
            // V-F1: config-role options type carries a Func field (plus a benign scalar).
            new CodeEdgeFact("sym:opts", "sym:ondone", CodeEdgeKinds.HasProperty, "file:c7", 8, 1.0, "roslyn"),
            new CodeEdgeFact("sym:opts", "sym:retries", CodeEdgeKinds.HasProperty, "file:c7", 10, 1.0, "roslyn"),
            // V-F2: carrier business method with 2 CALLS out-edges (real logic, delta case
            // real-logic-still-caught); getter excluded; Forward is a pure-delegate facade
            // (single CALLS, no write ACCESSES) — exempt (track refine-depa-detection-precision
            // T2.1, delta case pure-delegate-exempt / B-3).
            new CodeEdgeFact("sym:rt", "sym:recalc", CodeEdgeKinds.HasMethod, "file:c8", 10, 1.0, "roslyn"),
            new CodeEdgeFact("sym:rt", "sym:getx", CodeEdgeKinds.HasMethod, "file:c8", 20, 1.0, "roslyn"),
            new CodeEdgeFact("sym:rt", "sym:fwd", CodeEdgeKinds.HasMethod, "file:c8", 25, 1.0, "roslyn"),
            new CodeEdgeFact("sym:recalc", "sym:coredo", CodeEdgeKinds.Calls, "file:c8", 12, 1.0, "roslyn", "semantic"),
            new CodeEdgeFact("sym:recalc", "sym:getx", CodeEdgeKinds.Calls, "file:c8", 13, 1.0, "roslyn", "semantic"),
            new CodeEdgeFact("sym:fwd", "sym:coredo", CodeEdgeKinds.Calls, "file:c8", 25, 1.0, "roslyn", "semantic"),
            // V-L3: the contract file imports the impl file.
            new CodeEdgeFact("sym:istore", "sym:storeimpl", CodeEdgeKinds.Imports, "file:c1", 1, 0.9, "roslyn"),
            new CodeEdgeFact("sym:run", "sym:storeimpl", CodeEdgeKinds.Calls, "file:c10", 8, 1.0, "roslyn", "semantic"),
        ],
        EntryPoints:
        [
            new CodeEntryPointFact("sym:run", "public_api"),
            new CodeEntryPointFact("sym:runb", "public_api"),
        ],
        ExternalCalls:
        [
            // V-E1: leaky core method writes files directly, bypassing IStore.
            new CodeExternalCallFact("sym:coredo", "System.IO.File.WriteAllText", 3, FirstFileId: "file:c2", FirstLine: 88, Resolver: "roslyn"),
        ]);

    // V-F2 exemption on a heuristic carrier (track refine-depa-detection-precision T2.1):
    // no depa-map.json at all — the name heuristic judges the "Context"-named record a
    // carrier; Forward is a pure delegate (single CALLS, no write ACCESSES, exempt) while
    // Mutate carries real logic (CALLS + write ACCESSES, still a GAP).
    static CodeKnowledgeBatch HeuristicCarrierFixture() => new(
        Repositories: [new CodeRepositoryFact("repo:hc", "/repo/hc")],
        Files:
        [
            new CodeFileFact("file:h1", "repo:hc", "src/App/OpContext.cs", "csharp"),
            new CodeFileFact("file:h2", "repo:hc", "src/App/Engine.cs", "csharp"),
        ],
        Symbols:
        [
            new CodeSymbolFact("sym:hctx", "file:h1", "OpContext", "record", 3, 30, SymKey: "csharp:App.OpContext#0"),
            new CodeSymbolFact("sym:hstate", "file:h1", "State", "field", 6, 6, SymKey: "csharp:App.OpContext.State#0"),
            new CodeSymbolFact("sym:hfwd", "file:h1", "Forward", "method", 10, 10, SymKey: "csharp:App.OpContext.Forward#0"),
            new CodeSymbolFact("sym:hmut", "file:h1", "Mutate", "method", 14, 18, SymKey: "csharp:App.OpContext.Mutate#0"),
            new CodeSymbolFact("sym:hrun", "file:h2", "Run", "method", 5, 20, SymKey: "csharp:App.Engine.Run#0"),
        ],
        Edges:
        [
            new CodeEdgeFact("sym:hctx", "sym:hfwd", CodeEdgeKinds.HasMethod, "file:h1", 10, 1.0, "roslyn"),
            new CodeEdgeFact("sym:hctx", "sym:hmut", CodeEdgeKinds.HasMethod, "file:h1", 14, 1.0, "roslyn"),
            // Pure delegate: exactly one outgoing CALLS, no write ACCESSES.
            new CodeEdgeFact("sym:hfwd", "sym:hrun", CodeEdgeKinds.Calls, "file:h1", 10, 1.0, "roslyn", "semantic"),
            // Real logic: a CALLS plus a write ACCESSES.
            new CodeEdgeFact("sym:hmut", "sym:hrun", CodeEdgeKinds.Calls, "file:h1", 15, 1.0, "roslyn", "semantic"),
            new CodeEdgeFact("sym:hmut", "sym:hstate", CodeEdgeKinds.Accesses, "file:h1", 16, 0.9, "roslyn", "write"),
        ]);

    var detTmp = Directory.CreateTempSubdirectory("depa-detect-tests-");
    try
    {
        var detMapPath = Path.Combine(detTmp.FullName, "depa-map.json");
        File.WriteAllText(detMapPath, """
        {
          "capsules": [
            { "name": "CapA", "rootPath": "src/CapA" },
            { "name": "CapB", "rootPath": "src/CapB" }
          ],
          "contractPackages": ["src/CapA/Contracts"],
          "runtimeCarrierTypes": ["AppRuntime"],
          "cores": ["CoreLogic", "CleanCore"],
          "projections": ["ReportView"],
          "runtimeParams": [ { "symbolOrPath": "opts", "role": "config", "declaredType": "JobOptions" } ],
          "factSources": [
            { "symbolOrPath": "SchemaLedger", "grade": 1 },
            { "symbolOrPath": "EventLog", "grade": 2 }
          ]
        }
        """);

        using var detDb = new CozoDb(engine: "mem", path: "");
        var detOm = new CozoOm(detDb);
        await detOm.InitCodeKnowledgeAsync();
        await detOm.IndexCodeKnowledgeAsync(DetectFixture());

        var detResult = await detOm.DepaScanAsync(new DepaScanOptions(MapPath: detMapPath));
        string[] allDetectionRules = ["V-D1", "V-E1", "V-E2", "V-F1", "V-F2", "V-L1", "V-L3", "V-S1"];

        // Every detector has its inputs declared and its red light staged: all eight report GAP,
        // no BLOCKED noise, and no existential misses.
        Assert(allDetectionRules.All(rule => detResult.DetectionVerdicts[rule] == "GAP"),
            "all eight staged red lights should be detected as GAP: "
            + string.Join("; ", detResult.DetectionVerdicts.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}:{p.Value}")));
        // Batch-1 detectors (track expand-depa-detection-rules T2.1): the fixture stages no
        // parameter symbols under its cores and no input-role params, so exactly V-C1/V-S3
        // stay BLOCKED naming those gaps; batch-2 (T3.1) adds V-P2/V-S2a/V-S2b, whose
        // recoveryPaths/layers annotations this map does not declare; every other rule runs.
        Assert(detResult.RuleFindings.All(f => f.Verdict == "BLOCKED")
            && detResult.RuleFindings.Select(f => f.RuleId).OrderBy(r => r, StringComparer.Ordinal)
                .SequenceEqual(["V-C1", "V-P2", "V-S2a", "V-S2b", "V-S3"]),
            "fully-declared inputs should leave only the V-C1/V-P2/V-S2a/V-S2b/V-S3 missing-input BLOCKED findings: "
            + string.Join("; ", detResult.RuleFindings.Select(f => $"{f.RuleId}:{f.Verdict}:{f.Message}")));
        string[] batch1CleanRules = ["V-A1", "V-D3", "V-E3", "V-F3", "V-G1", "V-L2", "V-L4", "V-L5", "V-L6", "V-R1", "V-S4"];
        Assert(batch1CleanRules.All(rule => detResult.DetectionVerdicts[rule] == "PASS"),
            "batch-1 rules with no staged red light must report PASS on this fixture (反例零误报): "
            + string.Join("; ", batch1CleanRules.Select(rule => $"{rule}:{detResult.DetectionVerdicts[rule]}")));

        // Evidence discipline (design §5.2 / AC T1.1-AC1): every violation carries at least one
        // evidence entry and every entry has a real path:line.
        Assert(detResult.Violations.Count > 0
            && detResult.Violations.All(v => v.Evidence.Count > 0 && v.Evidence.All(e => e.Path.Length > 0 && e.Line > 0)),
            "every materialized violation must carry path:line evidence entries");
        Assert((await detOm.FindByTypeAsync("depa_violation")).Count == detResult.Violations.Count,
            "the store should hold exactly the reported violations");

        const string coreImplId = "depa:impl:csharp:Det.CoreLogic#0";
        const string cleanImplId = "depa:impl:csharp:Det.CleanCore#0";

        // --- effect-leak-detected (delta case): V-E1 GAP + path:line + effect_leaks_through ---
        var e1 = detResult.Violations.Single(v => v.RuleId == "V-E1");
        Assert(e1.SubjectEntityId == coreImplId && e1.Dimension == "effect"
            && e1.Evidence[0].Path == "src/CapA/CoreLogic.cs" && e1.Evidence[0].Line == 88
            && e1.Evidence[0].Detail.Contains("System.IO.File.WriteAllText", StringComparison.Ordinal)
            && e1.Evidence[0].Detail.Contains("count=3", StringComparison.Ordinal),
            $"V-E1 should blame the leaky core with the first-call-site evidence (got {e1.SubjectEntityId} {e1.Evidence[0].Path}:{e1.Evidence[0].Line} '{e1.Evidence[0].Detail}')");
        var leakEdges = await detOm.GetNeighborsAsync(coreImplId, "effect_leaks_through", OmDirection.Outgoing);
        Assert(leakEdges.Outgoing.Single().EntityId == "depa:effectapi:System.IO.**",
            "V-E1 should materialize an effect_leaks_through edge onto the matched whitelist entry");
        var e1Json = await detOm.GetPropertyAsync(e1.ViolationId, "evidence_json");
        Assert(e1Json is not null
            && e1Json.Value.GetProperty("rule_id").GetString() == "V-E1"
            && e1Json.Value.GetProperty("verdict").GetString() == "GAP"
            && e1Json.Value.GetProperty("subject").GetProperty("entity_id").GetString() == coreImplId
            && e1Json.Value.GetProperty("scan_commit").GetString() == "c0ffee01"
            && e1Json.Value.GetProperty("detected_at").GetString()!.Length > 0
            && e1Json.Value.GetProperty("evidence").EnumerateArray().All(e =>
                e.GetProperty("path").GetString()!.Length > 0 && e.GetProperty("line").GetInt32() > 0),
            "evidence_json should follow the design §5.2 schema with scan_commit/detected_at and path:line entries");

        // --- compliant-no-leak (delta case): the clean core stays clean ---
        Assert(!detResult.Violations.Any(v => v.RuleId == "V-E1" && v.SubjectEntityId == cleanImplId)
            && (await detOm.GetNeighborsAsync(cleanImplId, "effect_leaks_through", OmDirection.Outgoing)).Outgoing.Count == 0,
            "a core that only effects through the contract must not be reported by V-E1 (compliant sample distinguishable)");

        // --- func-in-config (delta case): V-F1 evidence points at the Func field declaration ---
        var f1 = detResult.Violations.Single(v => v.RuleId == "V-F1");
        Assert(f1.Evidence.Single().Path == "src/CapA/JobOptions.cs" && f1.Evidence.Single().Line == 8
            && f1.Message.Contains("OnDone", StringComparison.Ordinal)
            && !f1.Message.Contains("Retries", StringComparison.Ordinal),
            "V-F1 should flag exactly the function-object field with its declaration line");
        // dimension 修正（track expand-depa-detection-rules T1.1）：rubrics/violation-catalog.md F 组
        // "函数对象塞 config" 所属=分层 → V-F1 归 layering，不再是 processor。
        Assert(f1.Dimension == "layering",
            $"V-F1 must belong to the layering dimension per rubrics/violation-catalog.md F 组 (got '{f1.Dimension}')");

        // --- internals-crossing (delta case): V-L1, one evidence per crossing edge ---
        var l1 = detResult.Violations.Single(v => v.RuleId == "V-L1");
        Assert(l1.SubjectEntityId == "depa:capsule:CapA" && l1.Dimension == "layering"
            && l1.Evidence.Count == 1
            && l1.Evidence[0].Path == "src/CapA/CoreLogic.cs" && l1.Evidence[0].Line == 95
            && l1.Message.Contains("CapB", StringComparison.Ordinal),
            "V-L1 should report the crossing capsule with one evidence entry per boundary-violating edge");

        // --- multi-writer (delta case): V-D1 lists every writer ---
        var d1 = detResult.Violations.Single(v => v.RuleId == "V-D1"
            && v.Message.Contains("SchemaLedger", StringComparison.Ordinal));
        Assert(d1.Message.Contains("WriteA", StringComparison.Ordinal) && d1.Message.Contains("WriteB", StringComparison.Ordinal)
            && d1.Evidence.Count == 2
            && d1.Evidence.Select(e => e.Line).OrderBy(l => l).SequenceEqual([14, 22])
            && d1.Dimension == "data",
            "V-D1 should list all writers of the grade-1 ledger with one write-site evidence each");

        // --- V-S1 positive: projection back-writes its grade-2 upstream + backwrites edge ---
        var s1 = detResult.Violations.Single(v => v.RuleId == "V-S1");
        const string projectionId = "depa:projection:csharp:Det.ReportView#0";
        const string eventLogId = "depa:factsource:csharp:Det.Ledger.EventLog#0";
        Assert(s1.SubjectEntityId == projectionId && s1.Dimension == "fact_source"
            && s1.Evidence.Single().Line == 40,
            "V-S1 should flag the projection's write path into the grade<=2 upstream");
        Assert((await detOm.GetNeighborsAsync(projectionId, "backwrites", OmDirection.Outgoing)).Outgoing.Single().EntityId == eventLogId,
            "V-S1 should materialize the backwrites signal edge");

        // --- V-E2 positive: core touches a static mutable field of another capsule ---
        var e2 = detResult.Violations.Single(v => v.RuleId == "V-E2");
        Assert(e2.SubjectEntityId == coreImplId
            && e2.Evidence.Single().Path == "src/CapA/CoreLogic.cs" && e2.Evidence.Single().Line == 97
            && e2.Evidence.Single().Detail.Contains("Cache", StringComparison.Ordinal),
            "V-E2 should report the ACCESSES site of the static mutable field");

        // --- V-F2 positive: carrier business method (getter excluded), heuristic confidence <= 0.8 ---
        var f2 = detResult.Violations.Single(v => v.RuleId == "V-F2");
        Assert(f2.Message.Contains("Recalculate", StringComparison.Ordinal)
            && !detResult.Violations.Any(v => v.RuleId == "V-F2" && v.Message.Contains("get_Total", StringComparison.Ordinal))
            && f2.Confidence <= 0.8
            && f2.Evidence[0].Path == "src/CapA/AppRuntime.cs" && f2.Evidence[0].Line == 10,
            "V-F2 should flag the non-getter carrier method with capped heuristic confidence");
        // dimension 修正（track expand-depa-detection-rules T1.1）：rubrics/violation-catalog.md F 组
        // "业务逻辑写在 runtime dataclass 方法里" 所属=分层/Effect（主归属分层）→ V-F2 归 layering。
        Assert(f2.Dimension == "layering",
            $"V-F2 must belong to the layering dimension per rubrics/violation-catalog.md F 组 (got '{f2.Dimension}')");
        // --- V-F2 pure-delegate exemption (track refine-depa-detection-precision T2.1, delta
        // case pure-delegate-exempt): Forward has exactly one outgoing CALLS and no write
        // ACCESSES — a compliant single-expression facade, not business logic; Recalculate
        // (2 CALLS) stays reported above (delta case real-logic-still-caught).
        Assert(!detResult.Violations.Any(v => v.RuleId == "V-F2" && v.Message.Contains("Forward", StringComparison.Ordinal)),
            "V-F2 must exempt the pure-delegate carrier method (single CALLS, no write ACCESSES): "
            + string.Join("; ", detResult.Violations.Where(v => v.RuleId == "V-F2").Select(v => v.Message)));

        // --- V-F2 exemption on a heuristic carrier (delta case pure-delegate-exempt given):
        // with no runtimeCarrierTypes declaration the name heuristic judges OpContext a
        // carrier; its pure-delegate Forward must not GAP while Mutate (CALLS + write
        // ACCESSES) still does — unlabeled repos keep the compliant facade shape unharmed. ---
        using var hcDb = new CozoDb(engine: "mem", path: "");
        var hcOm = new CozoOm(hcDb);
        await hcOm.InitCodeKnowledgeAsync();
        await hcOm.IndexCodeKnowledgeAsync(HeuristicCarrierFixture());
        var hcResult = await hcOm.DepaScanAsync();
        var hcF2 = hcResult.Violations.Where(v => v.RuleId == "V-F2").ToArray();
        Assert(hcResult.DetectionVerdicts["V-F2"] == "GAP"
            && hcF2.Length == 1 && hcF2[0].Message.Contains("Mutate", StringComparison.Ordinal)
            && !hcF2.Any(v => v.Message.Contains("Forward", StringComparison.Ordinal)),
            "on a heuristic carrier the pure-delegate method must be exempt while the mutating method stays a GAP: "
            + string.Join("; ", hcF2.Select(v => v.Message)));

        // --- V-L3 positive: contract file imports the impl file ---
        var l3 = detResult.Violations.Single(v => v.RuleId == "V-L3");
        Assert(l3.SubjectEntityId == "depa:contract:csharp:Det.IStore#0" && l3.Dimension == "layering"
            && l3.Evidence.Single().Path == "src/CapA/Contracts/IStore.cs" && l3.Evidence.Single().Line == 1,
            "V-L3 should flag the reverse dependency at the import site of the contract file");

        // --- blocked-on-missing-input (delta case): same observations, inputs withheld ---
        var thinMapPath = Path.Combine(detTmp.FullName, "thin-map.json");
        File.WriteAllText(thinMapPath, """
        {
          "capsules": [
            { "name": "CapA", "rootPath": "src/CapA" },
            { "name": "CapB", "rootPath": "src/CapB" }
          ],
          "contractPackages": ["src/CapA/Contracts"]
        }
        """);
        using var thinDb = new CozoDb(engine: "mem", path: "");
        var thinOm = new CozoOm(thinDb);
        await thinOm.InitCodeKnowledgeAsync();
        await thinOm.IndexCodeKnowledgeAsync(DetectFixture());
        var thinResult = await thinOm.DepaScanAsync(new DepaScanOptions(MapPath: thinMapPath));
        string[] thinBlocked =
        [
            "V-C1", "V-D1", "V-D3", "V-E1", "V-E2", "V-F1", "V-F2", "V-F3", "V-P2", "V-R1",
            "V-S1", "V-S2a", "V-S2b", "V-S3", "V-S4",
        ];
        Assert(thinBlocked.All(rule => thinResult.DetectionVerdicts[rule] == "BLOCKED"),
            "detectors whose annotations are withheld should report BLOCKED, not guess: "
            + string.Join("; ", thinResult.DetectionVerdicts.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}:{p.Value}")));
        Assert(thinBlocked.All(rule => thinResult.RuleFindings.Any(f =>
                f.RuleId == rule && f.Verdict == "BLOCKED" && f.Message.Contains("BLOCKED", StringComparison.Ordinal) && f.Message.Length > 20)),
            "each blocked detector should explain what input is missing");
        Assert(thinResult.DetectionVerdicts["V-D1"] == "BLOCKED"
            && thinResult.RuleFindings.Single(f => f.RuleId == "V-D1").Message.Contains("fact", StringComparison.OrdinalIgnoreCase),
            "V-D1 without a fact-source grading table should name the grading table as the missing input");
        Assert(thinResult.DetectionVerdicts["V-L1"] == "GAP" && thinResult.DetectionVerdicts["V-L3"] == "GAP"
            && thinResult.Violations.All(v => v.RuleId is "V-L1" or "V-L3"),
            "detectors whose inputs are present should still run — no speculative violations for the blocked ones");

        // --- violation-idempotent-and-expiring (delta case) ---
        var detAgain = await detOm.DepaScanAsync(new DepaScanOptions(MapPath: detMapPath));
        Assert(detAgain.Violations.Count == detResult.Violations.Count
            && detAgain.Violations.Select(v => v.ViolationId).OrderBy(v => v, StringComparer.Ordinal)
                .SequenceEqual(detResult.Violations.Select(v => v.ViolationId).OrderBy(v => v, StringComparer.Ordinal))
            && (await detOm.FindByTypeAsync("depa_violation")).Count == detResult.Violations.Count,
            "re-scanning unchanged code must upsert violations in place (stable ids, no duplicates)");
        Assert((await detOm.GetNeighborsAsync(coreImplId, "effect_leaks_through", OmDirection.Outgoing)).Outgoing.Count == 1,
            "signal edges must not duplicate across scans");

        // Fix the V-E1 leak (drop the external-call observation) and re-scan: the violation and
        // its signal edge are cleaned up, everything else survives.
        await detOm.Runtime.Store.RunAsync("""
            ?[caller_id, target_key] <- [["sym:coredo", "System.IO.File.WriteAllText"]]
            :rm ck_external_call {caller_id, target_key}
            """);
        var detFixed = await detOm.DepaScanAsync(new DepaScanOptions(MapPath: detMapPath));
        Assert(detFixed.DetectionVerdicts["V-E1"] == "PASS"
            && !detFixed.Violations.Any(v => v.RuleId == "V-E1")
            && detFixed.Violations.Count == detResult.Violations.Count - 1,
            "after the fix V-E1 should turn PASS and only its violation should disappear");
        Assert(!(await detOm.FindByTypeAsync("depa_violation")).Any(v => v.Id == e1.ViolationId),
            "the fixed violation should be expired from the store at scan end (rebuildable projection)");
        Assert((await detOm.GetNeighborsAsync(coreImplId, "effect_leaks_through", OmDirection.Outgoing)).Outgoing.Count == 0,
            "the stale effect_leaks_through edge should be retracted with its violation");
        Assert(detFixed.DetectionVerdicts["V-D1"] == "GAP"
            && (await detOm.FindByTypeAsync("depa_violation")).Count == detResult.Violations.Count - 1,
            "violations that still hold must survive the cleanup");

        // --- depa-cleanup-no-orphans (delta case, track fix-om-depa-conformance-gaps T1.1):
        // expiry goes through DeleteEntityAsync, so the expired violation leaves no orphan
        // om_property rows (evidence_json/... used to linger after the raw ':rm om_entity').
        var expiredProps = await detOm.Runtime.Store.RunAsync(
            """
            ?[attr_name, valid_time] :=
              *om_property{ entity_id: $entity_id, attr_name, valid_time, value: _v, tx_time: _tx }
            """,
            new Dictionary<string, object?> { ["entity_id"] = e1.ViolationId });
        Assert(expiredProps.Rows.Count == 0,
            "the expired violation must leave zero om_property rows behind, got "
            + string.Join("; ", expiredProps.Rows.Select(r => r[0].ToString())));
        var orphanProps = await detOm.Runtime.Store.RunAsync(
            """
            ?[entity_id, attr_name] :=
              *om_property{ entity_id, attr_name, valid_time: _vt, value: _v, tx_time: _tx },
              starts_with(entity_id, "depa:violation:"),
              not *om_entity{ id: entity_id, type_name: _tn, label: _lb }
            """);
        Assert(orphanProps.Rows.Count == 0,
            "no depa:violation om_property row may exist without its om_entity row: "
            + string.Join("; ", orphanProps.Rows.Select(r => $"{r[0]}/{r[1]}")));
    }
    finally
    {
        detTmp.Delete(recursive: true);
    }
}

// === DeleteEntityAsync — single-transaction cascade + idempotency
// (track fix-om-depa-conformance-gaps T1.1; delta delete-cascades / delete-idempotent) ===
{
    static async Task<(int Entities, int Properties, int OutEdges, int InEdges)> CountEntityRowsAsync(CozoOm om, string id)
    {
        var parameters = new Dictionary<string, object?> { ["id"] = id };
        var entities = await om.Runtime.Store.RunAsync(
            """
            ?[type_name] :=
              *om_entity{ id: $id, type_name, label: _l }
            """, parameters);
        var properties = await om.Runtime.Store.RunAsync(
            """
            ?[attr_name, valid_time] :=
              *om_property{ entity_id: $id, attr_name, valid_time, value: _v, tx_time: _tx }
            """, parameters);
        var outgoing = await om.Runtime.Store.RunAsync(
            """
            ?[rel_name, to_id, valid_time] :=
              *om_edge{ from_id: $id, rel_name, to_id, valid_time, props: _p, tx_time: _tx }
            """, parameters);
        var incoming = await om.Runtime.Store.RunAsync(
            """
            ?[from_id, rel_name, valid_time] :=
              *om_edge{ from_id, rel_name, to_id: $id, valid_time, props: _p, tx_time: _tx }
            """, parameters);
        return (entities.Rows.Count, properties.Rows.Count, outgoing.Rows.Count, incoming.Rows.Count);
    }

    using var delDb = new CozoDb(engine: "mem", path: "");
    var delOm = new CozoOm(delDb);
    await delOm.InitSchemaAsync();
    await delOm.DefineTypeAsync("DelNode", "Delete-cascade test node");
    await delOm.DefineAttributeAsync("DelNode", "name", OmValueType.String);
    await delOm.DefineAttributeAsync("DelNode", "score", OmValueType.Number);
    await delOm.DefineAttributeAsync("DelNode", "tags", OmValueType.Json);
    await delOm.DefineRelationAsync("del_points_to", "DelNode", "DelNode");

    await delOm.CreateEntityAsync("del-x", "DelNode", "Victim");
    await delOm.CreateEntityAsync("del-a", "DelNode", "Bystander upstream");
    await delOm.CreateEntityAsync("del-b", "DelNode", "Bystander downstream");
    await delOm.SetPropertyAsync("del-x", "name", "victim");
    await delOm.SetPropertyAsync("del-x", "score", 1, new WriteOptions(ValidTime: "2024-01-01T00:00:00Z"));
    await delOm.SetPropertyAsync("del-x", "score", 2, new WriteOptions(ValidTime: "2025-01-01T00:00:00Z"));
    await delOm.SetPropertyAsync("del-x", "tags", new[] { "t1", "t2" });
    await delOm.SetPropertyAsync("del-a", "name", "bystander");
    await delOm.LinkEntitiesAsync("del-x", "del_points_to", "del-b");
    await delOm.LinkEntitiesAsync("del-a", "del_points_to", "del-x");
    await delOm.LinkEntitiesAsync("del-a", "del_points_to", "del-b");

    var before = await CountEntityRowsAsync(delOm, "del-x");
    Assert(before is { Entities: 1, Properties: 4, OutEdges: 1, InEdges: 1 },
        $"fixture should stage 1 entity / 4 property rows (score has 2 temporal versions) / 1 out + 1 in edge, got {before}");

    // delete-cascades: entity row, every temporal property row and every touching edge go in one call.
    await delOm.DeleteEntityAsync("del-x");
    var after = await CountEntityRowsAsync(delOm, "del-x");
    Assert(after is { Entities: 0, Properties: 0, OutEdges: 0, InEdges: 0 },
        $"DeleteEntityAsync must cascade to om_entity/om_property/om_edge with zero residue, got {after}");
    Assert(await delOm.GetEntityViewAsync("del-x") is null,
        "a deleted entity must be invisible to GetEntityViewAsync");

    // Bystanders keep their rows: del-a keeps its property and its edge to del-b.
    var bystander = await CountEntityRowsAsync(delOm, "del-a");
    Assert(bystander is { Entities: 1, Properties: 1, OutEdges: 1, InEdges: 0 },
        $"deleting del-x must not disturb other entities' rows, got {bystander}");
    Assert((await delOm.GetNeighborsAsync("del-a", "del_points_to", OmDirection.Outgoing)).Outgoing.Single().EntityId == "del-b",
        "the third-party edge del-a -> del-b must survive the cascade");

    // delete-idempotent: re-deleting and deleting a never-existing id are harmless no-ops.
    await delOm.DeleteEntityAsync("del-x");
    await delOm.DeleteEntityAsync("del-never-existed");
    Assert((await CountEntityRowsAsync(delOm, "del-a")).Entities == 1,
        "idempotent deletes must leave the store untouched");
}

// === DEPA report aggregation — depa_conformance / fact_grade_map / health_score backing
// (track add-llm-wiki-depa-conformance-tools, T2.1) ===
{
    // Fixture: capsules CapA/CapB with V-E1 (leaky core, clean core distinguishable),
    // V-D1 (two ledger writers), V-F1 (Func in config), V-F2 on the config-declared carrier
    // only — JobContext is a "Context"-named record with a business method, but the non-empty
    // runtimeCarrierTypes declaration closes the carrier world and suppresses the name
    // heuristic (track fix-om-depa-conformance-gaps, delta case carrier-false-positive-suppressed),
    // V-L1 (internals crossing); no projections declared so V-S1 stays BLOCKED.
    static CodeKnowledgeBatch ReportFixture() => new(
        Repositories: [new CodeRepositoryFact("repo:rep", "/repo/rep", Commit: "beefcafe")],
        Files:
        [
            new CodeFileFact("file:r1", "repo:rep", "src/CapA/Contracts/IStore.cs", "csharp"),
            new CodeFileFact("file:r2", "repo:rep", "src/CapA/CoreLogic.cs", "csharp"),
            new CodeFileFact("file:r3", "repo:rep", "src/CapA/StoreImpl.cs", "csharp"),
            new CodeFileFact("file:r4", "repo:rep", "src/CapA/Ledger.cs", "csharp"),
            new CodeFileFact("file:r5", "repo:rep", "src/CapA/JobOptions.cs", "csharp"),
            new CodeFileFact("file:r6", "repo:rep", "src/CapA/AppRuntime.cs", "csharp"),
            new CodeFileFact("file:r7", "repo:rep", "src/CapA/JobContext.cs", "csharp"),
            new CodeFileFact("file:r8", "repo:rep", "src/CapB/Internals/Secret.cs", "csharp"),
        ],
        Symbols:
        [
            new CodeSymbolFact("sym:ristore", "file:r1", "IStore", "interface", 3, 8, SymKey: "csharp:Rep.IStore#0"),
            new CodeSymbolFact("sym:rimpl", "file:r3", "StoreImpl", "class", 3, 40, SymKey: "csharp:Rep.StoreImpl#0"),
            new CodeSymbolFact("sym:rcore", "file:r2", "CoreLogic", "class", 5, 120, SymKey: "csharp:Rep.CoreLogic#0"),
            new CodeSymbolFact("sym:rcoredo", "file:r2", "Do", "method", 80, 110, SymKey: "csharp:Rep.CoreLogic.Do#0"),
            new CodeSymbolFact("sym:rclean", "file:r2", "CleanCore", "class", 130, 160, SymKey: "csharp:Rep.CleanCore#0"),
            new CodeSymbolFact("sym:rledger", "file:r4", "SchemaLedger", "field", 7, 7, SymKey: "csharp:Rep.Ledger.SchemaLedger#0"),
            new CodeSymbolFact("sym:reventlog", "file:r4", "EventLog", "field", 9, 9, SymKey: "csharp:Rep.Ledger.EventLog#0"),
            new CodeSymbolFact("sym:rwritea", "file:r4", "WriteA", "method", 12, 16, SymKey: "csharp:Rep.Ledger.WriteA#0"),
            new CodeSymbolFact("sym:rwriteb", "file:r4", "WriteB", "method", 20, 24, SymKey: "csharp:Rep.Ledger.WriteB#0"),
            new CodeSymbolFact("sym:rappend", "file:r4", "AppendEvent", "method", 30, 34, SymKey: "csharp:Rep.Ledger.AppendEvent#0"),
            new CodeSymbolFact("sym:ropts", "file:r5", "JobOptions", "class", 3, 20, SymKey: "csharp:Rep.JobOptions#0"),
            new CodeSymbolFact("sym:rondone", "file:r5", "OnDone", "property", 8, 8, Signature: "public Func<int, int> OnDone { get; set; }", SymKey: "csharp:Rep.JobOptions.OnDone#0"),
            new CodeSymbolFact("sym:rrt", "file:r6", "AppRuntime", "class", 3, 40, SymKey: "csharp:Rep.AppRuntime#0"),
            new CodeSymbolFact("sym:rrecalc", "file:r6", "Recalculate", "method", 10, 16, SymKey: "csharp:Rep.AppRuntime.Recalculate#0"),
            new CodeSymbolFact("sym:rtotal", "file:r6", "CachedTotal", "field", 6, 6, Signature: "private int CachedTotal", SymKey: "csharp:Rep.AppRuntime.CachedTotal#0"),
            new CodeSymbolFact("sym:rjctx", "file:r7", "JobContext", "record", 3, 30, SymKey: "csharp:Rep.JobContext#0"),
            new CodeSymbolFact("sym:rrefresh", "file:r7", "Refresh", "method", 12, 18, SymKey: "csharp:Rep.JobContext.Refresh#0"),
            new CodeSymbolFact("sym:rsecret", "file:r8", "Secret", "class", 3, 30, SymKey: "csharp:Rep.Secret#0"),
        ],
        Edges:
        [
            new CodeEdgeFact("sym:rimpl", "sym:ristore", CodeEdgeKinds.Implements, "file:r3", 3, 0.9, "roslyn"),
            new CodeEdgeFact("sym:rcore", "sym:rcoredo", CodeEdgeKinds.Contains, "file:r2", 80, 1.0, "roslyn"),
            new CodeEdgeFact("sym:rcoredo", "sym:rsecret", CodeEdgeKinds.Calls, "file:r2", 95, 1.0, "roslyn", "semantic"),
            new CodeEdgeFact("sym:rwritea", "sym:rledger", CodeEdgeKinds.Accesses, "file:r4", 14, 0.9, "roslyn", "write"),
            new CodeEdgeFact("sym:rwriteb", "sym:rledger", CodeEdgeKinds.Accesses, "file:r4", 22, 0.9, "roslyn", "write"),
            new CodeEdgeFact("sym:rappend", "sym:reventlog", CodeEdgeKinds.Accesses, "file:r4", 32, 0.9, "roslyn", "write"),
            new CodeEdgeFact("sym:ropts", "sym:rondone", CodeEdgeKinds.HasProperty, "file:r5", 8, 1.0, "roslyn"),
            new CodeEdgeFact("sym:rrt", "sym:rrecalc", CodeEdgeKinds.HasMethod, "file:r6", 10, 1.0, "roslyn"),
            new CodeEdgeFact("sym:rrecalc", "sym:rcoredo", CodeEdgeKinds.Calls, "file:r6", 12, 1.0, "roslyn", "semantic"),
            // Real logic, not a pure delegate: the write ACCESSES keeps Recalculate a V-F2 GAP
            // under the pure-delegate exemption (track refine-depa-detection-precision T2.1).
            new CodeEdgeFact("sym:rrecalc", "sym:rtotal", CodeEdgeKinds.Accesses, "file:r6", 13, 0.9, "roslyn", "write"),
            new CodeEdgeFact("sym:rjctx", "sym:rrefresh", CodeEdgeKinds.HasMethod, "file:r7", 12, 1.0, "roslyn"),
            new CodeEdgeFact("sym:rrefresh", "sym:rcoredo", CodeEdgeKinds.Calls, "file:r7", 14, 1.0, "roslyn", "semantic"),
        ],
        ExternalCalls:
        [
            new CodeExternalCallFact("sym:rcoredo", "System.IO.File.WriteAllText", 2, FirstFileId: "file:r2", FirstLine: 88, Resolver: "roslyn"),
        ]);

    var repTmp = Directory.CreateTempSubdirectory("depa-report-tests-");
    try
    {
        var repMapPath = Path.Combine(repTmp.FullName, "depa-map.json");
        File.WriteAllText(repMapPath, """
        {
          "capsules": [
            { "name": "CapA", "rootPath": "src/CapA" },
            { "name": "CapB", "rootPath": "src/CapB" }
          ],
          "contractPackages": ["src/CapA/Contracts"],
          "runtimeCarrierTypes": ["AppRuntime"],
          "cores": ["CoreLogic", "CleanCore"],
          "runtimeParams": [ { "symbolOrPath": "opts", "role": "config", "declaredType": "JobOptions" } ],
          "factSources": [
            { "symbolOrPath": "SchemaLedger", "grade": 1 },
            { "symbolOrPath": "EventLog", "grade": 2 }
          ]
        }
        """);

        using var repDb = new CozoDb(engine: "mem", path: "");
        var repOm = new CozoOm(repDb);
        await repOm.InitCodeKnowledgeAsync();
        await repOm.IndexCodeKnowledgeAsync(ReportFixture());

        // --- conformance-report (delta tools case, API backing): dimension grouping ---
        var report = await repOm.GetConformanceReportAsync(new DepaConformanceOptions(MapPath: repMapPath));
        Assert(report.Scanned, "the default ScanFirst report should mark itself as scanned");
        // eight-dimensions (delta case, track expand-depa-detection-rules T1.1): 报告按 8 维
        // 分组（新增 overdesign/vendor），V-F1/F2 归属修正为 layering，coverage 分母为全目录口径。
        string[] expectedDims = ["data", "effect", "processor", "layering", "fact_source", "actor", "overdesign", "vendor"];
        Assert(report.Dimensions.Select(d => d.Dimension).SequenceEqual(expectedDims),
            "the report should group by the eight dimensions (incl. overdesign/vendor) "
            + "in canonical order (got: " + string.Join(", ", report.Dimensions.Select(d => d.Dimension)) + ")");
        var ruleRows = report.Dimensions.ToDictionary(d => d.Dimension, d => d.Rules, StringComparer.Ordinal);
        // Batch-1 (T2.1) and batch-2 (T3.1: V-P2/V-S2a/V-S2b) detectors join their rule-map
        // dimensions: the denominator now spans 8+13+3 detectors + 8 placeholders = 32.
        Assert(ruleRows["data"].Select(r => r.RuleId).SequenceEqual(["V-D1", "V-D2", "V-D3"])
            && ruleRows["effect"].Select(r => r.RuleId).SequenceEqual(["V-E1", "V-E2", "V-E3"])
            && ruleRows["processor"].Select(r => r.RuleId).SequenceEqual(["V-C1", "V-P1", "V-P2", "V-P3", "V-P4"])
            && ruleRows["layering"].Select(r => r.RuleId).SequenceEqual(["V-F1", "V-F2", "V-F3", "V-L1", "V-L2", "V-L3", "V-L4", "V-L5", "V-L6", "V-R1"])
            && ruleRows["fact_source"].Select(r => r.RuleId).SequenceEqual(["V-S1", "V-S2a", "V-S2b", "V-S3", "V-S4"])
            && ruleRows["actor"].Select(r => r.RuleId).SequenceEqual(["V-A*", "V-A1"])
            && ruleRows["overdesign"].Select(r => r.RuleId).SequenceEqual(["V-G1", "V-G2", "V-G3"])
            && ruleRows["vendor"].Select(r => r.RuleId).SequenceEqual(["V-V*"]),
            "each dimension should carry exactly its own rules: V-F1/F2 belong to layering per the "
            + "rubrics F 组, and every catalog gap has an explicit placeholder (got: "
            + string.Join(" | ", report.Dimensions.Select(d => d.Dimension + ":" + string.Join(",", d.Rules.Select(r => r.RuleId)))) + ")");
        DepaRuleReport RuleRow(string dim, string id) => ruleRows[dim].Single(r => r.RuleId == id);
        Assert(RuleRow("effect", "V-E1").Verdict == "GAP" && RuleRow("effect", "V-E2").Verdict == "PASS"
            && RuleRow("data", "V-D1").Verdict == "GAP" && RuleRow("layering", "V-L3").Verdict == "PASS",
            "per-rule verdicts should surface on the rule rows");
        Assert(RuleRow("fact_source", "V-S1").Verdict == "BLOCKED"
            && RuleRow("fact_source", "V-S1").BlockedReason.Contains("projection", StringComparison.OrdinalIgnoreCase)
            && RuleRow("fact_source", "V-S1").Violations.Count == 0,
            "a BLOCKED rule row must carry the reason naming the missing input, and no violations");
        // Placeholder rules: each names the missing observation class (gap-matrix 分类：
        // 需语句级 AST / 需运行时语义 / 需人工语义比对), never a generic excuse and never PASS.
        var placeholderReasonPart = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["V-D2"] = "需运行时语义",
            ["V-P1"] = "需语句级 AST",
            ["V-P3"] = "需语句级 AST",
            ["V-P4"] = "需运行时语义",
            ["V-A*"] = "需运行时语义",
            ["V-G2"] = "需人工语义比对",
            ["V-G3"] = "需语句级 AST",
            ["V-V*"] = "需人工语义比对",
        };
        var placeholderRows = report.Dimensions.SelectMany(d => d.Rules)
            .Where(r => placeholderReasonPart.ContainsKey(r.RuleId))
            .ToArray();
        Assert(placeholderRows.Length == placeholderReasonPart.Count
            && placeholderRows.All(r => r.Verdict == "BLOCKED"
                && r.BlockedReason.Contains("静态观测不足", StringComparison.Ordinal)
                && r.BlockedReason.Contains(placeholderReasonPart[r.RuleId], StringComparison.Ordinal)
                && r.Violations.Count == 0),
            "every placeholder rule must report BLOCKED naming its missing observation class "
            + "(需语句级 AST / 需运行时语义 / 需人工语义比对) — never PASS (got: "
            + string.Join("; ", placeholderRows.Select(r => $"{r.RuleId}:{r.Verdict}:{r.BlockedReason}")) + ")");
        var f2Rows = RuleRow("layering", "V-F2").Violations;
        Assert(f2Rows.Count == 1 && f2Rows[0].Confidence == 0.8
            && f2Rows[0].Message.Contains("AppRuntime", StringComparison.Ordinal),
            "V-F2 should flag only the config-declared carrier at capped confidence (got: "
            + string.Join("; ", f2Rows.Select(v => $"{v.Confidence}:{v.Message}")) + ")");
        // carrier-false-positive-suppressed (delta case, track fix-om-depa-conformance-gaps):
        // a declared runtimeCarrierTypes list is closed-world — the "Context"-named record with
        // a business method must NOT be guessed into a carrier and flagged.
        Assert(!f2Rows.Any(v => v.Message.Contains("JobContext", StringComparison.Ordinal)),
            "declaring runtimeCarrierTypes must suppress the name-heuristic carrier false positive");
        Assert(report.Dimensions.SelectMany(d => d.Rules).SelectMany(r => r.Violations)
                .All(v => v.Evidence.Count > 0 && v.Evidence.All(e => e.Path.Length > 0 && e.Line > 0)),
            "every reported violation must keep its path:line evidence");
        Assert(report.StructuralFindings.Count(f => f.RuleId == "capsule_must_expose_entry") == 2,
            "existential findings should surface as structural findings, separate from the rule rows");

        // Dimension filter narrows the grouping; an unknown dimension is rejected, not guessed.
        var effectOnly = await repOm.GetConformanceReportAsync(new DepaConformanceOptions(MapPath: repMapPath, Dimension: "effect"));
        Assert(effectOnly.Dimensions.Single().Dimension == "effect"
            && effectOnly.Dimensions.Single().Rules.Select(r => r.RuleId).SequenceEqual(["V-E1", "V-E2", "V-E3"]),
            "the dimension filter should narrow the report to that dimension's rules");
        var badDimensionRejected = false;
        try
        {
            await repOm.GetConformanceReportAsync(new DepaConformanceOptions(Dimension: "bogus"));
        }
        catch (ArgumentException)
        {
            badDimensionRejected = true;
        }

        Assert(badDimensionRejected, "an unknown dimension filter should raise ArgumentException");

        // ScanFirst=false: aggregate persisted violations without re-detecting; rules without
        // hits degrade to BLOCKED (a rule that did not run cannot claim PASS).
        var persisted = await repOm.GetConformanceReportAsync(new DepaConformanceOptions(ScanFirst: false));
        Assert(!persisted.Scanned, "ScanFirst=false must not mark the report as scanned");
        var persistedRules = persisted.Dimensions.SelectMany(d => d.Rules).ToArray();
        var persistedE1 = persistedRules.Single(r => r.RuleId == "V-E1");
        Assert(persistedE1.Verdict == "GAP" && persistedE1.Violations.Single().Evidence.Single().Line == 88
            && persistedE1.Violations.Single().Evidence.Single().Path == "src/CapA/CoreLogic.cs"
            && persistedE1.Violations.Single().Message.Contains("System.IO.File.WriteAllText", StringComparison.Ordinal),
            "persisted violations must round-trip through evidence_json with their path:line evidence");
        Assert(persistedRules.Single(r => r.RuleId == "V-E2").Verdict == "BLOCKED"
            && persistedRules.Single(r => r.RuleId == "V-E2").BlockedReason.Contains("scan", StringComparison.OrdinalIgnoreCase),
            "without a scan, rules without persisted hits must report BLOCKED naming scanFirst, never PASS");
        var persistedF2 = persistedRules.Single(r => r.RuleId == "V-F2").Violations;
        Assert(persistedF2.Count == 1 && persistedF2[0].Confidence == 0.8,
            "persisted aggregation must round-trip exactly the materialized V-F2 violation");
        Assert(placeholderReasonPart.Keys
                .Select(id => persistedRules.Single(r => r.RuleId == id))
                .All(r => r.Verdict == "BLOCKED"
                    && r.BlockedReason.Contains("静态观测不足", StringComparison.Ordinal)
                    && r.Violations.Count == 0),
            "all eight placeholders must stay BLOCKED with the 静态观测不足 reason in the "
            + "persisted (ScanFirst=false) path too");

        // --- health-score-no-merge (delta tools case, API backing): per-dimension only ---
        var health = await repOm.GetHealthScoreAsync(new DepaConformanceOptions(MapPath: repMapPath));
        var healthByDim = health.Dimensions.ToDictionary(d => d.Dimension, StringComparer.Ordinal);
        Assert(health.Dimensions.Select(d => d.Dimension).SequenceEqual(expectedDims),
            "health rows should cover the eight dimensions in canonical order");
        Assert(healthByDim["effect"] is { GapCount: 1, BlockedCount: 0, RulesCovered: 3, RulesTotal: 3, Score: 0.6667 },
            $"effect health should be 1 gap / 3 covered (V-E3 lands in批次一, PASS here) (got {healthByDim["effect"]})");
        Assert(healthByDim["layering"] is { GapCount: 3, BlockedCount: 0, RulesCovered: 10, RulesTotal: 10, Score: 0.7 },
            "layering health spans the batch-1 rules (V-F3/V-L2/V-L4/V-L5/V-L6/V-R1 all run and "
            + $"PASS here; 3 gaps over V-F1/V-F2/V-L1 → score 0.7) (got {healthByDim["layering"]})");
        Assert(healthByDim["processor"] is { GapCount: 0, BlockedCount: 5, RulesCovered: 0, RulesTotal: 5, Score: 0 },
            "processor health counts V-C1 (BLOCKED here: no parameter symbols observed under any "
            + "core) and V-P2 (BLOCKED: no layers declared) next to the three placeholders — "
            + $"coverage must not be inflated (got {healthByDim["processor"]})");
        Assert(healthByDim["data"] is { GapCount: 1, BlockedCount: 2, RulesCovered: 1, RulesTotal: 3, Score: 0 },
            "data health must count the V-D2 placeholder and the V-D3 missing-projections BLOCKED "
            + $"in RulesTotal (1/3 covered, not a fake 1/1) (got {healthByDim["data"]})");
        Assert(healthByDim["fact_source"] is { GapCount: 0, BlockedCount: 4, RulesCovered: 1, RulesTotal: 5, Score: 0.2 },
            "fact_source health: V-S1/V-S3 BLOCKED on missing inputs, V-S2a/V-S2b BLOCKED without "
            + $"recoveryPaths, V-S4 runs and passes — BLOCKED is not compliance (got {healthByDim["fact_source"]})");
        Assert(healthByDim["actor"] is { GapCount: 0, BlockedCount: 1, RulesCovered: 1, RulesTotal: 2, Score: 0.5 },
            "the actor dimension pairs the V-A* placeholder with the V-A1 phenomenon detector "
            + $"(no threading density here → PASS) (got {healthByDim["actor"]})");
        Assert(healthByDim["overdesign"] is { GapCount: 0, BlockedCount: 2, RulesCovered: 1, RulesTotal: 3, Score: 0.3333 },
            "the overdesign dimension gains its first real detector (V-G1, PASS here — the single "
            + $"contract impl is the paradigm's own pattern), placeholders stay BLOCKED (got {healthByDim["overdesign"]})");
        Assert(healthByDim["vendor"] is { GapCount: 0, BlockedCount: 1, RulesCovered: 0, RulesTotal: 1, Score: 0 },
            $"the vendor dimension is all placeholder (V-V* BLOCKED), never PASS (got {healthByDim["vendor"]})");
        Assert(typeof(DepaHealthScore).GetProperties().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal)
                .SequenceEqual(["Dimensions", "Note"]),
            "the health score must expose per-dimension rows and a note only — no merged overall field");
        Assert(health.Note.Contains("independently", StringComparison.Ordinal)
            && health.Note.Contains("never", StringComparison.Ordinal),
            "the note must state that dimensions are judged independently and never merged");

        // --- rule-map-anchored (delta case, track expand-depa-detection-rules T1.1):
        // src/Om.Depa/rubrics/rule-map.md 三方映射（catalog 条目 → 工具规则 → 状态）双向完整。
        {
            static string FindRepoFile(string relative)
            {
                for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
                {
                    var candidate = Path.Combine(dir.FullName, relative);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }

                throw new InvalidOperationException($"cannot locate {relative} above {Directory.GetCurrentDirectory()}");
            }

            var ruleMapPath = FindRepoFile(Path.Combine("src", "Om.Depa", "rubrics", "rule-map.md"));
            var mapRows = File.ReadAllLines(ruleMapPath)
                .Where(l => l.StartsWith("| ", StringComparison.Ordinal))
                .Select(l => l.Split('|').Select(c => c.Trim()).ToArray())
                .Where(c => c.Length >= 6 && c[1] != "条目" && !c[1].StartsWith("---", StringComparison.Ordinal) && c[1].Length > 0)
                .Select(c => (CatalogId: c[1], Dimension: c[3], RuleId: c[4].Trim('`'), Status: c[5]))
                .ToArray();
            Assert(mapRows.Length >= 35, $"rule-map.md must enumerate at least the 35 catalog rows (got {mapRows.Length})");

            // 目录侧完整：violation-catalog A1..G5 全部 35 条都有映射行。
            string[] requiredCatalogIds =
            [
                .. Enumerable.Range(1, 4).Select(i => $"A{i}"), .. Enumerable.Range(1, 4).Select(i => $"B{i}"),
                .. Enumerable.Range(1, 4).Select(i => $"C{i}"), .. Enumerable.Range(1, 5).Select(i => $"D{i}"),
                .. Enumerable.Range(1, 6).Select(i => $"E{i}"), .. Enumerable.Range(1, 7).Select(i => $"F{i}"),
                .. Enumerable.Range(1, 5).Select(i => $"G{i}"),
            ];
            var mappedCatalogIds = mapRows.Select(r => r.CatalogId).ToHashSet(StringComparer.Ordinal);
            Assert(requiredCatalogIds.All(mappedCatalogIds.Contains),
                "every violation-catalog row A1..G5 must appear in rule-map.md (missing: "
                + string.Join(", ", requiredCatalogIds.Where(id => !mappedCatalogIds.Contains(id))) + ")");

            string[] validStatuses = ["implemented", "implemented-weakened", "placeholder-BLOCKED", "planned-batch1", "planned-batch2", "human-only"];
            Assert(mapRows.All(r => validStatuses.Contains(r.Status, StringComparer.Ordinal)),
                "every rule-map row must carry a status from the fixed vocabulary (bad: "
                + string.Join("; ", mapRows.Where(r => !validStatuses.Contains(r.Status, StringComparer.Ordinal)).Select(r => $"{r.CatalogId}:{r.Status}")) + ")");

            // 工具侧 → 映射侧：报告分母里的每条规则在 rule-map 中有实现/占位行。
            var toolRules = report.Dimensions.SelectMany(d => d.Rules.Select(r => r.RuleId)).ToHashSet(StringComparer.Ordinal);
            string[] liveStatuses = ["implemented", "implemented-weakened", "placeholder-BLOCKED"];
            Assert(toolRules.All(rule => mapRows.Any(r => r.RuleId == rule && liveStatuses.Contains(r.Status, StringComparer.Ordinal))),
                "every rule in the tool denominator must have an implemented/placeholder rule-map row (missing: "
                + string.Join(", ", toolRules.Where(rule => !mapRows.Any(r => r.RuleId == rule && liveStatuses.Contains(r.Status, StringComparer.Ordinal)))) + ")");
            Assert(placeholderReasonPart.Keys.All(rule => mapRows.Any(r => r.RuleId == rule && r.Status == "placeholder-BLOCKED")),
                "each of the eight placeholder rules must be marked placeholder-BLOCKED in rule-map.md");

            // 映射侧 → 工具侧：标 implemented/占位 的行必须真的在工具分母里；planned 的不得已在分母
            // 里冒充（除非该 id 同时有 implemented 行，如 V-S1 的批次一扩展）；human-only 不进分母。
            Assert(mapRows.Where(r => liveStatuses.Contains(r.Status, StringComparer.Ordinal)).All(r => toolRules.Contains(r.RuleId)),
                "rule-map rows marked implemented/placeholder must exist in the tool denominator (stale: "
                + string.Join(", ", mapRows.Where(r => liveStatuses.Contains(r.Status, StringComparer.Ordinal) && !toolRules.Contains(r.RuleId)).Select(r => r.RuleId)) + ")");
            var implementedIds = mapRows.Where(r => r.Status is "implemented" or "implemented-weakened").Select(r => r.RuleId).ToHashSet(StringComparer.Ordinal);
            Assert(mapRows.Where(r => r.Status is "planned-batch1" or "planned-batch2")
                    .All(r => implementedIds.Contains(r.RuleId) || !toolRules.Contains(r.RuleId)),
                "planned rules must not already sit in the tool denominator pretending to be live");
            var humanOnly = mapRows.Where(r => r.Status == "human-only").ToArray();
            Assert(humanOnly.Length == 4 && humanOnly.All(r => r.RuleId == "—"),
                "exactly the 4 human-only catalog rows (D3/D4/G4/G5) carry no tool rule and stay out "
                + $"of the tool denominator (got {humanOnly.Length}: "
                + string.Join(", ", humanOnly.Select(r => $"{r.CatalogId}:{r.RuleId}")) + ")");
            Assert(mapRows.Where(r => r.Status != "human-only").All(r => r.RuleId.StartsWith("V-", StringComparison.Ordinal)),
                "every non-human-only row must name a V-* tool rule id");

            // gap-matrix 口径：批次一 14 条已于 T2.1 落地、批次二 3 条已于 T3.1 落地 —— 不得再有
            // planned 行（按规则 id 去重，D5/CP2§5 共用 V-L2）。
            var batch1Ids = mapRows.Where(r => r.Status == "planned-batch1").Select(r => r.RuleId).Distinct(StringComparer.Ordinal).ToArray();
            var batch2Ids = mapRows.Where(r => r.Status == "planned-batch2").Select(r => r.RuleId).Distinct(StringComparer.Ordinal).ToArray();
            Assert(batch1Ids.Length == 0 && batch2Ids.Length == 0,
                "both batches landed (T2.1/T3.1): no rule-map row may still claim planned-batch1/planned-batch2, got "
                + $"{batch1Ids.Length} ({string.Join(",", batch1Ids)}) + {batch2Ids.Length} ({string.Join(",", batch2Ids)})");
            // The 14 batch-1 gap-matrix rows resolve to 13 distinct new rule ids + the V-S1
            // extension (same id); batch-2 (T3.1) adds V-S2a/V-S2b/V-P2. All must now be
            // implemented/implemented-weakened.
            string[] batchLandedIds =
            [
                "V-A1", "V-C1", "V-D3", "V-E3", "V-F3", "V-G1", "V-L2", "V-L4", "V-L5", "V-L6", "V-R1", "V-S1", "V-S3", "V-S4",
                "V-P2", "V-S2a", "V-S2b",
            ];
            Assert(batchLandedIds.All(id => mapRows.Any(r => r.RuleId == id && r.Status is "implemented" or "implemented-weakened")),
                "every batch-1/batch-2 rule id must carry an implemented/implemented-weakened rule-map row (missing: "
                + string.Join(", ", batchLandedIds.Where(id => !mapRows.Any(r => r.RuleId == id && r.Status is "implemented" or "implemented-weakened"))) + ")");

            // rubrics 真源就位：违规目录 + 5 张核查表 + fact-source-truth 复制进项目，带来源注记。
            string[] rubricFiles =
            [
                "violation-catalog.md", "component-protocol.md", "capsule-protocol.md",
                "depa-conformance.md", "runtime-explicitness.md", "fact-grade-classification.md",
                "fact-source-truth.md",
            ];
            var rubricsDir = Path.GetDirectoryName(ruleMapPath)!;
            Assert(rubricFiles.All(f => File.Exists(Path.Combine(rubricsDir, f))),
                "all seven rubric files must be copied into src/Om.Depa/rubrics/ (missing: "
                + string.Join(", ", rubricFiles.Where(f => !File.Exists(Path.Combine(rubricsDir, f)))) + ")");
            Assert(rubricFiles.All(f => File.ReadLines(Path.Combine(rubricsDir, f)).Take(3)
                    .Any(l => l.Contains("复制自", StringComparison.Ordinal) && l.Contains("depa-expert", StringComparison.Ordinal))),
                "each copied rubric must open with the 来源注记 (复制自 ~/.claude/skills/depa-expert…)");
        }

        // --- fact-grade-map (delta tools case, API backing): nodes + adjacency, manual links included ---
        const string ledgerId = "depa:factsource:csharp:Rep.Ledger.SchemaLedger#0";
        const string eventLogId = "depa:factsource:csharp:Rep.Ledger.EventLog#0";
        await repOm.UpsertEntityAsync("depa:projection:ManualView", "depa_projection", "ManualView");
        await repOm.LinkEntitiesAsync("depa:projection:ManualView", "projection_derived_from", eventLogId);
        var gradeMap = await repOm.GetFactGradeMapAsync();
        var ledgerNode = gradeMap.FactSources.Single(n => n.EntityId == ledgerId);
        Assert(ledgerNode.Grade == 1 && ledgerNode.GradeId == "authoritative_fact"
            && ledgerNode.Path == "src/CapA/Ledger.cs" && ledgerNode.Line == 7,
            $"the grade map node should carry grade/grade_id and the anchor path:line (got {ledgerNode})");
        Assert(gradeMap.FactSources.Select(n => n.Grade).SequenceEqual([1, 2]),
            "grade map nodes should be ordered by grade");
        Assert(gradeMap.Edges.Count(e => e.Relation == "fact_written_by" && e.FromId == ledgerId) == 2,
            "both observed ledger writers should appear as fact_written_by adjacency");
        var derived = gradeMap.Edges.Single(e => e.Relation == "projection_derived_from");
        Assert(derived.FromId == "depa:projection:ManualView" && derived.ToId == eventLogId,
            "manually asserted projection_derived_from links should appear in the grade map");
    }
    finally
    {
        repTmp.Delete(recursive: true);
    }
}

// === DEPA batch-1 detectors — 14 zero-new-annotation rules
// (track expand-depa-detection-rules T2.1, delta case batch1-detectors:
// 正例 GAP 带 path:line、反例不误报；evidence/confidence 纪律沿用) ===
{
    var b1Tmp = Directory.CreateTempSubdirectory("depa-batch1-tests-");
    try
    {
        // ---- group L: V-L4 multi-entry / V-L2 capsule cycle / V-L5 contract leaks internal
        // type / V-L6 duplicate contract definition ----
        {
            var mapPath = Path.Combine(b1Tmp.FullName, "l-map.json");
            File.WriteAllText(mapPath, """
            {
              "capsules": [
                { "name": "CapA", "rootPath": "src/CapA" },
                { "name": "CapB", "rootPath": "src/CapB" },
                { "name": "CapC", "rootPath": "src/CapC" }
              ],
              "contractPackages": ["src/CapA/Contracts"]
            }
            """);
            using var b1lDb = new CozoDb(engine: "mem", path: "");
            var b1lOm = new CozoOm(b1lDb);
            await b1lOm.InitCodeKnowledgeAsync();
            await b1lOm.IndexCodeKnowledgeAsync(new CodeKnowledgeBatch(
                Repositories: [new CodeRepositoryFact("repo:l", "/repo/l")],
                Files:
                [
                    new CodeFileFact("file:l1", "repo:l", "src/CapA/Contracts/IPort.cs", "csharp"),
                    new CodeFileFact("file:l2", "repo:l", "src/CapA/Contracts/IClean.cs", "csharp"),
                    new CodeFileFact("file:l9", "repo:l", "src/CapA/Contracts/IPayload.cs", "csharp"),
                    new CodeFileFact("file:l3", "repo:l", "src/CapA/Impls.cs", "csharp"),
                    new CodeFileFact("file:l4", "repo:l", "src/CapA/Api.cs", "csharp"),
                    new CodeFileFact("file:l5", "repo:l", "src/CapB/Worker.cs", "csharp"),
                    new CodeFileFact("file:l6", "repo:l", "src/CapC/Helper.cs", "csharp"),
                    new CodeFileFact("file:l7", "repo:l", "src/CapB/Internals/SecretRec.cs", "csharp"),
                    new CodeFileFact("file:l8", "repo:l", "src/CapC/Types/IPayload.cs", "csharp"),
                ],
                Symbols:
                [
                    new CodeSymbolFact("sym:iport", "file:l1", "IPort", "interface", 3, 8, SymKey: "csharp:L.IPort#0"),
                    // V-L5: the contract member's signature references CapB's internal type.
                    new CodeSymbolFact("sym:handle", "file:l1", "Handle", "method", 5, 5, Signature: "SecretRec Handle(int request)", SymKey: "csharp:L.IPort.Handle#1"),
                    new CodeSymbolFact("sym:iclean", "file:l2", "IClean", "interface", 3, 8, SymKey: "csharp:L.IClean#0"),
                    new CodeSymbolFact("sym:go", "file:l2", "Go", "method", 5, 5, Signature: "int Go(int request)", SymKey: "csharp:L.IClean.Go#1"),
                    // V-L6: same-name contract type defined in the contract package AND in CapC.
                    new CodeSymbolFact("sym:ipayc", "file:l9", "IPayload", "interface", 3, 8, SymKey: "csharp:L.Contracts.IPayload#0"),
                    new CodeSymbolFact("sym:ipayd", "file:l8", "IPayload", "interface", 3, 8, SymKey: "csharp:L.CapC.IPayload#0"),
                    new CodeSymbolFact("sym:portimpl", "file:l3", "PortImpl", "class", 3, 20, SymKey: "csharp:L.PortImpl#0"),
                    new CodeSymbolFact("sym:cleanimpl", "file:l3", "CleanImpl", "class", 22, 40, SymKey: "csharp:L.CleanImpl#0"),
                    new CodeSymbolFact("sym:paylimpl", "file:l3", "PayloadImpl", "class", 42, 60, SymKey: "csharp:L.PayloadImpl#0"),
                    // V-L4: two public entries exposed by CapA.
                    new CodeSymbolFact("sym:runa", "file:l4", "Run", "method", 6, 10, SymKey: "csharp:L.Api.Run#0"),
                    new CodeSymbolFact("sym:servea", "file:l4", "Serve", "method", 12, 16, SymKey: "csharp:L.Api.Serve#0"),
                    new CodeSymbolFact("sym:workerb", "file:l5", "Worker", "class", 3, 30, SymKey: "csharp:L.Worker#0"),
                    new CodeSymbolFact("sym:pumpb", "file:l5", "Pump", "method", 8, 14, SymKey: "csharp:L.Worker.Pump#0"),
                    new CodeSymbolFact("sym:entryb", "file:l5", "EntryB", "method", 20, 24, SymKey: "csharp:L.Worker.EntryB#0"),
                    new CodeSymbolFact("sym:helperc", "file:l6", "Helper", "class", 3, 30, SymKey: "csharp:L.Helper#0"),
                    new CodeSymbolFact("sym:assistc", "file:l6", "Assist", "method", 8, 14, SymKey: "csharp:L.Helper.Assist#0"),
                    new CodeSymbolFact("sym:entryc", "file:l6", "EntryC", "method", 20, 24, SymKey: "csharp:L.Helper.EntryC#0"),
                    new CodeSymbolFact("sym:secretrec", "file:l7", "SecretRec", "class", 3, 12, SymKey: "csharp:L.SecretRec#0"),
                ],
                Edges:
                [
                    new CodeEdgeFact("sym:iport", "sym:handle", CodeEdgeKinds.Contains, "file:l1", 5, 1.0, "roslyn"),
                    new CodeEdgeFact("sym:iclean", "sym:go", CodeEdgeKinds.Contains, "file:l2", 5, 1.0, "roslyn"),
                    new CodeEdgeFact("sym:portimpl", "sym:iport", CodeEdgeKinds.Implements, "file:l3", 3, 0.9, "roslyn"),
                    new CodeEdgeFact("sym:cleanimpl", "sym:iclean", CodeEdgeKinds.Implements, "file:l3", 22, 0.9, "roslyn"),
                    new CodeEdgeFact("sym:paylimpl", "sym:ipayc", CodeEdgeKinds.Implements, "file:l3", 42, 0.9, "roslyn"),
                    new CodeEdgeFact("sym:workerb", "sym:pumpb", CodeEdgeKinds.Contains, "file:l5", 8, 1.0, "roslyn"),
                    new CodeEdgeFact("sym:helperc", "sym:assistc", CodeEdgeKinds.Contains, "file:l6", 8, 1.0, "roslyn"),
                    // V-L2: CapB -> CapC -> CapB dependency cycle (no internals touched).
                    new CodeEdgeFact("sym:pumpb", "sym:assistc", CodeEdgeKinds.Calls, "file:l5", 10, 1.0, "roslyn", "semantic"),
                    new CodeEdgeFact("sym:assistc", "sym:pumpb", CodeEdgeKinds.Calls, "file:l6", 10, 1.0, "roslyn", "semantic"),
                ],
                EntryPoints:
                [
                    new CodeEntryPointFact("sym:runa", "public_api"),
                    new CodeEntryPointFact("sym:servea", "public_api"),
                    new CodeEntryPointFact("sym:entryb", "public_api"),
                    new CodeEntryPointFact("sym:entryc", "public_api"),
                ]));

            var b1lR = await b1lOm.DepaScanAsync(new DepaScanOptions(MapPath: mapPath));
            string[] lRules = ["V-L2", "V-L4", "V-L5", "V-L6"];
            Assert(lRules.All(rule => b1lR.DetectionVerdicts[rule] == "GAP"),
                "all four staged L-group red lights should be GAP: "
                + string.Join("; ", lRules.Select(rule => $"{rule}:{b1lR.DetectionVerdicts[rule]}")));
            Assert(b1lR.Violations.All(v => v.Evidence.Count > 0 && v.Evidence.All(e => e.Path.Length > 0 && e.Line > 0)),
                "every L-group violation must carry path:line evidence");
            Assert(b1lR.Violations.Where(v => lRules.Contains(v.RuleId)).All(v => v.Dimension == "layering"),
                "all L-group rules belong to the layering dimension per rule-map.md");

            var l4 = b1lR.Violations.Single(v => v.RuleId == "V-L4");
            Assert(l4.SubjectEntityId == "depa:capsule:CapA"
                && l4.Evidence.Count == 2
                && l4.Evidence.All(e => e.Path == "src/CapA/Api.cs")
                && l4.Evidence.Select(e => e.Line).OrderBy(l => l).SequenceEqual([6, 12])
                && l4.Message.Contains("2", StringComparison.Ordinal),
                $"V-L4 should flag CapA's two exposed entries with both entry sites (got {l4.SubjectEntityId}: {string.Join(", ", l4.Evidence.Select(e => $"{e.Path}:{e.Line}"))})");
            Assert(!b1lR.Violations.Any(v => v.RuleId == "V-L4" && v.SubjectEntityId != "depa:capsule:CapA"),
                "single-entry capsules (CapB/CapC) must not be flagged by V-L4");

            var l2 = b1lR.Violations.Single(v => v.RuleId == "V-L2");
            Assert(l2.Message.Contains("CapB", StringComparison.Ordinal) && l2.Message.Contains("CapC", StringComparison.Ordinal)
                && !l2.Message.Contains("CapA", StringComparison.Ordinal)
                && l2.Evidence.Count == 2
                && l2.Evidence.Any(e => e.Path == "src/CapB/Worker.cs" && e.Line == 10)
                && l2.Evidence.Any(e => e.Path == "src/CapC/Helper.cs" && e.Line == 10),
                $"V-L2 should report the CapB<->CapC cycle with one crossing-edge evidence per direction (got '{l2.Message}': {string.Join(", ", l2.Evidence.Select(e => $"{e.Path}:{e.Line}"))})");

            var l5 = b1lR.Violations.Single(v => v.RuleId == "V-L5");
            Assert(l5.SubjectEntityId == "depa:contract:csharp:L.IPort#0"
                && l5.Message.Contains("SecretRec", StringComparison.Ordinal)
                && l5.Evidence.Single().Path == "src/CapA/Contracts/IPort.cs" && l5.Evidence.Single().Line == 5,
                $"V-L5 should flag the contract member whose signature references CapB's internal type (got {l5.SubjectEntityId} '{l5.Message}')");
            Assert(!b1lR.Violations.Any(v => v.RuleId == "V-L5" && v.Message.Contains("IClean", StringComparison.Ordinal)),
                "a contract with internal-free signatures must not be flagged by V-L5");

            var l6 = b1lR.Violations.Single(v => v.RuleId == "V-L6");
            Assert(l6.Message.Contains("IPayload", StringComparison.Ordinal)
                && l6.Evidence.Count == 2
                && l6.Evidence.Any(e => e.Path == "src/CapA/Contracts/IPayload.cs")
                && l6.Evidence.Any(e => e.Path == "src/CapC/Types/IPayload.cs"),
                $"V-L6 should flag the duplicate contract definition with both declaration sites (got '{l6.Message}': {string.Join(", ", l6.Evidence.Select(e => $"{e.Path}:{e.Line}"))})");
            Assert(!b1lR.Violations.Any(v => v.RuleId == "V-L6" && (v.Message.Contains("IClean", StringComparison.Ordinal) || v.Message.Contains("IPort", StringComparison.Ordinal))),
                "uniquely-defined contracts must not be flagged by V-L6");
            Assert(!b1lR.Violations.Any(v => v.RuleId == "V-L1"),
                "the CapB<->CapC cycle edges do not touch internals — V-L1 must stay clean");
        }

        // ---- group F/R/C/A: V-F3 config duplicates runtime field / V-R1 runtime big-bag
        // lexicon / V-C1 fn(b1fR,i,c) coverage / V-A1 lock-density phenomenon ----
        {
            var mapPath = Path.Combine(b1Tmp.FullName, "f-map.json");
            File.WriteAllText(mapPath, """
            {
              "capsules": [ { "name": "CapF", "rootPath": "src/CapF" } ],
              "cores": ["Engine", "GoodCore"],
              "runtimeCarrierTypes": ["AppRt"],
              "runtimeParams": [
                { "symbolOrPath": "opts", "role": "config", "declaredType": "JobCfg" },
                { "symbolOrPath": "ctx", "role": "runtime", "declaredType": "Dictionary<string, object>" },
                { "symbolOrPath": "ecfg", "role": "config" },
                { "symbolOrPath": "grt", "role": "runtime" },
                { "symbolOrPath": "ginp", "role": "input" },
                { "symbolOrPath": "gcfg", "role": "config" }
              ]
            }
            """);
            using var b1fDb = new CozoDb(engine: "mem", path: "");
            var b1fOm = new CozoOm(b1fDb);
            await b1fOm.InitCodeKnowledgeAsync();
            await b1fOm.IndexCodeKnowledgeAsync(new CodeKnowledgeBatch(
                Repositories: [new CodeRepositoryFact("repo:f", "/repo/f")],
                Files:
                [
                    new CodeFileFact("file:f1", "repo:f", "src/CapF/JobCfg.cs", "csharp"),
                    new CodeFileFact("file:f2", "repo:f", "src/CapF/AppRt.cs", "csharp"),
                    new CodeFileFact("file:f3", "repo:f", "src/CapF/Engine.cs", "csharp"),
                    new CodeFileFact("file:f4", "repo:f", "src/CapF/GoodCore.cs", "csharp"),
                    new CodeFileFact("file:f5", "repo:f", "src/CapF/Api.cs", "csharp"),
                    new CodeFileFact("file:f6", "repo:f", "src/CapF/SharedState.cs", "csharp"),
                ],
                Symbols:
                [
                    new CodeSymbolFact("sym:jobcfg", "file:f1", "JobCfg", "class", 3, 12, SymKey: "csharp:F.JobCfg#0"),
                    // V-F3: same field name+type in the config type and the runtime carrier.
                    new CodeSymbolFact("sym:cfgstore", "file:f1", "StoreDup", "property", 6, 6, Signature: "public IStore StoreDup { get; set; }", SymKey: "csharp:F.JobCfg.StoreDup#0"),
                    new CodeSymbolFact("sym:cfgretries", "file:f1", "Retries", "property", 8, 8, Signature: "public int Retries { get; set; }", SymKey: "csharp:F.JobCfg.Retries#0"),
                    new CodeSymbolFact("sym:apprt", "file:f2", "AppRt", "class", 3, 14, SymKey: "csharp:F.AppRt#0"),
                    new CodeSymbolFact("sym:rtstore", "file:f2", "StoreDup", "property", 6, 6, Signature: "public IStore StoreDup { get; set; }", SymKey: "csharp:F.AppRt.StoreDup#0"),
                    // V-R1: a big-bag field on the runtime carrier (plus a benign typed one).
                    new CodeSymbolFact("sym:rtextras", "file:f2", "Extras", "property", 8, 8, Signature: "public Dictionary<string, object> Extras { get; set; }", SymKey: "csharp:F.AppRt.Extras#0"),
                    new CodeSymbolFact("sym:rtclock", "file:f2", "Clock", "property", 10, 10, Signature: "public TimeProvider Clock { get; set; }", SymKey: "csharp:F.AppRt.Clock#0"),
                    // V-C1: Engine's only role-annotated parameter is config — runtime/input missing.
                    new CodeSymbolFact("sym:engine", "file:f3", "Engine", "class", 3, 30, SymKey: "csharp:F.Engine#0"),
                    new CodeSymbolFact("sym:rune", "file:f3", "RunE", "method", 10, 20, SymKey: "csharp:F.Engine.RunE#1"),
                    new CodeSymbolFact("sym:ecfg", "file:f3", "ecfg", "parameter", 10, 10, SymKey: "csharp:F.Engine.RunE.ecfg#0"),
                    new CodeSymbolFact("sym:goodcore", "file:f4", "GoodCore", "class", 3, 30, SymKey: "csharp:F.GoodCore#0"),
                    new CodeSymbolFact("sym:dog", "file:f4", "DoG", "method", 10, 20, SymKey: "csharp:F.GoodCore.DoG#3"),
                    new CodeSymbolFact("sym:grt", "file:f4", "grt", "parameter", 10, 10, SymKey: "csharp:F.GoodCore.DoG.grt#0"),
                    new CodeSymbolFact("sym:ginp", "file:f4", "ginp", "parameter", 10, 10, SymKey: "csharp:F.GoodCore.DoG.ginp#0"),
                    new CodeSymbolFact("sym:gcfg", "file:f4", "gcfg", "parameter", 10, 10, SymKey: "csharp:F.GoodCore.DoG.gcfg#0"),
                    new CodeSymbolFact("sym:apirun", "file:f5", "ApiRun", "method", 5, 20, SymKey: "csharp:F.Api.ApiRun#2"),
                    new CodeSymbolFact("sym:opts", "file:f5", "opts", "parameter", 5, 5, SymKey: "csharp:F.Api.ApiRun.opts#0"),
                    // V-R1: runtime-role parameter declared as Dictionary<string, object>.
                    new CodeSymbolFact("sym:ctx", "file:f5", "ctx", "parameter", 6, 6, SymKey: "csharp:F.Api.ApiRun.ctx#0"),
                    new CodeSymbolFact("sym:sharedstate", "file:f6", "SharedState", "class", 3, 20, SymKey: "csharp:F.SharedState#0"),
                    new CodeSymbolFact("sym:sync", "file:f6", "Sync", "method", 8, 16, SymKey: "csharp:F.SharedState.Sync#0"),
                ],
                Edges:
                [
                    new CodeEdgeFact("sym:jobcfg", "sym:cfgstore", CodeEdgeKinds.HasProperty, "file:f1", 6, 1.0, "roslyn"),
                    new CodeEdgeFact("sym:jobcfg", "sym:cfgretries", CodeEdgeKinds.HasProperty, "file:f1", 8, 1.0, "roslyn"),
                    new CodeEdgeFact("sym:apprt", "sym:rtstore", CodeEdgeKinds.HasProperty, "file:f2", 6, 1.0, "roslyn"),
                    new CodeEdgeFact("sym:apprt", "sym:rtextras", CodeEdgeKinds.HasProperty, "file:f2", 8, 1.0, "roslyn"),
                    new CodeEdgeFact("sym:apprt", "sym:rtclock", CodeEdgeKinds.HasProperty, "file:f2", 10, 1.0, "roslyn"),
                    new CodeEdgeFact("sym:engine", "sym:rune", CodeEdgeKinds.Contains, "file:f3", 10, 1.0, "roslyn"),
                    new CodeEdgeFact("sym:rune", "sym:ecfg", CodeEdgeKinds.Contains, "file:f3", 10, 1.0, "roslyn"),
                    new CodeEdgeFact("sym:goodcore", "sym:dog", CodeEdgeKinds.Contains, "file:f4", 10, 1.0, "roslyn"),
                    new CodeEdgeFact("sym:dog", "sym:grt", CodeEdgeKinds.Contains, "file:f4", 10, 1.0, "roslyn"),
                    new CodeEdgeFact("sym:dog", "sym:ginp", CodeEdgeKinds.Contains, "file:f4", 10, 1.0, "roslyn"),
                    new CodeEdgeFact("sym:dog", "sym:gcfg", CodeEdgeKinds.Contains, "file:f4", 10, 1.0, "roslyn"),
                    new CodeEdgeFact("sym:sharedstate", "sym:sync", CodeEdgeKinds.Contains, "file:f6", 8, 1.0, "roslyn"),
                ],
                ExternalCalls:
                [
                    // V-A1: threading density >= 3 on one symbol (phenomenon-level).
                    new CodeExternalCallFact("sym:sync", "System.Threading.Monitor.Enter", 2, Category: "threading", FirstFileId: "file:f6", FirstLine: 9, Resolver: "roslyn"),
                    new CodeExternalCallFact("sym:sync", "System.Threading.Interlocked.Increment", 2, Category: "threading", FirstFileId: "file:f6", FirstLine: 11, Resolver: "roslyn"),
                    // Below the density threshold — must not be flagged.
                    new CodeExternalCallFact("sym:apirun", "System.Threading.Monitor.Enter", 2, Category: "threading", FirstFileId: "file:f5", FirstLine: 7, Resolver: "roslyn"),
                ]));

            var b1fR = await b1fOm.DepaScanAsync(new DepaScanOptions(MapPath: mapPath));
            Assert(b1fR.DetectionVerdicts["V-F3"] == "GAP" && b1fR.DetectionVerdicts["V-R1"] == "GAP"
                && b1fR.DetectionVerdicts["V-C1"] == "GAP" && b1fR.DetectionVerdicts["V-A1"] == "GAP",
                "all four staged F/R/C/A-group red lights should be GAP: "
                + string.Join("; ", new[] { "V-F3", "V-R1", "V-C1", "V-A1" }.Select(rule => $"{rule}:{b1fR.DetectionVerdicts[rule]}")));
            Assert(b1fR.DetectionVerdicts["V-E1"] == "PASS" && b1fR.DetectionVerdicts["V-F1"] == "PASS",
                "clean cores and a function-free config type must keep V-E1/V-F1 PASS");
            Assert(b1fR.Violations.All(v => v.Evidence.Count > 0 && v.Evidence.All(e => e.Path.Length > 0 && e.Line > 0)),
                "every F/R/C/A-group violation must carry path:line evidence");

            var f3 = b1fR.Violations.Single(v => v.RuleId == "V-F3");
            Assert(f3.Dimension == "layering"
                && f3.Message.Contains("StoreDup", StringComparison.Ordinal)
                && !f3.Message.Contains("Retries", StringComparison.Ordinal)
                && f3.Evidence.Count == 2
                && f3.Evidence.Any(e => e.Path == "src/CapF/JobCfg.cs" && e.Line == 6)
                && f3.Evidence.Any(e => e.Path == "src/CapF/AppRt.cs" && e.Line == 6),
                $"V-F3 should flag exactly the duplicated field with both declaration sites (got '{f3.Message}': {string.Join(", ", f3.Evidence.Select(e => $"{e.Path}:{e.Line}"))})");

            var r1s = b1fR.Violations.Where(v => v.RuleId == "V-R1").ToArray();
            Assert(r1s.Length == 2
                && r1s.Any(v => v.Evidence[0].Path == "src/CapF/Api.cs" && v.Evidence[0].Line == 6)
                && r1s.Any(v => v.Evidence[0].Path == "src/CapF/AppRt.cs" && v.Evidence[0].Line == 8)
                && r1s.All(v => v.Confidence <= 0.7 && v.Dimension == "layering"
                    && v.Evidence[0].Detail.Contains("heuristic", StringComparison.Ordinal))
                && !r1s.Any(v => v.Message.Contains("Clock", StringComparison.Ordinal)),
                "V-R1 should flag the bag-typed runtime param and carrier field (lexicon phenomenon, conf<=0.7, heuristic-marked evidence), not the typed field: "
                + string.Join("; ", r1s.Select(v => $"{v.Confidence}:{v.Message}@{v.Evidence[0].Path}:{v.Evidence[0].Line}")));

            var c1 = b1fR.Violations.Single(v => v.RuleId == "V-C1");
            Assert(c1.SubjectEntityId == "depa:impl:csharp:F.Engine#0" && c1.Dimension == "processor"
                && c1.Message.Contains("runtime", StringComparison.Ordinal) && c1.Message.Contains("input", StringComparison.Ordinal)
                && c1.Evidence[0].Path == "src/CapF/Engine.cs" && c1.Evidence[0].Line > 0,
                $"V-C1 should flag the core whose fn(runtime,input,config) role coverage is incomplete, naming the missing roles (got {c1.SubjectEntityId} '{c1.Message}')");
            Assert(!b1fR.Violations.Any(v => v.RuleId == "V-C1" && v.SubjectEntityId.Contains("GoodCore", StringComparison.Ordinal)),
                "a core with full runtime/input/config role coverage must not be flagged by V-C1");

            var a1 = b1fR.Violations.Single(v => v.RuleId == "V-A1");
            Assert(a1.Dimension == "actor" && a1.Confidence <= 0.6
                && a1.SubjectEntityId == "depa:capsule:CapF"
                && a1.Evidence.Any(e => e.Path == "src/CapF/SharedState.cs" && e.Line == 9)
                && a1.Evidence.All(e => e.Detail.Contains("heuristic", StringComparison.Ordinal)),
                $"V-A1 should flag the lock-density phenomenon at conf<=0.6 with heuristic-marked evidence (got conf={a1.Confidence} '{a1.Message}')");
            Assert(!b1fR.Violations.Any(v => v.RuleId == "V-A1" && v.Evidence.Any(e => e.Path == "src/CapF/Api.cs")),
                "a symbol below the threading-density threshold must not be flagged by V-A1");
        }

        // ---- group S/D: V-S1 extension (grade-6/7 writes grade<=3) / V-S3 snapshot carries
        // payload / V-S4 file-metadata probe / V-D3 projection-fact entanglement ----
        {
            var mapPath = Path.Combine(b1Tmp.FullName, "s-map.json");
            File.WriteAllText(mapPath, """
            {
              "capsules": [ { "name": "CapS", "rootPath": "src/CapS" } ],
              "cores": ["Watcher"],
              "projections": ["Books", "ReportCache", "CleanView"],
              "runtimeParams": [ { "symbolOrPath": "sinp", "role": "input", "declaredType": "JobInput" } ],
              "factSources": [
                { "symbolOrPath": "Books", "grade": 2 },
                { "symbolOrPath": "ControlFlag", "grade": 3 },
                { "symbolOrPath": "SnapshotRec", "grade": 5 },
                { "symbolOrPath": "ViewCache", "grade": 6 },
                { "symbolOrPath": "CleanCache", "grade": 6 }
              ]
            }
            """);
            using var b1sDb = new CozoDb(engine: "mem", path: "");
            var b1sOm = new CozoOm(b1sDb);
            await b1sOm.InitCodeKnowledgeAsync();
            await b1sOm.IndexCodeKnowledgeAsync(new CodeKnowledgeBatch(
                Repositories: [new CodeRepositoryFact("repo:s", "/repo/s")],
                Files:
                [
                    new CodeFileFact("file:s1", "repo:s", "src/CapS/Books.cs", "csharp"),
                    new CodeFileFact("file:s2", "repo:s", "src/CapS/Control.cs", "csharp"),
                    new CodeFileFact("file:s3", "repo:s", "src/CapS/ViewCache.cs", "csharp"),
                    new CodeFileFact("file:s4", "repo:s", "src/CapS/CleanCache.cs", "csharp"),
                    new CodeFileFact("file:s5", "repo:s", "src/CapS/SnapshotRec.cs", "csharp"),
                    new CodeFileFact("file:s6", "repo:s", "src/CapS/JobInput.cs", "csharp"),
                    new CodeFileFact("file:s7", "repo:s", "src/CapS/Hacker.cs", "csharp"),
                    new CodeFileFact("file:s8", "repo:s", "src/CapS/Report.cs", "csharp"),
                    new CodeFileFact("file:s9", "repo:s", "src/CapS/Watcher.cs", "csharp"),
                    new CodeFileFact("file:s10", "repo:s", "src/CapS/Api.cs", "csharp"),
                ],
                Symbols:
                [
                    // V-D3 dual annotation: Books is declared BOTH projection and grade-2 fact.
                    new CodeSymbolFact("sym:books", "file:s1", "Books", "field", 5, 5, SymKey: "csharp:S.Store.Books#0"),
                    new CodeSymbolFact("sym:bookswriter", "file:s1", "BooksWriter", "method", 10, 16, SymKey: "csharp:S.Store.BooksWriter#0"),
                    new CodeSymbolFact("sym:controlflag", "file:s2", "ControlFlag", "field", 5, 5, SymKey: "csharp:S.Control.ControlFlag#0"),
                    // V-S1 extension: a grade-6 derived node writes the grade-3 control fact.
                    new CodeSymbolFact("sym:viewcache", "file:s3", "ViewCache", "class", 3, 20, SymKey: "csharp:S.ViewCache#0"),
                    new CodeSymbolFact("sym:refresh", "file:s3", "Refresh", "method", 8, 14, SymKey: "csharp:S.ViewCache.Refresh#0"),
                    new CodeSymbolFact("sym:cleancache", "file:s4", "CleanCache", "class", 3, 20, SymKey: "csharp:S.CleanCache#0"),
                    new CodeSymbolFact("sym:peek", "file:s4", "Peek", "method", 8, 14, SymKey: "csharp:S.CleanCache.Peek#0"),
                    // V-S3: the grade-5 snapshot type carries an input-role payload field.
                    new CodeSymbolFact("sym:snapshotrec", "file:s5", "SnapshotRec", "class", 3, 12, SymKey: "csharp:S.SnapshotRec#0"),
                    new CodeSymbolFact("sym:snappayload", "file:s5", "Payload", "property", 6, 6, Signature: "public JobInput Payload { get; set; }", SymKey: "csharp:S.SnapshotRec.Payload#0"),
                    new CodeSymbolFact("sym:snapversion", "file:s5", "Version", "property", 8, 8, Signature: "public int Version { get; set; }", SymKey: "csharp:S.SnapshotRec.Version#0"),
                    new CodeSymbolFact("sym:jobinput", "file:s6", "JobInput", "class", 3, 10, SymKey: "csharp:S.JobInput#0"),
                    new CodeSymbolFact("sym:hacker", "file:s7", "Hacker", "class", 3, 20, SymKey: "csharp:S.Hacker#0"),
                    new CodeSymbolFact("sym:poke", "file:s7", "Poke", "method", 8, 14, SymKey: "csharp:S.Hacker.Poke#0"),
                    // V-D3 external write: an outside symbol writes the projection.
                    new CodeSymbolFact("sym:reportcache", "file:s8", "ReportCache", "field", 5, 5, SymKey: "csharp:S.Report.ReportCache#0"),
                    new CodeSymbolFact("sym:cleanview", "file:s8", "CleanView", "class", 7, 20, SymKey: "csharp:S.CleanView#0"),
                    new CodeSymbolFact("sym:render", "file:s8", "Render", "method", 9, 12, SymKey: "csharp:S.CleanView.Render#0"),
                    // V-S4: a core probing file existence/mtime for run-state.
                    new CodeSymbolFact("sym:watcher", "file:s9", "Watcher", "class", 3, 30, SymKey: "csharp:S.Watcher#0"),
                    new CodeSymbolFact("sym:check", "file:s9", "Check", "method", 8, 20, SymKey: "csharp:S.Watcher.Check#0"),
                    new CodeSymbolFact("sym:takein", "file:s10", "TakeIn", "method", 5, 10, SymKey: "csharp:S.Api.TakeIn#1"),
                    new CodeSymbolFact("sym:sinp", "file:s10", "sinp", "parameter", 5, 5, SymKey: "csharp:S.Api.TakeIn.sinp#0"),
                ],
                Edges:
                [
                    new CodeEdgeFact("sym:bookswriter", "sym:books", CodeEdgeKinds.Accesses, "file:s1", 12, 0.9, "roslyn", "write"),
                    new CodeEdgeFact("sym:viewcache", "sym:refresh", CodeEdgeKinds.Contains, "file:s3", 8, 1.0, "roslyn"),
                    new CodeEdgeFact("sym:refresh", "sym:controlflag", CodeEdgeKinds.Accesses, "file:s3", 10, 0.9, "roslyn", "write"),
                    new CodeEdgeFact("sym:cleancache", "sym:peek", CodeEdgeKinds.Contains, "file:s4", 8, 1.0, "roslyn"),
                    new CodeEdgeFact("sym:peek", "sym:controlflag", CodeEdgeKinds.Accesses, "file:s4", 10, 0.9, "roslyn", "read"),
                    new CodeEdgeFact("sym:snapshotrec", "sym:snappayload", CodeEdgeKinds.HasProperty, "file:s5", 6, 1.0, "roslyn"),
                    new CodeEdgeFact("sym:snapshotrec", "sym:snapversion", CodeEdgeKinds.HasProperty, "file:s5", 8, 1.0, "roslyn"),
                    new CodeEdgeFact("sym:hacker", "sym:poke", CodeEdgeKinds.Contains, "file:s7", 8, 1.0, "roslyn"),
                    new CodeEdgeFact("sym:poke", "sym:reportcache", CodeEdgeKinds.Accesses, "file:s7", 10, 0.9, "roslyn", "write"),
                    new CodeEdgeFact("sym:cleanview", "sym:render", CodeEdgeKinds.Contains, "file:s8", 9, 1.0, "roslyn"),
                    new CodeEdgeFact("sym:render", "sym:books", CodeEdgeKinds.Accesses, "file:s8", 10, 0.9, "roslyn", "read"),
                    new CodeEdgeFact("sym:watcher", "sym:check", CodeEdgeKinds.Contains, "file:s9", 8, 1.0, "roslyn"),
                    new CodeEdgeFact("sym:takein", "sym:sinp", CodeEdgeKinds.Contains, "file:s10", 5, 1.0, "roslyn"),
                ],
                ExternalCalls:
                [
                    new CodeExternalCallFact("sym:check", "System.IO.File.Exists", 1, FirstFileId: "file:s9", FirstLine: 10, Resolver: "roslyn"),
                    new CodeExternalCallFact("sym:check", "System.IO.File.GetLastWriteTimeUtc", 1, FirstFileId: "file:s9", FirstLine: 12, Resolver: "roslyn"),
                    // Plain file IO is a V-E1 matter, not a run-state probe — V-S4 must not flag it.
                    new CodeExternalCallFact("sym:check", "System.IO.File.ReadAllText", 1, FirstFileId: "file:s9", FirstLine: 14, Resolver: "roslyn"),
                ]));

            var b1sR = await b1sOm.DepaScanAsync(new DepaScanOptions(MapPath: mapPath));
            string[] sdRules = ["V-S1", "V-S3", "V-S4", "V-D3"];
            Assert(sdRules.All(rule => b1sR.DetectionVerdicts[rule] == "GAP"),
                "all four staged S/D-group red lights should be GAP: "
                + string.Join("; ", sdRules.Select(rule => $"{rule}:{b1sR.DetectionVerdicts[rule]}")));
            Assert(b1sR.Violations.All(v => v.Evidence.Count > 0 && v.Evidence.All(e => e.Path.Length > 0 && e.Line > 0)),
                "every S/D-group violation must carry path:line evidence");

            var s1 = b1sR.Violations.Single(v => v.RuleId == "V-S1");
            Assert(s1.SubjectEntityId == "depa:factsource:csharp:S.ViewCache#0" && s1.Dimension == "fact_source"
                && s1.Message.Contains("ControlFlag", StringComparison.Ordinal)
                && s1.Evidence.Single().Path == "src/CapS/ViewCache.cs" && s1.Evidence.Single().Line == 10,
                $"V-S1 extension should flag the grade-6 node writing the grade-3 control fact (got {s1.SubjectEntityId} '{s1.Message}')");
            Assert(!b1sR.Violations.Any(v => v.RuleId == "V-S1" && v.SubjectEntityId.Contains("CleanCache", StringComparison.Ordinal)),
                "a grade-6 node that only reads upstream must not be flagged by V-S1");

            var s3 = b1sR.Violations.Single(v => v.RuleId == "V-S3");
            Assert(s3.Dimension == "fact_source"
                && s3.Message.Contains("Payload", StringComparison.Ordinal)
                && s3.Evidence.Single().Path == "src/CapS/SnapshotRec.cs" && s3.Evidence.Single().Line == 6,
                $"V-S3 should flag the input-typed snapshot field with its declaration site (got '{s3.Message}')");
            Assert(!b1sR.Violations.Any(v => v.RuleId == "V-S3" && v.Message.Contains("Version", StringComparison.Ordinal)),
                "a plainly-typed snapshot field must not be flagged by V-S3");

            var s4s = b1sR.Violations.Where(v => v.RuleId == "V-S4").ToArray();
            Assert(s4s.Length == 2
                && s4s.All(v => v.SubjectEntityId == "depa:impl:csharp:S.Watcher#0" && v.Dimension == "fact_source"
                    && v.Confidence <= 0.6 && v.Evidence[0].Detail.Contains("heuristic", StringComparison.Ordinal))
                && s4s.Any(v => v.Evidence[0].Line == 10) && s4s.Any(v => v.Evidence[0].Line == 12)
                && !s4s.Any(v => v.Message.Contains("ReadAllText", StringComparison.Ordinal)),
                "V-S4 should flag exactly the Exists/mtime probes (phenomenon-level, conf<=0.6), not plain file IO: "
                + string.Join("; ", s4s.Select(v => $"{v.Confidence}:{v.Message}@{v.Evidence[0].Path}:{v.Evidence[0].Line}")));

            var d3s = b1sR.Violations.Where(v => v.RuleId == "V-D3").ToArray();
            Assert(d3s.All(v => v.Dimension == "data")
                && d3s.Any(v => v.Message.Contains("Books", StringComparison.Ordinal) && v.Message.Contains("grade-2", StringComparison.Ordinal))
                && d3s.Any(v => v.Message.Contains("ReportCache", StringComparison.Ordinal)
                    && v.Evidence[0].Path == "src/CapS/Hacker.cs" && v.Evidence[0].Line == 10)
                && !d3s.Any(v => v.Message.Contains("CleanView", StringComparison.Ordinal)),
                "V-D3 should flag the dual-annotated projection and the externally-written projection, not the clean one: "
                + string.Join("; ", d3s.Select(v => $"'{v.Message}'")));
        }

        // ---- group E/G: V-E3 contract hosts orchestration / V-G1 single-implementation
        // abstraction (effect contracts exempt) ----
        {
            var mapPath = Path.Combine(b1Tmp.FullName, "e-map.json");
            File.WriteAllText(mapPath, """
            {
              "capsules": [ { "name": "CapE", "rootPath": "src/CapE" } ],
              "contractPackages": ["src/CapE/Contracts"]
            }
            """);
            using var b1eDb = new CozoDb(engine: "mem", path: "");
            var b1eOm = new CozoOm(b1eDb);
            await b1eOm.InitCodeKnowledgeAsync();
            await b1eOm.IndexCodeKnowledgeAsync(new CodeKnowledgeBatch(
                Repositories: [new CodeRepositoryFact("repo:e", "/repo/e")],
                Files:
                [
                    new CodeFileFact("file:e1", "repo:e", "src/CapE/Contracts/IMailer.cs", "csharp"),
                    new CodeFileFact("file:e2", "repo:e", "src/CapE/Contracts/IPlain.cs", "csharp"),
                    new CodeFileFact("file:e3", "repo:e", "src/CapE/Impl.cs", "csharp"),
                    new CodeFileFact("file:e4", "repo:e", "src/CapE/Fmt.cs", "csharp"),
                    new CodeFileFact("file:e5", "repo:e", "src/CapE/Strategy.cs", "csharp"),
                    new CodeFileFact("file:e6", "repo:e", "src/CapE/Multi.cs", "csharp"),
                ],
                Symbols:
                [
                    new CodeSymbolFact("sym:imailer", "file:e1", "IMailer", "interface", 3, 10, SymKey: "csharp:E.IMailer#0"),
                    // V-E3: a contract member with an outgoing CALLS edge (orchestration in contract).
                    new CodeSymbolFact("sym:sendall", "file:e1", "SendAll", "method", 6, 8, SymKey: "csharp:E.IMailer.SendAll#0"),
                    new CodeSymbolFact("sym:iplain", "file:e2", "IPlain", "interface", 3, 8, SymKey: "csharp:E.IPlain#0"),
                    new CodeSymbolFact("sym:mailerimpl", "file:e3", "MailerImpl", "class", 3, 20, SymKey: "csharp:E.MailerImpl#0"),
                    new CodeSymbolFact("sym:plainimpl", "file:e3", "PlainImpl", "class", 22, 40, SymKey: "csharp:E.PlainImpl#0"),
                    new CodeSymbolFact("sym:fmthelper", "file:e4", "FmtHelper", "class", 3, 20, SymKey: "csharp:E.FmtHelper#0"),
                    new CodeSymbolFact("sym:fmt", "file:e4", "Fmt", "method", 8, 12, SymKey: "csharp:E.FmtHelper.Fmt#1"),
                    // V-G1: a non-contract abstraction with exactly one implementation.
                    new CodeSymbolFact("sym:istrategy", "file:e5", "IStrategy", "interface", 3, 8, SymKey: "csharp:E.IStrategy#0"),
                    new CodeSymbolFact("sym:onlystrategy", "file:e5", "OnlyStrategy", "class", 10, 20, SymKey: "csharp:E.OnlyStrategy#0"),
                    new CodeSymbolFact("sym:imulti", "file:e6", "IMulti", "interface", 3, 8, SymKey: "csharp:E.IMulti#0"),
                    new CodeSymbolFact("sym:multia", "file:e6", "MultiA", "class", 10, 16, SymKey: "csharp:E.MultiA#0"),
                    new CodeSymbolFact("sym:multib", "file:e6", "MultiB", "class", 20, 26, SymKey: "csharp:E.MultiB#0"),
                ],
                Edges:
                [
                    new CodeEdgeFact("sym:imailer", "sym:sendall", CodeEdgeKinds.Contains, "file:e1", 6, 1.0, "roslyn"),
                    new CodeEdgeFact("sym:sendall", "sym:fmt", CodeEdgeKinds.Calls, "file:e1", 8, 1.0, "roslyn", "semantic"),
                    new CodeEdgeFact("sym:mailerimpl", "sym:imailer", CodeEdgeKinds.Implements, "file:e3", 3, 0.9, "roslyn"),
                    new CodeEdgeFact("sym:plainimpl", "sym:iplain", CodeEdgeKinds.Implements, "file:e3", 22, 0.9, "roslyn"),
                    new CodeEdgeFact("sym:onlystrategy", "sym:istrategy", CodeEdgeKinds.Implements, "file:e5", 10, 0.9, "roslyn"),
                    new CodeEdgeFact("sym:multia", "sym:imulti", CodeEdgeKinds.Implements, "file:e6", 10, 0.9, "roslyn"),
                    new CodeEdgeFact("sym:multib", "sym:imulti", CodeEdgeKinds.Implements, "file:e6", 20, 0.9, "roslyn"),
                ]));

            var b1eR = await b1eOm.DepaScanAsync(new DepaScanOptions(MapPath: mapPath));
            Assert(b1eR.DetectionVerdicts["V-E3"] == "GAP" && b1eR.DetectionVerdicts["V-G1"] == "GAP",
                $"both staged E/G-group red lights should be GAP (got V-E3:{b1eR.DetectionVerdicts["V-E3"]}, V-G1:{b1eR.DetectionVerdicts["V-G1"]})");
            Assert(b1eR.Violations.All(v => v.Evidence.Count > 0 && v.Evidence.All(e => e.Path.Length > 0 && e.Line > 0)),
                "every E/G-group violation must carry path:line evidence");

            var e3 = b1eR.Violations.Single(v => v.RuleId == "V-E3");
            Assert(e3.SubjectEntityId == "depa:contract:csharp:E.IMailer#0" && e3.Dimension == "effect"
                && e3.Message.Contains("SendAll", StringComparison.Ordinal) && e3.Message.Contains("Fmt", StringComparison.Ordinal)
                && e3.Evidence.Single().Path == "src/CapE/Contracts/IMailer.cs" && e3.Evidence.Single().Line == 8,
                $"V-E3 should flag the orchestrating contract member at its call site (got {e3.SubjectEntityId} '{e3.Message}')");
            Assert(!b1eR.Violations.Any(v => v.RuleId == "V-E3" && v.Message.Contains("IPlain", StringComparison.Ordinal)),
                "a declaration-only contract must not be flagged by V-E3");

            var g1 = b1eR.Violations.Single(v => v.RuleId == "V-G1");
            Assert(g1.Dimension == "overdesign" && g1.SubjectEntityId == "depa:capsule:CapE"
                && g1.Confidence <= 0.7
                && g1.Message.Contains("IStrategy", StringComparison.Ordinal)
                && g1.Evidence.Single().Path == "src/CapE/Strategy.cs" && g1.Evidence.Single().Line == 3,
                $"V-G1 should flag the single-implementation abstraction as an overdesign signal (got conf={g1.Confidence} '{g1.Message}')");
            Assert(!b1eR.Violations.Any(v => v.RuleId == "V-G1" && v.Message.Contains("IMulti", StringComparison.Ordinal)),
                "an abstraction with two implementations must not be flagged by V-G1");
            Assert(!b1eR.Violations.Any(v => v.RuleId == "V-G1" && v.Message.Contains("IMailer", StringComparison.Ordinal)),
                "effect contracts are the paradigm's own mandated abstraction — single-impl contracts must not be flagged by V-G1");
        }
    }
    finally
    {
        b1Tmp.Delete(recursive: true);
    }
}

// === DEPA batch-2 detectors — 3 annotation-keyed rules
// (track expand-depa-detection-rules T3.1, delta case batch2-annotated:
// V-S2a/b 与 V-P2 依 recoveryPaths/layers 标注判定；未声明时 BLOCKED 而非猜测) ===
{
    // ck fixture: capsule CapT with a grade-5 checkpoint (Ckpt) + grade-4 journal (Journal),
    // a live-path reader (LiveGate.Gate reads Ckpt, LiveGate.Poll reads Journal), a compliant
    // recovery-path reader (Recovery/Restorer), a projection legitimately deriving from the
    // journal (JViewer), and a kernel→app layer-awareness violation (KHelper.KCall calls
    // AppSvc.ARun) next to the compliant app→kernel direction.
    static CodeKnowledgeBatch Batch2Fixture() => new(
        Repositories: [new CodeRepositoryFact("repo:t", "/repo/t")],
        Files:
        [
            new CodeFileFact("file:t1", "repo:t", "src/CapT/Store.cs", "csharp"),
            new CodeFileFact("file:t2", "repo:t", "src/CapT/App/LiveGate.cs", "csharp"),
            new CodeFileFact("file:t3", "repo:t", "src/CapT/Recovery/Restorer.cs", "csharp"),
            new CodeFileFact("file:t4", "repo:t", "src/CapT/JViewer.cs", "csharp"),
            new CodeFileFact("file:t5", "repo:t", "src/CapT/Kernel/KHelper.cs", "csharp"),
            new CodeFileFact("file:t6", "repo:t", "src/CapT/App/AppSvc.cs", "csharp"),
        ],
        Symbols:
        [
            new CodeSymbolFact("sym:tckpt", "file:t1", "Ckpt", "field", 5, 5, SymKey: "csharp:T.Store.Ckpt#0"),
            new CodeSymbolFact("sym:tjournal", "file:t1", "Journal", "field", 7, 7, SymKey: "csharp:T.Store.Journal#0"),
            new CodeSymbolFact("sym:tlivegate", "file:t2", "LiveGate", "class", 3, 20, SymKey: "csharp:T.LiveGate#0"),
            new CodeSymbolFact("sym:tgate", "file:t2", "Gate", "method", 8, 14, SymKey: "csharp:T.LiveGate.Gate#0"),
            new CodeSymbolFact("sym:tpoll", "file:t2", "Poll", "method", 16, 19, SymKey: "csharp:T.LiveGate.Poll#0"),
            new CodeSymbolFact("sym:trestorer", "file:t3", "Restorer", "class", 3, 20, SymKey: "csharp:T.Restorer#0"),
            new CodeSymbolFact("sym:trestore", "file:t3", "Restore", "method", 7, 12, SymKey: "csharp:T.Restorer.Restore#0"),
            new CodeSymbolFact("sym:tjviewer", "file:t4", "JViewer", "class", 3, 20, SymKey: "csharp:T.JViewer#0"),
            new CodeSymbolFact("sym:tview", "file:t4", "View", "method", 7, 12, SymKey: "csharp:T.JViewer.View#0"),
            new CodeSymbolFact("sym:tkhelper", "file:t5", "KHelper", "class", 3, 20, SymKey: "csharp:T.KHelper#0"),
            new CodeSymbolFact("sym:tkcall", "file:t5", "KCall", "method", 8, 12, SymKey: "csharp:T.KHelper.KCall#0"),
            new CodeSymbolFact("sym:tkmisc", "file:t5", "KMisc", "method", 14, 18, SymKey: "csharp:T.KHelper.KMisc#0"),
            new CodeSymbolFact("sym:tappsvc", "file:t6", "AppSvc", "class", 3, 20, SymKey: "csharp:T.AppSvc#0"),
            new CodeSymbolFact("sym:tarun", "file:t6", "ARun", "method", 8, 12, SymKey: "csharp:T.AppSvc.ARun#0"),
        ],
        Edges:
        [
            new CodeEdgeFact("sym:tlivegate", "sym:tgate", CodeEdgeKinds.Contains, "file:t2", 8, 1.0, "roslyn"),
            new CodeEdgeFact("sym:tlivegate", "sym:tpoll", CodeEdgeKinds.Contains, "file:t2", 16, 1.0, "roslyn"),
            new CodeEdgeFact("sym:trestorer", "sym:trestore", CodeEdgeKinds.Contains, "file:t3", 7, 1.0, "roslyn"),
            new CodeEdgeFact("sym:tjviewer", "sym:tview", CodeEdgeKinds.Contains, "file:t4", 7, 1.0, "roslyn"),
            new CodeEdgeFact("sym:tkhelper", "sym:tkcall", CodeEdgeKinds.Contains, "file:t5", 8, 1.0, "roslyn"),
            new CodeEdgeFact("sym:tappsvc", "sym:tarun", CodeEdgeKinds.Contains, "file:t6", 8, 1.0, "roslyn"),
            // V-S2a: a live-path (non-recovery) symbol reads the grade-5 checkpoint.
            new CodeEdgeFact("sym:tgate", "sym:tckpt", CodeEdgeKinds.Accesses, "file:t2", 10, 0.9, "roslyn", "read"),
            // V-S2b: a live-path symbol reads the grade-4 journal.
            new CodeEdgeFact("sym:tpoll", "sym:tjournal", CodeEdgeKinds.Accesses, "file:t2", 17, 0.9, "roslyn", "read"),
            // Compliant: the recovery-path reader (matches recoveryPaths glob).
            new CodeEdgeFact("sym:trestore", "sym:tckpt", CodeEdgeKinds.Accesses, "file:t3", 9, 0.9, "roslyn", "read"),
            // Compliant: the projection derives from the journal (normal derivation chain).
            new CodeEdgeFact("sym:tview", "sym:tjournal", CodeEdgeKinds.Accesses, "file:t4", 9, 0.9, "roslyn", "read"),
            // V-P2: the kernel (lower) layer calls up into the app (higher) layer...
            new CodeEdgeFact("sym:tkcall", "sym:tarun", CodeEdgeKinds.Calls, "file:t5", 10, 1.0, "roslyn", "semantic"),
            // ...a second upward reach observed only through name-only ambiguous resolution
            // (conf 0.5) — the violation must carry the edge's confidence, not claim 1.0
            // (T3.1 rescan adjudication counterexample)...
            new CodeEdgeFact("sym:tkmisc", "sym:tarun", CodeEdgeKinds.Calls, "file:t5", 16, 0.5, "treesitter", "ambiguous:2"),
            // ...while the app→kernel direction is the compliant one.
            new CodeEdgeFact("sym:tarun", "sym:tkcall", CodeEdgeKinds.Calls, "file:t6", 11, 1.0, "roslyn", "semantic"),
        ]);

    var b2Tmp = Directory.CreateTempSubdirectory("depa-batch2-tests-");
    try
    {
        // ---- annotated map: recoveryPaths + layers (low→high) declared → all three rules run ----
        var mapPath = Path.Combine(b2Tmp.FullName, "t-map.json");
        File.WriteAllText(mapPath, """
        {
          "capsules": [ { "name": "CapT", "rootPath": "src/CapT" } ],
          "projections": ["JViewer"],
          "recoveryPaths": ["src/CapT/Recovery/**"],
          "layers": [
            { "name": "kernel", "pathGlobs": ["src/CapT/Kernel/**"] },
            { "name": "app", "pathGlobs": ["src/CapT/App/**"] }
          ],
          "factSources": [
            { "symbolOrPath": "Ckpt", "grade": 5 },
            { "symbolOrPath": "Journal", "grade": 4 }
          ]
        }
        """);
        using var b2Db = new CozoDb(engine: "mem", path: "");
        var b2Om = new CozoOm(b2Db);
        await b2Om.InitCodeKnowledgeAsync();
        await b2Om.IndexCodeKnowledgeAsync(Batch2Fixture());
        var b2R = await b2Om.DepaScanAsync(new DepaScanOptions(MapPath: mapPath));

        string[] b2Rules = ["V-S2a", "V-S2b", "V-P2"];
        Assert(b2Rules.All(rule => b2R.DetectionVerdicts[rule] == "GAP"),
            "all three staged batch-2 red lights should be GAP: "
            + string.Join("; ", b2Rules.Select(rule => $"{rule}:{b2R.DetectionVerdicts[rule]}")));
        Assert(b2R.Violations.All(v => v.Evidence.Count > 0 && v.Evidence.All(e => e.Path.Length > 0 && e.Line > 0)),
            "every batch-2 violation must carry path:line evidence");

        // V-S2a positive: the live-path read of the grade-5 checkpoint, phenomenon-level
        // conf<=0.7 with heuristic-marked evidence; the recovery-path read is exempt.
        var s2a = b2R.Violations.Single(v => v.RuleId == "V-S2a");
        Assert(s2a.SubjectEntityId == "depa:factsource:csharp:T.Store.Ckpt#0" && s2a.Dimension == "fact_source"
            && s2a.Confidence <= 0.7
            && s2a.Message.Contains("Gate", StringComparison.Ordinal)
            && s2a.Evidence.Single().Path == "src/CapT/App/LiveGate.cs" && s2a.Evidence.Single().Line == 10
            && s2a.Evidence.Single().Detail.Contains("heuristic", StringComparison.Ordinal),
            $"V-S2a should flag the live-path checkpoint read at conf<=0.7 (got conf={s2a.Confidence} {s2a.SubjectEntityId} '{s2a.Message}' @{s2a.Evidence[0].Path}:{s2a.Evidence[0].Line})");
        Assert(!b2R.Violations.Any(v => v.RuleId == "V-S2a" && v.Message.Contains("Restore", StringComparison.Ordinal)),
            "a reader inside a declared recovery path must not be flagged by V-S2a");

        // V-S2b positive: the live-path read of the grade-4 journal; the projection's
        // derivation read is exempt (journal→projection is the normal chain).
        var s2b = b2R.Violations.Single(v => v.RuleId == "V-S2b");
        Assert(s2b.SubjectEntityId == "depa:factsource:csharp:T.Store.Journal#0" && s2b.Dimension == "fact_source"
            && s2b.Confidence <= 0.7
            && s2b.Message.Contains("Poll", StringComparison.Ordinal)
            && s2b.Evidence.Single().Path == "src/CapT/App/LiveGate.cs" && s2b.Evidence.Single().Line == 17
            && s2b.Evidence.Single().Detail.Contains("heuristic", StringComparison.Ordinal),
            $"V-S2b should flag the live-path journal read at conf<=0.7 (got conf={s2b.Confidence} {s2b.SubjectEntityId} '{s2b.Message}' @{s2b.Evidence[0].Path}:{s2b.Evidence[0].Line})");
        Assert(!b2R.Violations.Any(v => v.RuleId == "V-S2b" && v.Message.Contains("View", StringComparison.Ordinal)),
            "a projection deriving from the journal must not be flagged by V-S2b");

        // V-P2 positive: the lower (kernel) layer reaching up into the higher (app) layer;
        // the app→kernel direction stays clean (layers are declared low→high, catalog C4).
        var p2s = b2R.Violations.Where(v => v.RuleId == "V-P2").ToArray();
        var p2 = p2s.Single(v => v.Message.Contains("KCall", StringComparison.Ordinal));
        Assert(p2.Dimension == "processor" && p2.SubjectEntityId == "depa:capsule:CapT"
            && p2.Confidence == 1.0
            && p2.Message.Contains("kernel", StringComparison.Ordinal) && p2.Message.Contains("app", StringComparison.Ordinal)
            && p2.Evidence.Single().Path == "src/CapT/Kernel/KHelper.cs" && p2.Evidence.Single().Line == 10,
            $"V-P2 should flag the kernel→app upward reach at its call site (got {p2.SubjectEntityId} conf={p2.Confidence} '{p2.Message}' @{p2.Evidence[0].Path}:{p2.Evidence[0].Line})");
        // Rescan-adjudication counterexample (T3.1): an upward reach observed only through a
        // name-only ambiguous CALLS resolution keeps the edge's own confidence (0.5) and
        // heuristic-marked evidence — reported honestly, never inflated to config grade.
        var p2Low = p2s.Single(v => v.Message.Contains("KMisc", StringComparison.Ordinal));
        Assert(p2Low.Confidence == 0.5
            && p2Low.Evidence.Single().Line == 16
            && p2Low.Evidence.Single().Detail.Contains("heuristic", StringComparison.Ordinal)
            && p2Low.Evidence.Single().Detail.Contains("ambiguous", StringComparison.Ordinal),
            $"V-P2 must propagate the observation edge's confidence and mark low-fidelity evidence (got conf={p2Low.Confidence} '{p2Low.Evidence[0].Detail}')");
        Assert(!b2R.Violations.Any(v => v.RuleId == "V-P2" && v.Evidence.Any(e => e.Path == "src/CapT/App/AppSvc.cs")),
            "the compliant app→kernel (high→low) call must not be flagged by V-P2");

        // ---- undeclared map: same observations, recoveryPaths/layers withheld → BLOCKED
        // naming the missing annotation, never a guess (delta case batch2-annotated) ----
        var blockedMapPath = Path.Combine(b2Tmp.FullName, "t-blocked-map.json");
        File.WriteAllText(blockedMapPath, """
        {
          "capsules": [ { "name": "CapT", "rootPath": "src/CapT" } ],
          "projections": ["JViewer"],
          "factSources": [
            { "symbolOrPath": "Ckpt", "grade": 5 },
            { "symbolOrPath": "Journal", "grade": 4 }
          ]
        }
        """);
        using var b2bDb = new CozoDb(engine: "mem", path: "");
        var b2bOm = new CozoOm(b2bDb);
        await b2bOm.InitCodeKnowledgeAsync();
        await b2bOm.IndexCodeKnowledgeAsync(Batch2Fixture());
        var b2bR = await b2bOm.DepaScanAsync(new DepaScanOptions(MapPath: blockedMapPath));
        Assert(b2Rules.All(rule => b2bR.DetectionVerdicts[rule] == "BLOCKED"),
            "without recoveryPaths/layers all three batch-2 rules must report BLOCKED, not guess: "
            + string.Join("; ", b2Rules.Select(rule => $"{rule}:{b2bR.DetectionVerdicts[rule]}")));
        Assert(b2bR.RuleFindings.Single(f => f.RuleId == "V-S2a").Message.Contains("recoveryPaths", StringComparison.Ordinal)
            && b2bR.RuleFindings.Single(f => f.RuleId == "V-S2b").Message.Contains("recoveryPaths", StringComparison.Ordinal)
            && b2bR.RuleFindings.Single(f => f.RuleId == "V-P2").Message.Contains("layers", StringComparison.Ordinal),
            "each blocked batch-2 rule must name its missing depa-map key: "
            + string.Join("; ", b2bR.RuleFindings.Where(f => b2Rules.Contains(f.RuleId)).Select(f => $"{f.RuleId}:{f.Message}")));
        Assert(!b2bR.Violations.Any(v => b2Rules.Contains(v.RuleId)),
            "blocked batch-2 rules must not materialize speculative violations");

        // ---- partial map: recoveryPaths declared but no grade-5 node → V-S2a BLOCKED naming
        // the missing grade node while V-S2b still runs on the grade-4 journal ----
        var partialMapPath = Path.Combine(b2Tmp.FullName, "t-partial-map.json");
        File.WriteAllText(partialMapPath, """
        {
          "capsules": [ { "name": "CapT", "rootPath": "src/CapT" } ],
          "projections": ["JViewer"],
          "recoveryPaths": ["src/CapT/Recovery/**"],
          "factSources": [ { "symbolOrPath": "Journal", "grade": 4 } ]
        }
        """);
        using var b2pDb = new CozoDb(engine: "mem", path: "");
        var b2pOm = new CozoOm(b2pDb);
        await b2pOm.InitCodeKnowledgeAsync();
        await b2pOm.IndexCodeKnowledgeAsync(Batch2Fixture());
        var b2pR = await b2pOm.DepaScanAsync(new DepaScanOptions(MapPath: partialMapPath));
        Assert(b2pR.DetectionVerdicts["V-S2a"] == "BLOCKED"
            && b2pR.RuleFindings.Single(f => f.RuleId == "V-S2a").Message.Contains("grade-5", StringComparison.Ordinal),
            "recoveryPaths without any grade-5 node must leave V-S2a BLOCKED naming the missing grade node: "
            + b2pR.RuleFindings.SingleOrDefault(f => f.RuleId == "V-S2a")?.Message);
        Assert(b2pR.DetectionVerdicts["V-S2b"] == "GAP"
            && b2pR.Violations.Single(v => v.RuleId == "V-S2b").Message.Contains("Poll", StringComparison.Ordinal),
            "V-S2b must still run and flag the journal live read when only the grade-4 node is declared");
    }
    finally
    {
        b2Tmp.Delete(recursive: true);
    }
}

Console.WriteLine("Cozo.DotNet OM tests passed.");

/// <summary>Fixed clock for the timeprovider-indexed-at case (track fix-om-depa-conformance-gaps).</summary>
file sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

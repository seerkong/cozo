using System.Text.Json;
using Cozo.DotNet;

namespace Cozo.DotNet.VizServer;

public sealed record PermissionParam(string Key, string Label, string Placeholder, bool Required);

public sealed record PermissionQuery(string Id, string Label, string Meaning, string Cozo, IReadOnlyList<PermissionParam> Params);

public sealed record PermissionModel(
    string Id,
    string Label,
    string Description,
    IReadOnlyList<PermissionTable> Tables,
    IReadOnlyList<PermissionQuery> Queries);

public sealed record PermissionTable(string Name, string Schema, IReadOnlyList<string> Columns, IReadOnlyList<Dictionary<string, JsonElement>> Rows);

public sealed record PermissionRunRequest(string ModelId, string QueryId, Dictionary<string, string>? Params = null, IReadOnlyList<PermissionTable>? Tables = null);

public static class PermissionModels
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IReadOnlyList<PermissionModel> All =>
    [
        new PermissionModel(
            "rbac-basic",
            "RBAC Basic",
            "Role based permission model implemented as server-owned raw CozoScript.",
            DefaultTables,
            [
                new PermissionQuery(
                    "userPermissions",
                    "User permissions",
                    "List permissions granted to a user through roles.",
                    UserPermissionsQuery,
                    [new PermissionParam("userId", "User ID", "u001", true)])
            ])
    ];

    private const string UserPermissionsQuery = """
        ?[user_id, username, role_name, resource, action] :=
          *users{ user_id: $user_id, username, email: _email, created_at: _created_at },
          *user_roles{ user_id, role_id },
          *roles{ role_id, role_name, description: _role_desc },
          *role_permissions{ role_id, permission_id },
          *permissions{ permission_id, resource, action, description: _perm_desc }
        :sort resource, action
        """;

    private static readonly IReadOnlyList<PermissionTable> DefaultTables =
    [
        Table("users", "{ user_id: String => username: String, email: String, created_at: Int }", ["user_id", "username", "email", "created_at"],
        [
            Row(("user_id", "u001"), ("username", "admin"), ("email", "admin@example.com"), ("created_at", 20240101)),
            Row(("user_id", "u002"), ("username", "alice"), ("email", "alice@example.com"), ("created_at", 20240102))
        ]),
        Table("roles", "{ role_id: String => role_name: String, description: String }", ["role_id", "role_name", "description"],
        [
            Row(("role_id", "r001"), ("role_name", "SuperAdmin"), ("description", "All permissions")),
            Row(("role_id", "r002"), ("role_name", "Viewer"), ("description", "Read only"))
        ]),
        Table("permissions", "{ permission_id: String => resource: String, action: String, description: String }", ["permission_id", "resource", "action", "description"],
        [
            Row(("permission_id", "p001"), ("resource", "user"), ("action", "read"), ("description", "Read users")),
            Row(("permission_id", "p002"), ("resource", "user"), ("action", "delete"), ("description", "Delete users")),
            Row(("permission_id", "p003"), ("resource", "article"), ("action", "read"), ("description", "Read articles"))
        ]),
        Table("user_roles", "{ user_id: String, role_id: String => }", ["user_id", "role_id"],
        [
            Row(("user_id", "u001"), ("role_id", "r001")),
            Row(("user_id", "u002"), ("role_id", "r002"))
        ]),
        Table("role_permissions", "{ role_id: String, permission_id: String => }", ["role_id", "permission_id"],
        [
            Row(("role_id", "r001"), ("permission_id", "p001")),
            Row(("role_id", "r001"), ("permission_id", "p002")),
            Row(("role_id", "r001"), ("permission_id", "p003")),
            Row(("role_id", "r002"), ("permission_id", "p003"))
        ])
    ];

    public static PermissionModel? Find(string id) => All.FirstOrDefault(model => string.Equals(model.Id, id, StringComparison.Ordinal));

    public static async Task<IResult> RunAsync(PermissionRunRequest request)
    {
        var model = Find(request.ModelId);
        if (model is null)
        {
            return Results.Json(new { status = "error", error = $"Unknown model: {request.ModelId}" });
        }

        var query = model.Queries.FirstOrDefault(q => string.Equals(q.Id, request.QueryId, StringComparison.Ordinal));
        if (query is null)
        {
            return Results.Json(new { status = "error", error = $"Unknown query: {request.QueryId}" });
        }

        using var db = new CozoDb(engine: "mem", path: "");
        foreach (var table in request.Tables is { Count: > 0 } ? request.Tables : model.Tables)
        {
            SeedTable(db, table);
        }

        using var result = db.Run(query.Cozo, new { user_id = Param(request.Params, "userId", "u001") });
        var tableResult = ToTable(result.RootElement);
        return Results.Json(new
        {
            status = "ok",
            sections = new[]
            {
                new
                {
                    id = query.Id,
                    label = query.Label,
                    query = query.Cozo,
                    table = tableResult
                }
            }
        });
    }

    private static void SeedTable(CozoDb db, PermissionTable table)
    {
        var data = table.Rows.Select(row => table.Columns.Select(column => JsonToObject(row[column])).ToArray()).ToArray();
        db.Run($"?[{string.Join(", ", table.Columns)}] <- $data\n:replace {table.Name} {table.Schema}", new { data });
    }

    private static TableResult ToTable(JsonElement root)
    {
        var columns = root.GetProperty("headers").EnumerateArray().Select(item => item.GetString() ?? "").ToArray();
        var rows = root.GetProperty("rows").EnumerateArray()
            .Select(row => row.EnumerateArray().Select((cell, index) => (columns[index], value: JsonToObject(cell)))
                .ToDictionary(item => item.Item1, item => item.value, StringComparer.Ordinal))
            .ToArray();
        return new TableResult(columns, rows);
    }

    private static string Param(Dictionary<string, string>? values, string key, string fallback) =>
        values is not null && values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : fallback;

    private static PermissionTable Table(string name, string schema, IReadOnlyList<string> columns, IReadOnlyList<Dictionary<string, JsonElement>> rows) =>
        new(name, schema, columns, rows);

    private static Dictionary<string, JsonElement> Row(params (string Key, object? Value)[] values)
    {
        return values.ToDictionary(
            item => item.Key,
            item => JsonSerializer.SerializeToElement(item.Value, JsonOptions),
            StringComparer.Ordinal);
    }

    private static object? JsonToObject(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetInt64(out var longValue) => longValue,
            JsonValueKind.Number when element.TryGetDouble(out var doubleValue) => doubleValue,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => JsonSerializer.Deserialize<object?>(element.GetRawText(), JsonOptions)
        };
    }
}

using System.Text.Json;
using Cozo.DotNet.Om.Contracts;

namespace Cozo.DotNet.Om.Internals;

internal static class JsonRows
{
    internal static string? StringAt(IReadOnlyList<JsonElement> row, int index)
    {
        if (index >= row.Count || row[index].ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        return row[index].GetString();
    }

    internal static int IntAt(IReadOnlyList<JsonElement> row, int index)
    {
        return row[index].GetInt32();
    }

    internal static bool BoolAt(IReadOnlyList<JsonElement> row, int index)
    {
        return row[index].ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => row[index].GetInt32() != 0,
            JsonValueKind.String => bool.TryParse(row[index].GetString(), out var v) && v,
            _ => false,
        };
    }

    internal static JsonElement ElementAt(IReadOnlyList<JsonElement> row, int index) => row[index].Clone();

    internal static IReadOnlyList<IReadOnlyList<JsonElement>> Rows(OmQueryResult result) => result.Rows;
}

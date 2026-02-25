namespace Cozo.DotNet.Om.Support;

public static class CozoScriptBuilder
{
    public static string InputPut(string relation, IReadOnlyList<string> keyColumns, IReadOnlyList<string> valueColumns)
    {
        var allColumns = keyColumns.Concat(valueColumns).ToArray();
        var head = string.Join(", ", allColumns);
        var values = string.Join(", ", allColumns.Select(c => $"${c}"));
        var schema = valueColumns.Count == 0
            ? $"{{{string.Join(", ", keyColumns)}}}"
            : $"{{{string.Join(", ", keyColumns)} => {string.Join(", ", valueColumns)}}}";
        return $@"
?[{head}] <- [[{values}]]
:put {relation} {schema}
".Trim();
    }

    public static string InputInsert(string relation, IReadOnlyList<string> keyColumns, IReadOnlyList<string> valueColumns)
    {
        var allColumns = keyColumns.Concat(valueColumns).ToArray();
        var head = string.Join(", ", allColumns);
        var values = string.Join(", ", allColumns.Select(c => $"${c}"));
        var schema = valueColumns.Count == 0
            ? $"{{{string.Join(", ", keyColumns)}}}"
            : $"{{{string.Join(", ", keyColumns)} => {string.Join(", ", valueColumns)}}}";
        return $@"
?[{head}] <- [[{values}]]
:insert {relation} {schema}
".Trim();
    }
}

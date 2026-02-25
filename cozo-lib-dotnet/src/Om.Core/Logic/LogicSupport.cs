using System.Text.Json;
using Cozo.DotNet.Om.Contracts;
using Cozo.DotNet.Om.Internals;
using Cozo.DotNet.Om.Runtime;
using Cozo.DotNet;

namespace Cozo.DotNet.Om.Logic;

internal static class LogicSupport
{
    internal static async Task CreateIgnoreConflictAsync(CozoOmRuntime runtime, string script, CancellationToken cancellationToken)
    {
        try
        {
            await runtime.Store.RunAsync(script, cancellationToken: cancellationToken);
        }
        catch (CozoException ex) when (IsCreateConflict(ex))
        {
        }
    }

    private static bool IsCreateConflict(CozoException ex)
    {
        var text = $"{ex.Message}\n{ex.RawResponse}".ToLowerInvariant();
        return text.Contains("conflict", StringComparison.Ordinal) ||
               text.Contains("already", StringComparison.Ordinal) ||
               text.Contains("exists", StringComparison.Ordinal);
    }

    internal static async Task<bool> ExistsAsync(
        CozoOmRuntime runtime,
        string relation,
        string keyField,
        string keyValue,
        CancellationToken cancellationToken)
    {
        var script = $@"
?[{keyField}] :=
  *{relation}{{ {keyField} }},
  {keyField} = ${keyField}
:limit 1
".Trim();
        var rows = await runtime.Store.RunAsync(script, new Dictionary<string, object?> { [keyField] = keyValue }, cancellationToken: cancellationToken);
        return rows.Rows.Count > 0;
    }

    internal static string DirectionToString(Contracts.Models.OmDirection direction) => direction switch
    {
        Contracts.Models.OmDirection.Outgoing => "outgoing",
        Contracts.Models.OmDirection.Incoming => "incoming",
        _ => "both",
    };

    internal static Dictionary<string, object?> Params(params (string Key, object? Value)[] entries)
    {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in entries)
        {
            dict[key] = OmConvert.NormalizeParamValue(value);
        }

        return dict;
    }

    internal static JsonElement EmptyObject()
    {
        return JsonSerializer.SerializeToElement(new Dictionary<string, object?>()).Clone();
    }
}

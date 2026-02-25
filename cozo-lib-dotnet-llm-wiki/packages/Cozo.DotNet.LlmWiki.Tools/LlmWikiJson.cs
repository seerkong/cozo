using System.Text.Json;

namespace Cozo.DotNet.LlmWiki.Tools;

public static class LlmWikiJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };
}

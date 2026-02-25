namespace Cozo.DotNet.LlmWiki.LlmClient;

using System.Text;
using System.Text.Json.Nodes;

/// <summary>
/// Anthropic provider: POST {base}/v1/messages with x-api-key + anthropic-version headers;
/// the system prompt is a top-level field, not a message (design §1.3).
/// </summary>
internal sealed class AnthropicClient(LlmClientConfig config, HttpMessageHandler? handler = null)
    : HttpLlmClientBase(config, handler)
{
    internal const string ApiVersion = "2023-06-01";

    /// <summary>max_tokens is required by the Messages API; default aligned with GitNexus.</summary>
    internal const int DefaultMaxTokens = 16384;

    protected override HttpRequestMessage BuildRequest(string systemPrompt, string userPrompt, LlmOptions options)
    {
        var body = new JsonObject
        {
            ["model"] = options.Model ?? Config.Model,
            ["max_tokens"] = options.MaxTokens ?? DefaultMaxTokens,
            ["system"] = systemPrompt,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "user", ["content"] = userPrompt })
        };
        if (options.Temperature is { } temperature)
        {
            body["temperature"] = temperature;
        }

        var request = new HttpRequestMessage(
            HttpMethod.Post,
            JoinUrl(Config.BaseUrl ?? LlmClientConfig.AnthropicDefaultBaseUrl, "v1/messages"))
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        request.Headers.Add("x-api-key", Config.ApiKey);
        request.Headers.Add("anthropic-version", ApiVersion);
        return request;
    }

    protected override LlmCompletion ParseResponse(JsonObject json)
    {
        var text = string.Concat((json["content"] as JsonArray ?? [])
            .Where(block => block?["type"]?.GetValue<string>() is null or "text")
            .Select(block => block?["text"]?.GetValue<string>() ?? ""));
        var usage = json["usage"];
        return new LlmCompletion(
            Text: text,
            Model: json["model"]?.GetValue<string>(),
            Usage: usage is null ? null : new LlmUsage(
                PromptTokens: usage["input_tokens"]?.GetValue<int>(),
                CompletionTokens: usage["output_tokens"]?.GetValue<int>()));
    }
}

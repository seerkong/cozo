namespace Cozo.DotNet.LlmWiki.LlmClient;

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

/// <summary>
/// OpenAI-compatible provider: OpenAI / OpenRouter / DeepSeek / llama.cpp / Ollama / LiteLLM —
/// anything speaking POST {base}/chat/completions with Bearer auth (design §1.3).
/// </summary>
internal sealed class OpenAiCompatibleClient(LlmClientConfig config, HttpMessageHandler? handler = null)
    : HttpLlmClientBase(config, handler)
{
    protected override HttpRequestMessage BuildRequest(string systemPrompt, string userPrompt, LlmOptions options)
    {
        var body = new JsonObject
        {
            ["model"] = options.Model ?? Config.Model,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = systemPrompt },
                new JsonObject { ["role"] = "user", ["content"] = userPrompt })
        };
        if (options.MaxTokens is { } maxTokens)
        {
            body["max_completion_tokens"] = maxTokens;
        }
        if (options.Temperature is { } temperature)
        {
            body["temperature"] = temperature;
        }

        var request = new HttpRequestMessage(
            HttpMethod.Post,
            JoinUrl(Config.BaseUrl ?? LlmClientConfig.OpenAiDefaultBaseUrl, "chat/completions"))
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Config.ApiKey);
        return request;
    }

    protected override LlmCompletion ParseResponse(JsonObject json)
    {
        var message = json["choices"]?[0]?["message"];
        var usage = json["usage"];
        return new LlmCompletion(
            Text: message?["content"]?.GetValue<string>() ?? "",
            Model: json["model"]?.GetValue<string>(),
            Usage: usage is null ? null : new LlmUsage(
                PromptTokens: usage["prompt_tokens"]?.GetValue<int>(),
                CompletionTokens: usage["completion_tokens"]?.GetValue<int>()));
    }
}

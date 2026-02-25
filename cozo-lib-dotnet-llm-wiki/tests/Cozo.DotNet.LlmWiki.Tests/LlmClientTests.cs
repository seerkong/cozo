namespace Cozo.DotNet.LlmWiki.Tests;

using System.Net;
using System.Text.Json.Nodes;
using Cozo.DotNet.LlmWiki.LlmClient;

/// <summary>
/// LlmClient capsule tests (add-llm-wiki-llm-pipeline track T1.1, delta cases:
/// llm-client {openai-compatible, unavailable-graceful}).
/// Mock HttpMessageHandler based — no network. Covers: OpenAI-compatible request
/// structure (url/auth/model/messages role order) and response parsing; Anthropic
/// request structure (x-api-key/anthropic-version headers, top-level system field,
/// /v1/messages url) and response parsing; no-config graceful unavailability
/// (factory never throws); non-2xx -> LlmException with status and truncated body.
/// </summary>
internal static class LlmClientTests
{
    /// <summary>Records the last request and answers with a canned response.</summary>
    private sealed class MockHandler(HttpStatusCode status, string responseBody) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastRequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(responseBody, System.Text.Encoding.UTF8, "application/json")
            };
        }
    }

    public static async Task RunAsync(Action<bool, string> assert)
    {
        // --- unavailable-graceful (delta case): no configuration at all -> NullLlmClient.
        var unavailable = LlmClientFactory.FromEnvironment(_ => null);
        assert(!unavailable.IsAvailable, "no provider configuration should yield IsAvailable=false");
        assert(!string.IsNullOrWhiteSpace(unavailable.UnavailableReason),
            "unavailable client should carry a non-empty UnavailableReason");
        // Factory never throws; CompleteAsync on the null client throws LlmException only when misused.
        var misuse = await Record(() => unavailable.CompleteAsync("s", "u"));
        assert(misuse is LlmException, "CompleteAsync on an unavailable client should throw LlmException as a misuse guard");

        // Partial config (key but no model) also degrades gracefully instead of throwing.
        var noModel = LlmClientFactory.FromEnvironment(name => name == "DEPA_WIKI_LLM_API_KEY" ? "sk-x" : null);
        assert(!noModel.IsAvailable && noModel.UnavailableReason!.Contains("DEPA_WIKI_LLM_MODEL", StringComparison.Ordinal),
            "missing model should degrade with a reason naming DEPA_WIKI_LLM_MODEL");

        // --- openai-compatible (delta case): request structure + response parsing.
        var openAiHandler = new MockHandler(HttpStatusCode.OK, """
        {
          "id": "chatcmpl-1",
          "model": "gpt-test",
          "choices": [{ "index": 0, "message": { "role": "assistant", "content": "hello world" }, "finish_reason": "stop" }],
          "usage": { "prompt_tokens": 12, "completion_tokens": 5 }
        }
        """);
        var openAi = LlmClientFactory.Create(new LlmClientConfig(
            Provider: "openai",
            BaseUrl: "https://llm.example.test/v1",
            ApiKey: "sk-test-key",
            Model: "gpt-test"), openAiHandler);
        assert(openAi.IsAvailable && openAi.UnavailableReason is null, "configured openai client should be available");

        var openAiResult = await openAi.CompleteAsync("You are a wiki writer.", "Describe the module.",
            new LlmOptions(MaxTokens: 256, Temperature: 0));
        var openAiRequest = openAiHandler.LastRequest!;
        assert(openAiRequest.Method == HttpMethod.Post
                && openAiRequest.RequestUri!.ToString() == "https://llm.example.test/v1/chat/completions",
            "openai client should POST {base}/chat/completions (got: " + openAiRequest.RequestUri + ")");
        assert(openAiRequest.Headers.Authorization?.Scheme == "Bearer"
                && openAiRequest.Headers.Authorization?.Parameter == "sk-test-key",
            "openai client should send Authorization: Bearer <key>");
        var openAiBody = JsonNode.Parse(openAiHandler.LastRequestBody!)!.AsObject();
        assert(openAiBody["model"]!.GetValue<string>() == "gpt-test", "openai request body should carry the configured model");
        var messages = openAiBody["messages"]!.AsArray();
        assert(messages.Count == 2
                && messages[0]!["role"]!.GetValue<string>() == "system"
                && messages[0]!["content"]!.GetValue<string>() == "You are a wiki writer."
                && messages[1]!["role"]!.GetValue<string>() == "user"
                && messages[1]!["content"]!.GetValue<string>() == "Describe the module.",
            "openai messages should be [system, user] in order with the given prompts");
        assert(openAiBody["max_completion_tokens"]!.GetValue<int>() == 256,
            "openai request should carry max_completion_tokens from LlmOptions");
        assert(Math.Abs(openAiBody["temperature"]!.GetValue<double>()) < 1e-9,
            "openai request should carry temperature from LlmOptions");
        assert(openAiResult.Text == "hello world", "openai response content should be parsed into LlmCompletion.Text");
        assert(openAiResult.Model == "gpt-test", "openai response model should be parsed");
        assert(openAiResult.Usage is { PromptTokens: 12, CompletionTokens: 5 },
            "openai usage tokens should be parsed into LlmUsage");

        // Per-request model override wins over the configured model.
        await openAi.CompleteAsync("s", "u", new LlmOptions(Model: "gpt-other"));
        assert(JsonNode.Parse(openAiHandler.LastRequestBody!)!["model"]!.GetValue<string>() == "gpt-other",
            "LlmOptions.Model should override the configured model per request");

        // --- Anthropic request structure (T1.1-AC1): headers + top-level system field.
        var anthropicHandler = new MockHandler(HttpStatusCode.OK, """
        {
          "id": "msg-1",
          "model": "claude-test",
          "content": [{ "type": "text", "text": "narrative section" }],
          "usage": { "input_tokens": 20, "output_tokens": 7 }
        }
        """);
        var anthropic = LlmClientFactory.Create(new LlmClientConfig(
            Provider: "anthropic",
            BaseUrl: null,
            ApiKey: "ant-key",
            Model: "claude-test"), anthropicHandler);
        assert(anthropic.IsAvailable, "configured anthropic client should be available");

        var anthropicResult = await anthropic.CompleteAsync("System discipline.", "Write the overview.",
            new LlmOptions(MaxTokens: 512));
        var anthropicRequest = anthropicHandler.LastRequest!;
        assert(anthropicRequest.Method == HttpMethod.Post
                && anthropicRequest.RequestUri!.ToString() == "https://api.anthropic.com/v1/messages",
            "anthropic client should POST {base}/v1/messages with the default base url (got: " + anthropicRequest.RequestUri + ")");
        assert(anthropicRequest.Headers.TryGetValues("x-api-key", out var apiKeys) && apiKeys.Single() == "ant-key",
            "anthropic client should send the x-api-key header");
        assert(anthropicRequest.Headers.TryGetValues("anthropic-version", out var versions) && versions.Single().Length > 0,
            "anthropic client should send the anthropic-version header");
        assert(anthropicRequest.Headers.Authorization is null,
            "anthropic client should not send an Authorization header");
        var anthropicBody = JsonNode.Parse(anthropicHandler.LastRequestBody!)!.AsObject();
        assert(anthropicBody["system"]!.GetValue<string>() == "System discipline.",
            "anthropic system prompt should be the top-level system field, not a message");
        var anthropicMessages = anthropicBody["messages"]!.AsArray();
        assert(anthropicMessages.Count == 1
                && anthropicMessages[0]!["role"]!.GetValue<string>() == "user"
                && anthropicMessages[0]!["content"]!.GetValue<string>() == "Write the overview.",
            "anthropic messages should carry only the user prompt");
        assert(anthropicBody["model"]!.GetValue<string>() == "claude-test"
                && anthropicBody["max_tokens"]!.GetValue<int>() == 512,
            "anthropic request should carry model and required max_tokens");
        assert(anthropicResult.Text == "narrative section"
                && anthropicResult.Model == "claude-test"
                && anthropicResult.Usage is { PromptTokens: 20, CompletionTokens: 7 },
            "anthropic response content/model/usage should be parsed");

        // --- FromEnvironment: env-var driven config with a custom base url, no trailing-slash dup.
        var envHandler = new MockHandler(HttpStatusCode.OK, """
        { "model": "local-model", "choices": [{ "message": { "content": "ok" } }] }
        """);
        var envVars = new Dictionary<string, string>
        {
            ["DEPA_WIKI_LLM_PROVIDER"] = "openai",
            ["DEPA_WIKI_LLM_BASE_URL"] = "http://localhost:8080/v1/",
            ["DEPA_WIKI_LLM_API_KEY"] = "local-key",
            ["DEPA_WIKI_LLM_MODEL"] = "local-model"
        };
        var envClient = LlmClientFactory.FromEnvironment(name => envVars.GetValueOrDefault(name), envHandler);
        assert(envClient.IsAvailable, "env-configured client should be available");
        var envResult = await envClient.CompleteAsync("s", "u");
        assert(envHandler.LastRequest!.RequestUri!.ToString() == "http://localhost:8080/v1/chat/completions",
            "env base url should be joined without a duplicate slash (got: " + envHandler.LastRequest.RequestUri + ")");
        assert(envResult.Text == "ok", "env client should parse the response text");

        // Anthropic provider falls back to ANTHROPIC_API_KEY when DEPA_WIKI_LLM_API_KEY is unset.
        var fallbackVars = new Dictionary<string, string>
        {
            ["DEPA_WIKI_LLM_PROVIDER"] = "anthropic",
            ["DEPA_WIKI_LLM_MODEL"] = "claude-test",
            ["ANTHROPIC_API_KEY"] = "fallback-key"
        };
        var fallbackClient = LlmClientFactory.FromEnvironment(name => fallbackVars.GetValueOrDefault(name), anthropicHandler);
        assert(fallbackClient.IsAvailable, "anthropic provider should fall back to ANTHROPIC_API_KEY");
        await fallbackClient.CompleteAsync("s", "u");
        assert(anthropicHandler.LastRequest!.Headers.GetValues("x-api-key").Single() == "fallback-key",
            "fallback key should be sent as x-api-key");

        // Unknown provider degrades gracefully instead of throwing.
        var badProvider = LlmClientFactory.FromEnvironment(name => name switch
        {
            "DEPA_WIKI_LLM_PROVIDER" => "mystery",
            "DEPA_WIKI_LLM_API_KEY" => "k",
            "DEPA_WIKI_LLM_MODEL" => "m",
            _ => null
        });
        assert(!badProvider.IsAvailable && badProvider.UnavailableReason!.Contains("mystery", StringComparison.Ordinal),
            "unknown provider should degrade with a reason naming the provider value");

        // --- non-2xx -> LlmException with status and truncated body.
        var errorBody = "{\"error\":{\"message\":\"" + new string('x', 5000) + "\"}}";
        var errorHandler = new MockHandler(HttpStatusCode.TooManyRequests, errorBody);
        var errorClient = LlmClientFactory.Create(new LlmClientConfig("openai", "https://llm.example.test/v1", "k", "m"), errorHandler);
        var thrown = await Record(() => errorClient.CompleteAsync("s", "u"));
        assert(thrown is LlmException, "non-2xx response should throw LlmException (got: " + (thrown?.GetType().Name ?? "none") + ")");
        var llmException = (LlmException)thrown!;
        assert(llmException.StatusCode == 429, "LlmException should carry the HTTP status code");
        assert(llmException.ResponseBody is not null && llmException.ResponseBody.Length <= 2048
                && llmException.ResponseBody.StartsWith("{\"error\"", StringComparison.Ordinal),
            "LlmException should carry the response body truncated to a bounded length");

        // Empty completion content is an error, not a silent empty string.
        var emptyHandler = new MockHandler(HttpStatusCode.OK, """{ "choices": [{ "message": { "content": "" } }] }""");
        var emptyClient = LlmClientFactory.Create(new LlmClientConfig("openai", "https://llm.example.test/v1", "k", "m"), emptyHandler);
        assert(await Record(() => emptyClient.CompleteAsync("s", "u")) is LlmException,
            "an empty completion should throw LlmException");
    }

    private static async Task<Exception?> Record(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }
}

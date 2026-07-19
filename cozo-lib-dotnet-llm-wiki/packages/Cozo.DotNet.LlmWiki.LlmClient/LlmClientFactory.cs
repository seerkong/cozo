namespace Cozo.DotNet.LlmWiki.LlmClient;

/// <summary>
/// Builds <see cref="ILlmClient"/> instances from configuration. Never throws (design §1.5):
/// missing provider prerequisites or an unknown provider yields an unavailable client with a reason,
/// because degradation to the pure structure layer is the pipeline's normal path.
/// </summary>
public static class LlmClientFactory
{
    /// <summary>Builds a client from DEPA_WIKI_LLM_* environment variables (see <see cref="LlmClientConfig"/>).</summary>
    public static ILlmClient FromEnvironment(HttpMessageHandler? httpMessageHandler = null) =>
        FromEnvironment(Environment.GetEnvironmentVariable, httpMessageHandler);

    /// <summary>Testable overload: reads variables through <paramref name="getEnvironmentVariable"/>.</summary>
    public static ILlmClient FromEnvironment(Func<string, string?> getEnvironmentVariable, HttpMessageHandler? httpMessageHandler = null) =>
        Create(LlmClientConfig.FromEnvironment(getEnvironmentVariable), httpMessageHandler);

    /// <summary>Builds a client from explicit configuration (CLI/tool parameters override env).</summary>
    public static ILlmClient Create(LlmClientConfig config, HttpMessageHandler? httpMessageHandler = null)
    {
        if (LlmClientConfig.IsCodexCli(config.Provider))
        {
            return CodexCliLlmClient.TryCreate(config, out var client, out var unavailableReason)
                ? client
                : new NullLlmClient(unavailableReason);
        }

        if (!LlmClientConfig.IsOpenAiCompatible(config.Provider) && !LlmClientConfig.IsAnthropic(config.Provider))
        {
            return new NullLlmClient(
                $"unknown DEPA_WIKI_LLM_PROVIDER '{config.Provider}' (expected openai | anthropic | codex-cli)");
        }

        if (string.IsNullOrWhiteSpace(config.ApiKey))
        {
            return new NullLlmClient("no LLM API key configured (DEPA_WIKI_LLM_API_KEY unset)");
        }

        if (string.IsNullOrWhiteSpace(config.Model))
        {
            return new NullLlmClient("no LLM model configured (DEPA_WIKI_LLM_MODEL unset; no default model)");
        }

        return LlmClientConfig.IsAnthropic(config.Provider)
            ? new AnthropicClient(config, httpMessageHandler)
            : new OpenAiCompatibleClient(config, httpMessageHandler);
    }
}

/// <summary>Degraded client: answers availability questions, guards against misuse.</summary>
internal sealed class NullLlmClient(string reason) : ILlmClient
{
    public bool IsAvailable => false;

    public string? UnavailableReason => reason;

    public Task<LlmCompletion> CompleteAsync(string systemPrompt, string userPrompt, LlmOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new LlmException($"LLM client is unavailable: {reason}");
}

namespace Cozo.DotNet.LlmWiki.LlmClient;

/// <summary>
/// Provider-level configuration (design §1.4). Sources, by priority: explicit construction
/// (CLI/tool parameters) &gt; environment variables &gt; unavailable (degraded).
/// </summary>
/// <param name="Provider">"openai" (alias "openai-compatible"; the default) or "anthropic".</param>
/// <param name="BaseUrl">Provider base url; defaults per provider (OpenAI: https://api.openai.com/v1, Anthropic: https://api.anthropic.com).</param>
/// <param name="ApiKey">API key; required for availability.</param>
/// <param name="Model">Model id; required for availability — no hardcoded default model.</param>
/// <param name="Timeout">Request timeout; default 120 seconds.</param>
public sealed record LlmClientConfig(
    string? Provider = null,
    string? BaseUrl = null,
    string? ApiKey = null,
    string? Model = null,
    TimeSpan? Timeout = null)
{
    internal const string DefaultProvider = "openai";
    internal const string OpenAiDefaultBaseUrl = "https://api.openai.com/v1";
    internal const string AnthropicDefaultBaseUrl = "https://api.anthropic.com";
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);

    /// <summary>
    /// Reads configuration from DEPA_WIKI_LLM_* environment variables:
    /// DEPA_WIKI_LLM_PROVIDER (openai | anthropic), DEPA_WIKI_LLM_BASE_URL,
    /// DEPA_WIKI_LLM_API_KEY (falls back to OPENAI_API_KEY / ANTHROPIC_API_KEY per provider),
    /// DEPA_WIKI_LLM_MODEL, DEPA_WIKI_LLM_TIMEOUT_SECONDS.
    /// </summary>
    internal static LlmClientConfig FromEnvironment(Func<string, string?> getEnv)
    {
        var provider = Normalize(getEnv("DEPA_WIKI_LLM_PROVIDER"));
        var apiKey = Normalize(getEnv("DEPA_WIKI_LLM_API_KEY"))
            ?? Normalize(getEnv(IsAnthropic(provider) ? "ANTHROPIC_API_KEY" : "OPENAI_API_KEY"));
        var timeoutSeconds = Normalize(getEnv("DEPA_WIKI_LLM_TIMEOUT_SECONDS"));
        return new LlmClientConfig(
            Provider: provider,
            BaseUrl: Normalize(getEnv("DEPA_WIKI_LLM_BASE_URL")),
            ApiKey: apiKey,
            Model: Normalize(getEnv("DEPA_WIKI_LLM_MODEL")),
            Timeout: double.TryParse(timeoutSeconds, out var seconds) && seconds > 0
                ? TimeSpan.FromSeconds(seconds)
                : null);
    }

    internal static bool IsAnthropic(string? provider) =>
        string.Equals(provider, "anthropic", StringComparison.OrdinalIgnoreCase);

    internal static bool IsOpenAiCompatible(string? provider) =>
        provider is null
        || string.Equals(provider, "openai", StringComparison.OrdinalIgnoreCase)
        || string.Equals(provider, "openai-compatible", StringComparison.OrdinalIgnoreCase);

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

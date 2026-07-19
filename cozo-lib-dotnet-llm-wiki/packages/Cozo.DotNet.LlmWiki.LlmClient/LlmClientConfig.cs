namespace Cozo.DotNet.LlmWiki.LlmClient;

/// <summary>
/// Provider-level configuration (design §1.4). Sources, by priority: explicit construction
/// (CLI/tool parameters) &gt; environment variables &gt; unavailable (degraded).
/// </summary>
/// <param name="Provider">"openai" (alias "openai-compatible"; the default), "anthropic", or "codex-cli".</param>
/// <param name="BaseUrl">HTTP provider base url; defaults per provider (OpenAI: https://api.openai.com/v1, Anthropic: https://api.anthropic.com).</param>
/// <param name="ApiKey">HTTP API key; required for HTTP provider availability, unused by "codex-cli".</param>
/// <param name="Model">Optional model id. HTTP providers require it; "codex-cli" defaults to <c>gpt-5.6-terra</c> when omitted.</param>
/// <param name="Timeout">Request timeout; default 120 seconds.</param>
/// <param name="CodexCliPath">Optional absolute Codex executable path; "codex-cli" otherwise resolves `codex` from PATH.</param>
/// <param name="CodexCliModelProvider">Optional named Codex provider used with the bounded provider-only routing override.</param>
/// <param name="CodexCliBaseUrl">Optional base URL for <paramref name="CodexCliModelProvider"/>.</param>
/// <param name="CodexCliWireApi">Optional wire API for <paramref name="CodexCliModelProvider"/>; currently only <c>responses</c> is accepted.</param>
public sealed record LlmClientConfig(
    string? Provider = null,
    string? BaseUrl = null,
    string? ApiKey = null,
    string? Model = null,
    TimeSpan? Timeout = null,
    string? CodexCliPath = null,
    string? CodexCliModelProvider = null,
    string? CodexCliBaseUrl = null,
    string? CodexCliWireApi = null)
{
    internal const string DefaultProvider = "openai";
    internal const string DefaultCodexCliModel = "gpt-5.6-terra";
    internal const string OpenAiDefaultBaseUrl = "https://api.openai.com/v1";
    internal const string AnthropicDefaultBaseUrl = "https://api.anthropic.com";
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);

    /// <summary>
    /// Reads configuration from DEPA_WIKI_LLM_* environment variables:
    /// DEPA_WIKI_LLM_PROVIDER (openai | anthropic | codex-cli), DEPA_WIKI_LLM_BASE_URL,
    /// DEPA_WIKI_LLM_API_KEY (falls back to OPENAI_API_KEY / ANTHROPIC_API_KEY per provider),
    /// DEPA_WIKI_LLM_MODEL, DEPA_WIKI_LLM_TIMEOUT_SECONDS, DEPA_WIKI_CODEX_CLI_PATH, and the
    /// provider-only Codex routing variables DEPA_WIKI_CODEX_CLI_MODEL_PROVIDER,
    /// DEPA_WIKI_CODEX_CLI_BASE_URL, and DEPA_WIKI_CODEX_CLI_WIRE_API.
    /// </summary>
    internal static LlmClientConfig FromEnvironment(Func<string, string?> getEnv)
    {
        var provider = Normalize(getEnv("DEPA_WIKI_LLM_PROVIDER"));
        var apiKey = IsCodexCli(provider)
            ? null
            : Normalize(getEnv("DEPA_WIKI_LLM_API_KEY"))
                ?? Normalize(getEnv(IsAnthropic(provider) ? "ANTHROPIC_API_KEY" : "OPENAI_API_KEY"));
        var model = Normalize(getEnv("DEPA_WIKI_LLM_MODEL"));
        var timeoutSeconds = Normalize(getEnv("DEPA_WIKI_LLM_TIMEOUT_SECONDS"));
        return new LlmClientConfig(
            Provider: provider,
            BaseUrl: Normalize(getEnv("DEPA_WIKI_LLM_BASE_URL")),
            ApiKey: apiKey,
            Model: IsCodexCli(provider) ? model ?? DefaultCodexCliModel : model,
            Timeout: double.TryParse(timeoutSeconds, out var seconds) && seconds > 0
                ? TimeSpan.FromSeconds(seconds)
                : null,
            CodexCliPath: Normalize(getEnv("DEPA_WIKI_CODEX_CLI_PATH")),
            CodexCliModelProvider: Normalize(getEnv("DEPA_WIKI_CODEX_CLI_MODEL_PROVIDER")),
            CodexCliBaseUrl: Normalize(getEnv("DEPA_WIKI_CODEX_CLI_BASE_URL")),
            CodexCliWireApi: Normalize(getEnv("DEPA_WIKI_CODEX_CLI_WIRE_API")));
    }

    internal static bool IsAnthropic(string? provider) =>
        string.Equals(provider, "anthropic", StringComparison.OrdinalIgnoreCase);

    internal static bool IsOpenAiCompatible(string? provider) =>
        provider is null
        || string.Equals(provider, "openai", StringComparison.OrdinalIgnoreCase)
        || string.Equals(provider, "openai-compatible", StringComparison.OrdinalIgnoreCase);

    internal static bool IsCodexCli(string? provider) =>
        string.Equals(provider, "codex-cli", StringComparison.OrdinalIgnoreCase);

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

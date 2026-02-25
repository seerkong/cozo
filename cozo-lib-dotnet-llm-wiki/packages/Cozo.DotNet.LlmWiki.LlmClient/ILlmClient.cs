namespace Cozo.DotNet.LlmWiki.LlmClient;

/// <summary>
/// LLM backend contract (add-llm-wiki-llm-pipeline track, design §1.2). Availability is a
/// first-class citizen: a missing key/model yields <see cref="IsAvailable"/>=false with a
/// reason instead of a construction-time or call-time surprise — degradation is the normal
/// path of the wiki pipeline, not an error path.
/// </summary>
public interface ILlmClient
{
    /// <summary>Whether the client can serve completions (key + model configured, provider known).</summary>
    bool IsAvailable { get; }

    /// <summary>Why the client is unavailable (missing key, missing model, unknown provider); null when available.</summary>
    string? UnavailableReason { get; }

    /// <summary>Runs one completion. Throws <see cref="LlmException"/> on HTTP failure, empty response, or misuse while unavailable.</summary>
    Task<LlmCompletion> CompleteAsync(
        string systemPrompt,
        string userPrompt,
        LlmOptions? options = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Per-request options; null members fall back to provider/config defaults.</summary>
public sealed record LlmOptions(
    int? MaxTokens = null,
    double? Temperature = null,
    string? Model = null);

/// <summary>One completion result. Usage/model are null when the provider omits them.</summary>
public sealed record LlmCompletion(
    string Text,
    string? Model = null,
    LlmUsage? Usage = null);

/// <summary>Token accounting normalized across providers (prompt/input, completion/output).</summary>
public sealed record LlmUsage(
    int? PromptTokens = null,
    int? CompletionTokens = null);

/// <summary>
/// LLM call failure: non-2xx HTTP (carries status + truncated body), empty completion,
/// or a completion attempted on an unavailable client.
/// </summary>
public sealed class LlmException : Exception
{
    /// <summary>Bound on <see cref="ResponseBody"/> length to keep error surfaces readable.</summary>
    internal const int MaxResponseBodyLength = 2048;

    public LlmException(string message, int? statusCode = null, string? responseBody = null, Exception? inner = null)
        : base(message, inner)
    {
        StatusCode = statusCode;
        ResponseBody = responseBody is { Length: > MaxResponseBodyLength }
            ? responseBody[..MaxResponseBodyLength]
            : responseBody;
    }

    /// <summary>HTTP status code when the failure came from a non-2xx response; null otherwise.</summary>
    public int? StatusCode { get; }

    /// <summary>Response body truncated to a bounded length; null when no body applies.</summary>
    public string? ResponseBody { get; }
}

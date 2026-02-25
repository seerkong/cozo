namespace Cozo.DotNet.LlmWiki.LlmClient;

using System.Text.Json.Nodes;

/// <summary>
/// Shared HTTP plumbing for provider clients: send JSON, map non-2xx to
/// <see cref="LlmException"/> with status + truncated body, reject empty completions.
/// </summary>
internal abstract class HttpLlmClientBase : ILlmClient, IDisposable
{
    private readonly HttpClient _http;

    protected HttpLlmClientBase(LlmClientConfig config, HttpMessageHandler? handler)
    {
        Config = config;
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = config.Timeout ?? LlmClientConfig.DefaultTimeout;
    }

    protected LlmClientConfig Config { get; }

    public bool IsAvailable => true;

    public string? UnavailableReason => null;

    public async Task<LlmCompletion> CompleteAsync(string systemPrompt, string userPrompt, LlmOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var request = BuildRequest(systemPrompt, userPrompt, options ?? new LlmOptions());
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new LlmException($"LLM request to {request.RequestUri} failed: {ex.Message}", inner: ex);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new LlmException(
                    $"LLM request to {request.RequestUri} returned HTTP {(int)response.StatusCode}",
                    statusCode: (int)response.StatusCode,
                    responseBody: body);
            }

            JsonObject json;
            try
            {
                json = JsonNode.Parse(body)!.AsObject();
            }
            catch (Exception ex)
            {
                throw new LlmException("LLM response was not valid JSON", responseBody: body, inner: ex);
            }

            var completion = ParseResponse(json);
            if (string.IsNullOrEmpty(completion.Text))
            {
                throw new LlmException("LLM returned an empty completion", responseBody: body);
            }

            return completion;
        }
    }

    /// <summary>Joins the base url and a relative path without duplicate slashes.</summary>
    protected static Uri JoinUrl(string baseUrl, string path) =>
        new(baseUrl.TrimEnd('/') + "/" + path.TrimStart('/'));

    protected abstract HttpRequestMessage BuildRequest(string systemPrompt, string userPrompt, LlmOptions options);

    protected abstract LlmCompletion ParseResponse(JsonObject json);

    public void Dispose() => _http.Dispose();
}

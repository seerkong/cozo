namespace Cozo.DotNet.LlmWiki.Tests;

using System.Diagnostics;
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

        // --- codex-cli: local command availability does not require an HTTP key or model.
        // The current test process is only a resolvable executable fixture; it is never invoked.
        var executableFixture = Environment.ProcessPath;
        assert(!string.IsNullOrWhiteSpace(executableFixture) && Path.IsPathFullyQualified(executableFixture),
            "the test process should expose an absolute executable path fixture");
        var codexCli = LlmClientFactory.FromEnvironment(name => name switch
        {
            "DEPA_WIKI_LLM_PROVIDER" => "codex-cli",
            "DEPA_WIKI_CODEX_CLI_PATH" => executableFixture,
            _ => null
        });
        assert(codexCli.IsAvailable && codexCli.UnavailableReason is null,
            "a resolvable codex-cli path should be available without an HTTP API key or model");
        assert(codexCli.GetType().Name == "CodexCliLlmClient",
            "codex-cli environment selection should construct the dedicated provider");
        assert(codexCli is CodexCliLlmClient { Config.Model: LlmClientConfig.DefaultCodexCliModel },
            "codex-cli should default DEPA_WIKI_LLM_MODEL to gpt-5.6-terra when it is not configured");

        var incompleteCodexRouting = LlmClientFactory.FromEnvironment(name => name switch
        {
            "DEPA_WIKI_LLM_PROVIDER" => "codex-cli",
            "DEPA_WIKI_CODEX_CLI_PATH" => executableFixture,
            "DEPA_WIKI_CODEX_CLI_MODEL_PROVIDER" => "custom",
            _ => null
        });
        assert(!incompleteCodexRouting.IsAvailable
                && incompleteCodexRouting.UnavailableReason!.Contains("must be set together", StringComparison.Ordinal),
            "partial provider-only Codex routing should fail before a completion process starts");

        var missingCodexCli = LlmClientFactory.FromEnvironment(name => name switch
        {
            "DEPA_WIKI_LLM_PROVIDER" => "codex-cli",
            "DEPA_WIKI_CODEX_CLI_PATH" => "/definitely/missing/codex",
            _ => null
        });
        assert(!missingCodexCli.IsAvailable
                && missingCodexCli.UnavailableReason!.Contains("DEPA_WIKI_CODEX_CLI_PATH", StringComparison.Ordinal),
            "an unresolved codex-cli path should degrade with an actionable bounded configuration reason");

        // --- codex-cli process boundary (T1.3): execute only a locally generated shim.
        // The shim records what the child actually received, rather than inspecting an in-memory
        // ProcessStartInfo; this keeps the isolation contract honest across OS process creation.
        using (var fakeCodex = await FakeCodexCli.CreateAsync())
        {
            using var hostSecrets = new EnvironmentOverride(
                ("DEPA_WIKI_LLM_API_KEY", "host-depa-wiki-api-key"),
                ("OPENAI_API_KEY", "host-openai-api-key"),
                ("ANTHROPIC_API_KEY", "host-anthropic-api-key"),
                ("CODEX_UNRELATED_SECRET", "host-codex-unrelated-secret"),
                ("ARBITRARY_HOST_SECRET", "host-arbitrary-secret"));
            var targetRepository = Path.Combine(fakeCodex.Root, "target-repository");
            Directory.CreateDirectory(targetRepository);
            var processRunner = new CodexCliProcessRunner(fakeCodex.GetTestOnlyEnvironment);
            var processClient = new CodexCliLlmClient(
                fakeCodex.ExecutablePath,
                new LlmClientConfig(
                    Provider: "codex-cli",
                    Model: "configured-model",
                    Timeout: TimeSpan.FromSeconds(5)),
                processRunner);

            var success = await fakeCodex.RunAsync(
                "success",
                () => processClient.CompleteAsync(
                    "SYSTEM-TEST-CONTRACT",
                    "USER-TEST-EVIDENCE",
                    new LlmOptions(Model: "request-model")));
            assert(success.Value is { Text: "{\"schemaVersion\":\"onto-semantic-v1\",\"candidates\":[]}", Model: "request-model" },
                "codex-cli should return only the shim final-message content and the effective model");

            var successRecord = success.RecordDirectory;
            var arguments = await fakeCodex.ReadLinesAsync(successRecord, "args.txt");
            var childWorkingDirectory = (await fakeCodex.ReadTextAsync(successRecord, "cwd.txt")).Trim();
            var stdin = await fakeCodex.ReadTextAsync(successRecord, "stdin.txt");
            var childEnvironment = await fakeCodex.ReadLinesAsync(successRecord, "env.txt");
            var requestedWorkingDirectory = arguments.ElementAtOrDefault(8);
            var expectedArguments = new[]
            {
                "exec",
                "--ephemeral",
                "--skip-git-repo-check",
                "--ignore-user-config",
                "--ignore-rules",
                "--sandbox",
                "read-only",
                "--cd",
                requestedWorkingDirectory,
                "--output-last-message",
                Path.Combine(requestedWorkingDirectory ?? string.Empty, "final-message.json"),
                "--model",
                "request-model",
                "-"
            };
            assert(arguments.SequenceEqual(expectedArguments, StringComparer.Ordinal),
                "codex-cli should receive the exact bounded exec ArgumentList in order without provider-specific structured-output requirements");

            var routedStartInfo = CodexCliProcessRunner.CreateStartInfo(
                new CodexCliExecutionOptions(
                    fakeCodex.ExecutablePath,
                    "request-model",
                    TimeSpan.FromSeconds(5),
                    ModelProvider: "custom",
                    BaseUrl: "https://provider.example.test/v1",
                    WireApi: "responses"),
                Path.Combine(fakeCodex.Root, "private-cwd"),
                Path.Combine(fakeCodex.Root, "final-message.json"));
            assert(routedStartInfo.ArgumentList.Contains("--ignore-user-config", StringComparer.Ordinal)
                    && routedStartInfo.ArgumentList.Contains("model_provider=\"custom\"", StringComparer.Ordinal)
                    && routedStartInfo.ArgumentList.Contains("model_providers.custom.name=\"DEPA Wiki custom\"", StringComparer.Ordinal)
                    && routedStartInfo.ArgumentList.Contains("model_providers.custom.base_url=\"https://provider.example.test/v1\"", StringComparer.Ordinal)
                    && routedStartInfo.ArgumentList.Contains("model_providers.custom.wire_api=\"responses\"", StringComparer.Ordinal),
                "provider-only routing should preserve config isolation while supplying the selected Codex provider");
            assert(!string.IsNullOrWhiteSpace(requestedWorkingDirectory)
                    && Path.GetFileName(requestedWorkingDirectory).StartsWith("cozo-codex-cli-", StringComparison.Ordinal)
                    && Path.GetFileName(requestedWorkingDirectory) == Path.GetFileName(childWorkingDirectory)
                    && !string.Equals(requestedWorkingDirectory, targetRepository, StringComparison.Ordinal)
                    && !string.Equals(requestedWorkingDirectory, Directory.GetCurrentDirectory(), StringComparison.Ordinal),
                "codex-cli should run from a private temporary cwd, never the target repository or test cwd");
            assert(!arguments.Contains("--add-dir", StringComparer.Ordinal)
                    && !arguments.Contains(targetRepository, StringComparer.Ordinal)
                    && !arguments.Contains(Directory.GetCurrentDirectory(), StringComparer.Ordinal),
                "codex-cli should not receive an add-dir or a target/test repository path");
            assert(stdin.Contains("<system-contract>\nSYSTEM-TEST-CONTRACT\n</system-contract>", StringComparison.Ordinal)
                    && stdin.Contains("<evidence-payload>\nUSER-TEST-EVIDENCE\n</evidence-payload>", StringComparison.Ordinal),
                "codex-cli stdin should carry the bounded system/user envelope");
            assert(childEnvironment.Contains("FAKE_CODEX_MODE=success", StringComparer.Ordinal)
                    && childEnvironment.Any(entry => entry.StartsWith("FAKE_CODEX_RECORD_DIR=", StringComparison.Ordinal))
                    && childEnvironment.Any(entry => entry.StartsWith("PATH=", StringComparison.Ordinal)),
                "codex-cli child environment should contain only explicitly injected fake controls plus required launcher values");
            foreach (var forbiddenSecret in new[]
                     {
                         "DEPA_WIKI_LLM_API_KEY=host-depa-wiki-api-key",
                         "OPENAI_API_KEY=host-openai-api-key",
                         "ANTHROPIC_API_KEY=host-anthropic-api-key",
                         "CODEX_UNRELATED_SECRET=host-codex-unrelated-secret",
                         "ARBITRARY_HOST_SECRET=host-arbitrary-secret",
                     })
            {
                assert(!childEnvironment.Contains(forbiddenSecret, StringComparer.Ordinal),
                    "codex-cli child environment must not inherit host secret " + forbiddenSecret.Split('=')[0]);
            }

            if (!OperatingSystem.IsWindows())
            {
                var unixMode = (await fakeCodex.ReadTextAsync(successRecord, "mode.txt")).Trim();
                assert(unixMode == "700", "codex-cli temporary cwd must be Unix owner-only mode 0700 (got " + unixMode + ")");
            }

            assert(!Directory.Exists(childWorkingDirectory),
                "successful codex-cli completion should remove its private temporary cwd");

            foreach (var mode in new[] { "missing", "empty", "oversize" })
            {
                var failure = await fakeCodex.RunAsync(
                    mode,
                    async () =>
                    {
                        await processClient.CompleteAsync("SYSTEM-SECRET", "USER-SECRET");
                        return string.Empty;
                    });
                assert(failure.Exception is LlmException,
                    $"codex-cli {mode} final output should fail with LlmException");
                var failureCwd = (await fakeCodex.ReadTextAsync(failure.RecordDirectory, "cwd.txt")).Trim();
                assert(!Directory.Exists(failureCwd),
                    $"codex-cli {mode} failure should remove its private temporary cwd");
            }

            var nonzero = await fakeCodex.RunAsync(
                "nonzero",
                async () =>
                {
                    await processClient.CompleteAsync("SYSTEM-SECRET", "USER-SECRET");
                    return string.Empty;
                });
            assert(nonzero.Exception is LlmException
                    && !nonzero.Exception.Message.Contains("SYSTEM-SECRET", StringComparison.Ordinal)
                    && !nonzero.Exception.Message.Contains("USER-SECRET", StringComparison.Ordinal)
                    && !nonzero.Exception.Message.Contains(FakeCodexCli.DiagnosticSecret, StringComparison.Ordinal),
                "nonzero codex-cli failure should expose only bounded diagnostics, never stdin or child diagnostic content");
            var nonzeroCwd = (await fakeCodex.ReadTextAsync(nonzero.RecordDirectory, "cwd.txt")).Trim();
            assert(!Directory.Exists(nonzeroCwd),
                "nonzero codex-cli failure should remove its private temporary cwd");

            var timeoutClient = new CodexCliLlmClient(
                fakeCodex.ExecutablePath,
                new LlmClientConfig(Provider: "codex-cli", Timeout: TimeSpan.FromMilliseconds(250)),
                processRunner);
            var timedOut = await fakeCodex.RunAsync(
                "sleep",
                async () =>
                {
                    await timeoutClient.CompleteAsync("SYSTEM-SECRET", "USER-SECRET");
                    return string.Empty;
                });
            assert(timedOut.Exception is LlmException timeoutFailure
                    && timeoutFailure.Message.Contains("timed out", StringComparison.Ordinal),
                "codex-cli timeout should fail with a bounded timeout LlmException");
            await fakeCodex.AssertChildStoppedAsync(timedOut.RecordDirectory, assert, "timeout");

            var externalCancellation = new CancellationTokenSource();
            var canceledRecord = fakeCodex.CreateRecordDirectory();
            using (fakeCodex.SetScenario("sleep", canceledRecord))
            {
                var cancellationTask = processClient.CompleteAsync(
                    "SYSTEM-SECRET",
                    "USER-SECRET",
                    cancellationToken: externalCancellation.Token);
                await fakeCodex.WaitForFileAsync(canceledRecord, "child.pid");
                externalCancellation.Cancel();
                var canceled = await Record(async () => await cancellationTask);
                assert(canceled is LlmException cancellationFailure
                        && cancellationFailure.Message.Contains("canceled", StringComparison.Ordinal),
                    "external codex-cli cancellation should fail with a bounded cancellation LlmException");
            }
            await fakeCodex.AssertChildStoppedAsync(canceledRecord, assert, "external cancellation");

            var privateDirectoryFailureRunner = new CodexCliProcessRunner(
                fakeCodex.GetTestOnlyEnvironment,
                () => throw new LlmException("test-only private directory failure"));
            var privateDirectoryFailureClient = new CodexCliLlmClient(
                fakeCodex.ExecutablePath,
                new LlmClientConfig(Provider: "codex-cli", Timeout: TimeSpan.FromSeconds(5)),
                privateDirectoryFailureRunner);
            var privateDirectoryFailure = await fakeCodex.RunAsync(
                "success",
                async () =>
                {
                    await privateDirectoryFailureClient.CompleteAsync("SYSTEM-SECRET", "USER-SECRET");
                    return string.Empty;
                });
            assert(privateDirectoryFailure.Exception is LlmException privateDirectoryException
                    && privateDirectoryException.Message.Contains("private directory failure", StringComparison.Ordinal)
                    && !File.Exists(Path.Combine(privateDirectoryFailure.RecordDirectory, "cwd.txt")),
                "a private-directory failure must abort before starting the codex-cli child process");
        }

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

    private sealed class FakeCodexCli : IDisposable
    {
        internal const string DiagnosticSecret = "fake-codex-diagnostic-secret";
        private readonly string _recordsRoot;
        private readonly object _scenarioGate = new();
        private IReadOnlyDictionary<string, string>? _testOnlyEnvironment;

        private FakeCodexCli(string root, string executablePath)
        {
            Root = root;
            ExecutablePath = executablePath;
            _recordsRoot = Path.Combine(root, "records");
        }

        internal string Root { get; }

        internal string ExecutablePath { get; }

        internal static async Task<FakeCodexCli> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "cozo-codex-cli-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(Path.Combine(root, "records"));
            var executablePath = Path.Combine(root, OperatingSystem.IsWindows() ? "codex-fake.cmd" : "codex-fake");
            await File.WriteAllTextAsync(executablePath, OperatingSystem.IsWindows() ? WindowsScript : UnixScript);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    executablePath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            return new FakeCodexCli(root, executablePath);
        }

        internal string CreateRecordDirectory()
        {
            var directory = Path.Combine(_recordsRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            return directory;
        }

        internal async Task<FakeCodexResult<T>> RunAsync<T>(string mode, Func<Task<T>> action)
        {
            var recordDirectory = CreateRecordDirectory();
            using var scenario = SetScenario(mode, recordDirectory);
            try
            {
                return new FakeCodexResult<T>(await action(), null, recordDirectory);
            }
            catch (Exception ex)
            {
                return new FakeCodexResult<T>(default, ex, recordDirectory);
            }
        }

        internal IDisposable SetScenario(string mode, string recordDirectory)
        {
            lock (_scenarioGate)
            {
                var previous = _testOnlyEnvironment;
                _testOnlyEnvironment = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["FAKE_CODEX_MODE"] = mode,
                    ["FAKE_CODEX_RECORD_DIR"] = recordDirectory,
                    ["FAKE_CODEX_DIAGNOSTIC_SECRET"] = DiagnosticSecret,
                };
                return new ActionOnDispose(() =>
                {
                    lock (_scenarioGate)
                    {
                        _testOnlyEnvironment = previous;
                    }
                });
            }
        }

        internal IReadOnlyDictionary<string, string>? GetTestOnlyEnvironment()
        {
            lock (_scenarioGate)
            {
                return _testOnlyEnvironment is null
                    ? null
                    : new Dictionary<string, string>(_testOnlyEnvironment, StringComparer.Ordinal);
            }
        }

        internal Task<string> ReadTextAsync(string recordDirectory, string fileName) =>
            File.ReadAllTextAsync(Path.Combine(recordDirectory, fileName));

        internal async Task<string[]> ReadLinesAsync(string recordDirectory, string fileName) =>
            await File.ReadAllLinesAsync(Path.Combine(recordDirectory, fileName));

        internal async Task WaitForFileAsync(string recordDirectory, string fileName)
        {
            var path = Path.Combine(recordDirectory, fileName);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
            while (!File.Exists(path) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
            }

            if (!File.Exists(path))
            {
                throw new InvalidOperationException($"fake codex did not create {fileName}");
            }
        }

        internal async Task AssertChildStoppedAsync(string recordDirectory, Action<bool, string> assert, string scenario)
        {
            await WaitForFileAsync(recordDirectory, "child.pid");
            var processIdText = (await ReadTextAsync(recordDirectory, "child.pid")).Trim();
            assert(int.TryParse(processIdText, out var processId),
                $"fake codex {scenario} child pid should be parseable");

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
            while (IsProcessAlive(processId) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
            }

            assert(!IsProcessAlive(processId),
                $"codex-cli {scenario} should terminate the fake child process tree");
            var childWorkingDirectory = (await ReadTextAsync(recordDirectory, "cwd.txt")).Trim();
            assert(!Directory.Exists(childWorkingDirectory),
                $"codex-cli {scenario} should remove its private temporary cwd");
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }

        private static bool IsProcessAlive(int processId)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                return !process.HasExited;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        private const string UnixScript = """
            #!/usr/bin/env bash
            set -eu
            record_dir="${FAKE_CODEX_RECORD_DIR:?}"
            printf '%s\n' "$PWD" > "$record_dir/cwd.txt"
            printf '%s\n' "$@" > "$record_dir/args.txt"
            env | sort > "$record_dir/env.txt"
            if stat -f '%Lp' "$PWD" >/dev/null 2>&1; then
              stat -f '%Lp' "$PWD" > "$record_dir/mode.txt"
            else
              stat -c '%a' "$PWD" > "$record_dir/mode.txt"
            fi
            cat > "$record_dir/stdin.txt"
            output=""
            while [[ "$#" -gt 0 ]]; do
              if [[ "$1" == "--output-last-message" ]]; then
                output="$2"
                shift 2
                continue
              fi
              shift
            done
            case "${FAKE_CODEX_MODE:?}" in
              success)
                printf '%s' '{"schemaVersion":"onto-semantic-v1","candidates":[]}' > "$output"
                ;;
              nonzero)
                printf '%s' "$FAKE_CODEX_DIAGNOSTIC_SECRET" >&2
                exit 23
                ;;
              missing)
                ;;
              empty)
                : > "$output"
                ;;
              oversize)
                head -c 98305 /dev/zero | tr '\\0' x > "$output"
                ;;
              sleep)
                sleep 30 &
                child="$!"
                printf '%s\n' "$child" > "$record_dir/child.pid"
                wait "$child"
                ;;
              *)
                exit 99
                ;;
            esac
            """;

        private const string WindowsScript = """
            @echo off
            setlocal EnableExtensions DisableDelayedExpansion
            set "record_dir=%FAKE_CODEX_RECORD_DIR%"
            cd > "%record_dir%\cwd.txt"
            set > "%record_dir%\env.txt"
            type nul > "%record_dir%\args.txt"
            set "output="
            :args
            if "%~1"=="" goto args_done
            echo(%~1>> "%record_dir%\args.txt"
            if "%~1"=="--output-last-message" set "output=%~2"
            shift
            goto args
            :args_done
            more > "%record_dir%\stdin.txt"
            if /I "%FAKE_CODEX_MODE%"=="success" > "%output%" <nul set /p "={\"schemaVersion\":\"onto-semantic-v1\",\"candidates\":[]}"
            if /I "%FAKE_CODEX_MODE%"=="nonzero" (
              echo %FAKE_CODEX_DIAGNOSTIC_SECRET% 1>&2
              exit /b 23
            )
            if /I "%FAKE_CODEX_MODE%"=="empty" type nul > "%output%"
            if /I "%FAKE_CODEX_MODE%"=="oversize" (
              for /L %%i in (1,1,98305) do <nul set /p "=x" >> "%output%"
            )
            if /I "%FAKE_CODEX_MODE%"=="sleep" (
              powershell -NoProfile -Command "$p = Start-Process -FilePath $env:ComSpec -ArgumentList '/c timeout /t 30 /nobreak > nul' -PassThru; Set-Content -LiteralPath (Join-Path $env:FAKE_CODEX_RECORD_DIR 'child.pid') -Value $p.Id; Wait-Process -Id $p.Id"
            )
            exit /b 0
            """;
    }

    private sealed record FakeCodexResult<T>(T? Value, Exception? Exception, string RecordDirectory);

    private sealed class ActionOnDispose(Action action) : IDisposable
    {
        private Action? _action = action;

        public void Dispose() => Interlocked.Exchange(ref _action, null)?.Invoke();
    }

    private sealed class EnvironmentOverride : IDisposable
    {
        private readonly Dictionary<string, string?> _previous;

        public EnvironmentOverride(params (string Name, string Value)[] values)
        {
            _previous = values.ToDictionary(
                value => value.Name,
                value => Environment.GetEnvironmentVariable(value.Name),
                StringComparer.Ordinal);
            foreach (var (name, value) in values)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }

        public void Dispose()
        {
            foreach (var (name, value) in _previous)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }
}

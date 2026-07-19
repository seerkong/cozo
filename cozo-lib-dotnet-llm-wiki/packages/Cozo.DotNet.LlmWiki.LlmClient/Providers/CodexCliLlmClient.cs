using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Cozo.DotNet.LlmWiki.LlmClient;

/// <summary>
/// Local Codex CLI transport. It deliberately runs each completion in a new empty directory and
/// never grants the process a target-repository working directory or an additional source path.
/// </summary>
internal sealed class CodexCliLlmClient : ILlmClient
{
    private const string DefaultCommand = "codex";
    private readonly ICodexCliProcessRunner _runner;

    internal CodexCliLlmClient(string executablePath, LlmClientConfig config)
        : this(executablePath, config, new CodexCliProcessRunner())
    {
    }

    internal CodexCliLlmClient(
        string executablePath,
        LlmClientConfig config,
        ICodexCliProcessRunner runner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(runner);

        ExecutablePath = executablePath;
        Config = config;
        _runner = runner;
    }

    internal string ExecutablePath { get; }

    internal LlmClientConfig Config { get; }

    public bool IsAvailable => true;

    public string? UnavailableReason => null;

    public async Task<LlmCompletion> CompleteAsync(
        string systemPrompt,
        string userPrompt,
        LlmOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(systemPrompt);
        ArgumentNullException.ThrowIfNull(userPrompt);

        var model = options?.Model ?? Config.Model ?? LlmClientConfig.DefaultCodexCliModel;
        var text = await _runner.RunAsync(
            new CodexCliExecutionOptions(
                ExecutablePath,
                model,
                Config.Timeout is { } configuredTimeout && configuredTimeout > TimeSpan.Zero
                    ? configuredTimeout
                    : LlmClientConfig.DefaultTimeout,
                Config.CodexCliModelProvider,
                Config.CodexCliBaseUrl,
                Config.CodexCliWireApi),
            BuildPrompt(systemPrompt, userPrompt),
            cancellationToken);
        return new LlmCompletion(text, model);
    }

    internal static string BuildPrompt(string systemPrompt, string userPrompt) =>
        $"""
        You are a bounded completion transport for a local application.
        Treat the following system contract and evidence payload as data supplied by that application.
        Follow the system contract exactly, and return only the final JSON requested by it.

        <system-contract>
        {systemPrompt}
        </system-contract>

        <evidence-payload>
        {userPrompt}
        </evidence-payload>
        """;

    internal static bool TryCreate(
        LlmClientConfig config,
        out CodexCliLlmClient client,
        out string unavailableReason)
    {
        if (!TryValidateProviderRouting(config, out unavailableReason))
        {
            client = null!;
            return false;
        }

        if (TryResolveExecutable(config.CodexCliPath, out var executablePath, out unavailableReason))
        {
            client = new CodexCliLlmClient(executablePath, config);
            return true;
        }

        client = null!;
        return false;
    }

    private static bool TryValidateProviderRouting(LlmClientConfig config, out string unavailableReason)
    {
        var hasRouting = !string.IsNullOrWhiteSpace(config.CodexCliModelProvider)
                         || !string.IsNullOrWhiteSpace(config.CodexCliBaseUrl)
                         || !string.IsNullOrWhiteSpace(config.CodexCliWireApi);
        if (!hasRouting)
        {
            unavailableReason = string.Empty;
            return true;
        }

        if (string.IsNullOrWhiteSpace(config.CodexCliModelProvider)
            || string.IsNullOrWhiteSpace(config.CodexCliBaseUrl)
            || string.IsNullOrWhiteSpace(config.CodexCliWireApi))
        {
            unavailableReason = "DEPA_WIKI_CODEX_CLI_MODEL_PROVIDER, DEPA_WIKI_CODEX_CLI_BASE_URL, and DEPA_WIKI_CODEX_CLI_WIRE_API must be set together";
            return false;
        }

        if (!Regex.IsMatch(config.CodexCliModelProvider, "^[A-Za-z][A-Za-z0-9_-]*$", RegexOptions.CultureInvariant)
            || !Uri.TryCreate(config.CodexCliBaseUrl, UriKind.Absolute, out var baseUrl)
            || (baseUrl.Scheme != Uri.UriSchemeHttp && baseUrl.Scheme != Uri.UriSchemeHttps)
            || !string.Equals(config.CodexCliWireApi, "responses", StringComparison.Ordinal))
        {
            unavailableReason = "Codex provider-only routing requires a simple provider name, an absolute http(s) base URL, and DEPA_WIKI_CODEX_CLI_WIRE_API=responses";
            return false;
        }

        unavailableReason = string.Empty;
        return true;
    }

    private static bool TryResolveExecutable(string? configuredPath, out string executablePath, out string unavailableReason)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            if (!Path.IsPathFullyQualified(configuredPath))
            {
                executablePath = string.Empty;
                unavailableReason = "DEPA_WIKI_CODEX_CLI_PATH must be an absolute executable path";
                return false;
            }

            if (!IsExecutableFile(configuredPath))
            {
                executablePath = string.Empty;
                unavailableReason = "DEPA_WIKI_CODEX_CLI_PATH does not resolve to an executable file";
                return false;
            }

            executablePath = Path.GetFullPath(configuredPath);
            unavailableReason = string.Empty;
            return true;
        }

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Path.IsPathFullyQualified(directory))
            {
                continue;
            }

            foreach (var commandName in CommandNames())
            {
                var candidate = Path.Combine(directory, commandName);
                if (IsExecutableFile(candidate))
                {
                    executablePath = candidate;
                    unavailableReason = string.Empty;
                    return true;
                }
            }
        }

        executablePath = string.Empty;
        unavailableReason = "codex command was not found on PATH; set DEPA_WIKI_CODEX_CLI_PATH to an absolute executable path";
        return false;
    }

    private static IEnumerable<string> CommandNames()
    {
        if (!OperatingSystem.IsWindows())
        {
            yield return DefaultCommand;
            yield break;
        }

        var extensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var extension in extensions)
        {
            yield return DefaultCommand + extension;
        }
    }

    private static bool IsExecutableFile(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        if (OperatingSystem.IsWindows())
        {
            return true;
        }

        try
        {
            var mode = File.GetUnixFileMode(path);
            const UnixFileMode executeMask = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            return (mode & executeMask) != 0;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (PlatformNotSupportedException)
        {
            return true;
        }
    }
}

/// <summary>Internal execution seam for deterministic fake-process tests.</summary>
internal interface ICodexCliProcessRunner
{
    Task<string> RunAsync(CodexCliExecutionOptions options, string prompt, CancellationToken cancellationToken);
}

internal sealed record CodexCliExecutionOptions(
    string ExecutablePath,
    string? Model,
    TimeSpan Timeout,
    string? ModelProvider = null,
    string? BaseUrl = null,
    string? WireApi = null);

internal sealed class CodexCliProcessRunner : ICodexCliProcessRunner
{
    internal const int MaxFinalOutputBytes = 96 * 1024;
    private const int MaxDiagnosticTailChars = 8 * 1024;
    private const string TempDirectoryPrefix = "cozo-codex-cli-";
    private const string FinalMessageFileName = "final-message.json";
    private static readonly string[] ChildEnvironmentAllowlist =
    [
        "HOME",
        "CODEX_HOME",
        "PATH",
        "LANG",
        "LC_ALL",
        "LC_CTYPE",
        "LC_MESSAGES",
        "TMPDIR",
        "TEMP",
        "TMP",
        "USERPROFILE",
        "LOCALAPPDATA",
        "APPDATA",
        "XDG_CONFIG_HOME",
        "XDG_CACHE_HOME",
        "XDG_STATE_HOME",
        "XDG_RUNTIME_DIR",
        "SystemRoot",
        "WINDIR",
        "ComSpec",
        "PATHEXT",
    ];

    private readonly Func<IReadOnlyDictionary<string, string>?>? _testOnlyEnvironmentFactory;
    private readonly Func<string>? _testOnlyPrivateTempDirectoryFactory;

    internal CodexCliProcessRunner()
    {
    }

    // This seam is internal to the test assembly. Production callers never get to add arbitrary
    // child environment variables or bypass the private-directory creation path.
    internal CodexCliProcessRunner(
        Func<IReadOnlyDictionary<string, string>?> testOnlyEnvironmentFactory,
        Func<string>? testOnlyPrivateTempDirectoryFactory = null)
    {
        ArgumentNullException.ThrowIfNull(testOnlyEnvironmentFactory);
        _testOnlyEnvironmentFactory = testOnlyEnvironmentFactory;
        _testOnlyPrivateTempDirectoryFactory = testOnlyPrivateTempDirectoryFactory;
    }

    public async Task<string> RunAsync(
        CodexCliExecutionOptions options,
        string prompt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(prompt);

        var tempDirectory = string.Empty;
        Process? process = null;
        Task<BoundedDiagnostic>? stdoutTask = null;
        Task<BoundedDiagnostic>? stderrTask = null;

        using var timeout = new CancellationTokenSource(options.Timeout);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            tempDirectory = (_testOnlyPrivateTempDirectoryFactory ?? CreatePrivateTempDirectory)();
            var finalMessagePath = Path.Combine(tempDirectory, FinalMessageFileName);
            cancellationToken.ThrowIfCancellationRequested();

            process = StartProcess(options, tempDirectory, finalMessagePath);
            stdoutTask = DrainDiagnosticAsync(process.StandardOutput);
            stderrTask = DrainDiagnosticAsync(process.StandardError);

            try
            {
                await process.StandardInput.WriteAsync(prompt.AsMemory(), linkedCancellation.Token);
                await process.StandardInput.FlushAsync(linkedCancellation.Token);
                process.StandardInput.Close();
                await process.WaitForExitAsync(linkedCancellation.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested || cancellationToken.IsCancellationRequested)
            {
                await StopProcessTreeAsync(process);
                throw new LlmException(timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested
                    ? "codex-cli completion timed out"
                    : "codex-cli completion was canceled");
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                await StopProcessTreeAsync(process);
                throw new LlmException("codex-cli completion process I/O failed", inner: ex);
            }

            var diagnostics = await AwaitDiagnosticsAsync(stdoutTask, stderrTask);
            if (process.ExitCode != 0)
            {
                throw new LlmException($"codex-cli exited with code {process.ExitCode}{diagnostics.Summary}");
            }

            return await ReadFinalMessageAsync(finalMessagePath, cancellationToken);
        }
        catch (LlmException)
        {
            throw;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested || cancellationToken.IsCancellationRequested)
        {
            if (process is not null)
            {
                await StopProcessTreeAsync(process);
            }

            throw new LlmException(timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested
                ? "codex-cli completion timed out"
                : "codex-cli completion was canceled");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException or UnauthorizedAccessException or IOException or InvalidOperationException)
        {
            throw new LlmException("failed to start or read from codex-cli completion process", inner: ex);
        }
        finally
        {
            if (process is not null)
            {
                await StopProcessTreeAsync(process);
                await AwaitDiagnosticsSilentlyAsync(stdoutTask, stderrTask);
                process.Dispose();
            }

            if (!string.IsNullOrEmpty(tempDirectory))
            {
                TryDeleteDirectory(tempDirectory);
            }
        }
    }

    internal static ProcessStartInfo CreateStartInfo(
        CodexCliExecutionOptions options,
        string temporaryWorkingDirectory,
        string finalMessagePath,
        IReadOnlyDictionary<string, string>? testOnlyEnvironment = null)
    {
        var startInfo = new ProcessStartInfo(options.ExecutablePath)
        {
            WorkingDirectory = temporaryWorkingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        PopulateSealedEnvironment(startInfo, testOnlyEnvironment);
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add("--ephemeral");
        startInfo.ArgumentList.Add("--skip-git-repo-check");
        startInfo.ArgumentList.Add("--ignore-user-config");
        startInfo.ArgumentList.Add("--ignore-rules");
        AddProviderRoutingOverrides(startInfo, options);
        startInfo.ArgumentList.Add("--sandbox");
        startInfo.ArgumentList.Add("read-only");
        startInfo.ArgumentList.Add("--cd");
        startInfo.ArgumentList.Add(temporaryWorkingDirectory);
        startInfo.ArgumentList.Add("--output-last-message");
        startInfo.ArgumentList.Add(finalMessagePath);
        if (!string.IsNullOrWhiteSpace(options.Model))
        {
            startInfo.ArgumentList.Add("--model");
            startInfo.ArgumentList.Add(options.Model);
        }

        startInfo.ArgumentList.Add("-");
        return startInfo;
    }

    private static void AddProviderRoutingOverrides(ProcessStartInfo startInfo, CodexCliExecutionOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ModelProvider))
        {
            return;
        }

        AddTomlConfig(startInfo, "model_provider", options.ModelProvider);
        AddTomlConfig(startInfo, $"model_providers.{options.ModelProvider}.name", $"DEPA Wiki {options.ModelProvider}");
        AddTomlConfig(startInfo, $"model_providers.{options.ModelProvider}.base_url", options.BaseUrl!);
        AddTomlConfig(startInfo, $"model_providers.{options.ModelProvider}.wire_api", options.WireApi!);
    }

    private static void AddTomlConfig(ProcessStartInfo startInfo, string key, string value)
    {
        startInfo.ArgumentList.Add("--config");
        startInfo.ArgumentList.Add(key + "=" + JsonSerializer.Serialize(value));
    }

    private static void PopulateSealedEnvironment(
        ProcessStartInfo startInfo,
        IReadOnlyDictionary<string, string>? testOnlyEnvironment)
    {
        // ProcessStartInfo inherits the parent environment by default. Clear it first so a future
        // host secret is denied by construction, instead of relying on a blocklist to stay current.
        startInfo.Environment.Clear();
        foreach (var name in ChildEnvironmentAllowlist)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value))
            {
                startInfo.Environment[name] = value;
            }
        }

        if (testOnlyEnvironment is null)
        {
            return;
        }

        foreach (var (name, value) in testOnlyEnvironment)
        {
            if (!name.StartsWith("FAKE_CODEX_", StringComparison.Ordinal)
                || string.IsNullOrEmpty(value))
            {
                throw new ArgumentException(
                    "test-only Codex CLI environment entries must be nonempty FAKE_CODEX_* values",
                    nameof(testOnlyEnvironment));
            }

            startInfo.Environment[name] = value;
        }
    }

    private Process StartProcess(
        CodexCliExecutionOptions options,
        string temporaryWorkingDirectory,
        string finalMessagePath)
    {
        var process = Process.Start(CreateStartInfo(
            options,
            temporaryWorkingDirectory,
            finalMessagePath,
            _testOnlyEnvironmentFactory?.Invoke()));
        return process ?? throw new LlmException("failed to start codex-cli completion process");
    }

    private static async Task<string> ReadFinalMessageAsync(string finalMessagePath, CancellationToken cancellationToken)
    {
        try
        {
            var info = new FileInfo(finalMessagePath);
            if (!info.Exists || info.Length == 0)
            {
                throw new LlmException("codex-cli did not produce a final completion");
            }

            if (info.Length > MaxFinalOutputBytes)
            {
                throw new LlmException($"codex-cli final completion exceeded the {MaxFinalOutputBytes}-byte limit");
            }

            await using var stream = new FileStream(
                finalMessagePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                useAsync: true);
            using var buffer = new MemoryStream((int)info.Length);
            await stream.CopyToAsync(buffer, 4096, cancellationToken);
            if (buffer.Length == 0)
            {
                throw new LlmException("codex-cli did not produce a final completion");
            }

            if (buffer.Length > MaxFinalOutputBytes)
            {
                throw new LlmException($"codex-cli final completion exceeded the {MaxFinalOutputBytes}-byte limit");
            }

            return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        }
        catch (LlmException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            throw new LlmException("could not read codex-cli final completion", inner: ex);
        }
    }

    private static async Task<BoundedDiagnostic> DrainDiagnosticAsync(StreamReader reader)
    {
        var tail = new StringBuilder(MaxDiagnosticTailChars);
        var buffer = new char[1024];
        var totalChars = 0;
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory())) > 0)
        {
            totalChars += read;
            tail.Append(buffer, 0, read);
            if (tail.Length > MaxDiagnosticTailChars)
            {
                tail.Remove(0, tail.Length - MaxDiagnosticTailChars);
            }
        }

        return new BoundedDiagnostic(totalChars, totalChars > MaxDiagnosticTailChars);
    }

    private static async Task<ProcessDiagnostics> AwaitDiagnosticsAsync(
        Task<BoundedDiagnostic>? stdoutTask,
        Task<BoundedDiagnostic>? stderrTask)
    {
        var stdout = stdoutTask is null ? default : await stdoutTask;
        var stderr = stderrTask is null ? default : await stderrTask;
        return new ProcessDiagnostics(stdout, stderr);
    }

    private static async Task AwaitDiagnosticsSilentlyAsync(
        Task<BoundedDiagnostic>? stdoutTask,
        Task<BoundedDiagnostic>? stderrTask)
    {
        try
        {
            if (stdoutTask is not null)
            {
                await stdoutTask;
            }

            if (stderrTask is not null)
            {
                await stderrTask;
            }
        }
        catch (Exception)
        {
            // Diagnostics are deliberately never surfaced as prompt/response-bearing error text.
        }
    }

    private static async Task StopProcessTreeAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            return;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return;
        }

        try
        {
            await process.WaitForExitAsync();
        }
        catch (InvalidOperationException)
        {
            // The process exited between HasExited and WaitForExitAsync.
        }
    }

    private static string CreatePrivateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), TempDirectoryPrefix + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            if (OperatingSystem.IsWindows())
            {
                ProtectWindowsDirectoryForCurrentUser(directory);
            }
            else
            {
                ProtectUnixDirectoryForCurrentUser(directory);
            }

            return directory;
        }
        catch (Exception ex) when (ex is IOException
                                    or UnauthorizedAccessException
                                    or PlatformNotSupportedException
                                    or InvalidOperationException
                                    or IdentityNotMappedException
                                    or System.ComponentModel.Win32Exception
                                    or System.Security.SecurityException)
        {
            TryDeleteDirectory(directory);
            throw new LlmException("failed to create a private codex-cli temporary directory", inner: ex);
        }
    }

    [UnsupportedOSPlatform("windows")]
    private static void ProtectUnixDirectoryForCurrentUser(string directory)
    {
        const UnixFileMode ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        File.SetUnixFileMode(directory, ownerOnly);
        var actual = File.GetUnixFileMode(directory);
        if (actual != ownerOnly)
        {
            throw new IOException("codex-cli temporary directory did not retain owner-only permissions");
        }
    }

    [SupportedOSPlatform("windows")]
    private static void ProtectWindowsDirectoryForCurrentUser(string directory)
    {
        var currentUser = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("could not resolve the current Windows user");
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(currentUser);
        security.AddAccessRule(new FileSystemAccessRule(
            currentUser,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        var directoryInfo = new DirectoryInfo(directory);
        directoryInfo.SetAccessControl(security);

        var actual = directoryInfo.GetAccessControl();
        var rules = actual.GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier))
            .OfType<FileSystemAccessRule>()
            .ToArray();
        var owner = actual.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (!actual.AreAccessRulesProtected
            || owner is null
            || owner != currentUser
            || rules.Length != 1
            || rules[0].IdentityReference != currentUser
            || rules[0].AccessControlType != AccessControlType.Allow
            || (rules[0].FileSystemRights & FileSystemRights.FullControl) != FileSystemRights.FullControl)
        {
            throw new IOException("codex-cli temporary directory did not retain a protected current-user ACL");
        }
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // A cleanup failure must not replace a completion failure or expose temp paths.
        }
        catch (UnauthorizedAccessException)
        {
            // A cleanup failure must not replace a completion failure or expose temp paths.
        }
    }

    private readonly record struct BoundedDiagnostic(int TotalChars, bool WasTruncated);

    private readonly record struct ProcessDiagnostics(BoundedDiagnostic Stdout, BoundedDiagnostic Stderr)
    {
        public string Summary => Stdout.TotalChars == 0 && Stderr.TotalChars == 0
            ? string.Empty
            : "; diagnostic output suppressed";
    }
}

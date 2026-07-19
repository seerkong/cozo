using System.Diagnostics;
using System.Xml.Linq;

namespace Cozo.DotNet.LlmWiki.Tools;

/// <summary>
/// Runs the canonical generated-XML validator before atomically publishing a new candidate-only
/// bundle. It never reads or mutates accepted ontology, review, or materialization state.
/// </summary>
public sealed record BusinessOntologyCandidateBundlePublicationRequest(
    BusinessOntologyCandidateBundleRequest BundleRequest,
    string OutputDirectory,
    string BunExecutable,
    string ValidatorScriptPath,
    string BundleName);

public sealed record BusinessOntologyCandidateBundleValidationResult(
    bool Succeeded,
    int ExitCode,
    string StandardOutput,
    string StandardError);

public sealed record BusinessOntologyCandidateBundlePublicationResult(
    BusinessOntologyCandidateBundleBuildResult Build,
    string? PublishedDirectory,
    BusinessOntologyCandidateBundleValidationResult? Validation)
{
    public bool Published => PublishedDirectory is not null;
}

public sealed class BusinessOntologyCandidateBundlePublisher(BusinessOntologyCandidateBundleBuilder builder)
{
    private static readonly TimeSpan ValidatorTimeout = TimeSpan.FromSeconds(60);

    public async Task<BusinessOntologyCandidateBundlePublicationResult> PublishAsync(
        BusinessOntologyCandidateBundlePublicationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var build = await builder.BuildAsync(request.BundleRequest, cancellationToken);
        return await PublishFreshBuildAsync(build, request, cancellationToken);
    }

    // Internal test seam only. Do not trust a supplied result: rebuild it from the isolated
    // analysis workspace and require exact semantic equivalence before publishing.
    internal async Task<BusinessOntologyCandidateBundlePublicationResult> PublishBuiltAsync(
        BusinessOntologyCandidateBundleBuildResult build,
        BusinessOntologyCandidateBundlePublicationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(build);
        ArgumentNullException.ThrowIfNull(request);
        var authoritativeBuild = await builder.BuildAsync(request.BundleRequest, cancellationToken);
        RequireAuthoritativeBuild(build, authoritativeBuild);
        return await PublishFreshBuildAsync(authoritativeBuild, request, cancellationToken);
    }

    private async Task<BusinessOntologyCandidateBundlePublicationResult> PublishFreshBuildAsync(
        BusinessOntologyCandidateBundleBuildResult build,
        BusinessOntologyCandidateBundlePublicationRequest request,
        CancellationToken cancellationToken)
    {
        if (!build.HasMappableCandidates)
        {
            return new BusinessOntologyCandidateBundlePublicationResult(build, null, null);
        }

        var outputDirectory = RequireDirectory(request.OutputDirectory, nameof(request.OutputDirectory));
        var bunExecutable = RequireExistingFile(request.BunExecutable, nameof(request.BunExecutable));
        var validatorScript = RequireTrustedValidatorScript(request.ValidatorScriptPath);
        var bundleName = RequireBundleName(request.BundleName);
        var destination = Path.Combine(outputDirectory, bundleName);
        if (Directory.Exists(destination) || File.Exists(destination))
        {
            throw new IOException($"Candidate bundle destination must be new: {destination}");
        }

        var staging = Path.Combine(outputDirectory, ".staging-" + Guid.NewGuid().ToString("N"));
        string? writtenStaging = null;
        try
        {
            writtenStaging = await builder.WriteStagingAsync(build, staging, cancellationToken);
            if (writtenStaging is null)
            {
                return new BusinessOntologyCandidateBundlePublicationResult(build, null, null);
            }

            var validation = await ValidateAsync(
                bunExecutable,
                validatorScript,
                Path.Combine(writtenStaging, "ontology.xml"),
                outputDirectory,
                cancellationToken);
            if (!validation.Succeeded)
            {
                return new BusinessOntologyCandidateBundlePublicationResult(build, null, validation);
            }

            // staging and destination are siblings, so Directory.Move is one filesystem rename.
            Directory.Move(writtenStaging, destination);
            writtenStaging = null;
            return new BusinessOntologyCandidateBundlePublicationResult(build, destination, validation);
        }
        finally
        {
            if (writtenStaging is not null)
            {
                try { Directory.Delete(writtenStaging, recursive: true); } catch { }
            }
        }
    }

    private static void RequireAuthoritativeBuild(
        BusinessOntologyCandidateBundleBuildResult supplied,
        BusinessOntologyCandidateBundleBuildResult authoritative)
    {
        if (!StringComparer.Ordinal.Equals(supplied.OntologyId, authoritative.OntologyId)
            || !StringComparer.Ordinal.Equals(supplied.GenerationId, authoritative.GenerationId)
            || !EquivalentMappings(supplied.Mappings, authoritative.Mappings)
            || !EquivalentExclusions(supplied.Exclusions, authoritative.Exclusions)
            || !EquivalentFiles(supplied.Files, authoritative.Files))
        {
            throw new ArgumentException(
                "Prebuilt candidate bundle does not exactly match the hypothesis-only build reconstructed from the isolated analysis workspace.",
                nameof(supplied));
        }
    }

    private static bool EquivalentMappings(
        IReadOnlyList<BusinessOntologyCandidateBundleMapping> left,
        IReadOnlyList<BusinessOntologyCandidateBundleMapping> right) =>
        left.OrderBy(item => item.RunId, StringComparer.Ordinal).ThenBy(item => item.RecordId, StringComparer.Ordinal)
            .SequenceEqual(
                right.OrderBy(item => item.RunId, StringComparer.Ordinal).ThenBy(item => item.RecordId, StringComparer.Ordinal),
                CandidateMappingComparer.Instance);

    private static bool EquivalentExclusions(
        IReadOnlyList<BusinessOntologyCandidateBundleExclusion> left,
        IReadOnlyList<BusinessOntologyCandidateBundleExclusion> right) =>
        left.OrderBy(item => item.RunId, StringComparer.Ordinal).ThenBy(item => item.RecordId, StringComparer.Ordinal)
            .SequenceEqual(
                right.OrderBy(item => item.RunId, StringComparer.Ordinal).ThenBy(item => item.RecordId, StringComparer.Ordinal));

    private static bool EquivalentFiles(
        IReadOnlyDictionary<string, XDocument> left,
        IReadOnlyDictionary<string, XDocument> right)
    {
        if (left.Count != right.Count) return false;
        foreach (var (path, document) in left)
        {
            if (!right.TryGetValue(path, out var expected) || !XNode.DeepEquals(document, expected)) return false;
        }
        return true;
    }

    private sealed class CandidateMappingComparer : IEqualityComparer<BusinessOntologyCandidateBundleMapping>
    {
        public static CandidateMappingComparer Instance { get; } = new();

        public bool Equals(BusinessOntologyCandidateBundleMapping? left, BusinessOntologyCandidateBundleMapping? right) =>
            left is not null && right is not null
            && StringComparer.Ordinal.Equals(left.RunId, right.RunId)
            && StringComparer.Ordinal.Equals(left.RecordId, right.RecordId)
            && StringComparer.Ordinal.Equals(left.CandidateXmlId, right.CandidateXmlId)
            && left.Decision.Mappable == right.Decision.Mappable
            && StringComparer.Ordinal.Equals(left.Decision.XmlKind, right.Decision.XmlKind)
            && StringComparer.Ordinal.Equals(left.Decision.SemanticId, right.Decision.SemanticId)
            && StringComparer.Ordinal.Equals(left.Decision.XmlStatus, right.Decision.XmlStatus)
            && StringComparer.Ordinal.Equals(left.Decision.CanonicalSemanticJson, right.Decision.CanonicalSemanticJson)
            && left.Decision.EvidenceIds.SequenceEqual(right.Decision.EvidenceIds, StringComparer.Ordinal)
            && left.Decision.RejectReasons.SequenceEqual(right.Decision.RejectReasons, StringComparer.Ordinal)
            && left.Decision.DowngradeReasons.SequenceEqual(right.Decision.DowngradeReasons, StringComparer.Ordinal);

        public int GetHashCode(BusinessOntologyCandidateBundleMapping item) =>
            HashCode.Combine(item.RunId, item.RecordId, item.CandidateXmlId, item.Decision.CanonicalSemanticJson);
    }

    private static async Task<BusinessOntologyCandidateBundleValidationResult> ValidateAsync(
        string bunExecutable,
        string validatorScript,
        string ontologyPath,
        string bundleWorkspaceRoot,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(bunExecutable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = bundleWorkspaceRoot,
        };
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add(validatorScript);
        startInfo.ArgumentList.Add(ontologyPath);
        startInfo.ArgumentList.Add("--workspace-root");
        startInfo.ArgumentList.Add(bundleWorkspaceRoot);
        startInfo.ArgumentList.Add("--generated");
        startInfo.ArgumentList.Add("--hypothesis-only");

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start ontology XML DSL validator.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = new CancellationTokenSource(ValidatorTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"Ontology XML DSL validator did not finish within {ValidatorTimeout.TotalSeconds:0} seconds.");
        }

        return new BusinessOntologyCandidateBundleValidationResult(
            process.ExitCode == 0,
            process.ExitCode,
            await stdoutTask,
            await stderrTask);
    }

    private static string RequireDirectory(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("A directory is required.", parameterName);
        return Path.GetFullPath(value);
    }

    private static string RequireExistingFile(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("A file is required.", parameterName);
        var path = Path.GetFullPath(value);
        if (!File.Exists(path)) throw new ArgumentException("File does not exist.", parameterName);
        return path;
    }

    private static string RequireTrustedValidatorScript(string value)
    {
        var path = RequireExistingFile(value, nameof(value));
        var scriptDirectory = Path.GetDirectoryName(path);
        var skillDirectory = scriptDirectory is null ? null : Directory.GetParent(scriptDirectory)?.FullName;
        if (scriptDirectory is null || skillDirectory is null ||
            !StringComparer.Ordinal.Equals(Path.GetFileName(path), "validate-ontology-xml.ts") ||
            !StringComparer.Ordinal.Equals(Path.GetFileName(scriptDirectory), "scripts") ||
            !StringComparer.Ordinal.Equals(Path.GetFileName(skillDirectory), "ontology-xml-dsl") ||
            !File.Exists(Path.Combine(skillDirectory, "package.json")))
        {
            throw new ArgumentException(
                "ValidatorScriptPath must be the canonical ontology-xml-dsl validation script.",
                nameof(value));
        }
        return path;
    }

    private static string RequireBundleName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "." or ".." || value.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
        {
            throw new ArgumentException("BundleName must be a single non-empty directory name.", nameof(value));
        }
        return value;
    }
}

using System.Xml.Linq;

namespace Cozo.DotNet.LlmWiki.Tools;

/// <summary>
/// Writes review-only diagnosis artifacts next to an already validated candidate hypothesis
/// bundle. This type reads the isolated analysis workspace and never writes accepted ontology,
/// review, or materialization relations.
/// </summary>
public sealed record BusinessOntologyCandidateArtifactWriteRequest(
    BusinessOntologyCandidateBundleRequest BundleRequest,
    string PublishedBundleDirectory,
    string CreatedAt,
    IReadOnlyDictionary<string, string> SuggestedDecisions,
    string ArtifactDirectoryName = "analysis");

public sealed record BusinessOntologyCandidateArtifactWriteResult(
    BusinessOntologyCandidateDiagnosisDocument Diagnosis,
    BusinessOntologyAdvisoryReviewPacket ReviewPacket,
    string ArtifactDirectory);

public sealed class BusinessOntologyCandidateArtifactWriter(BusinessOntologyAnalysisStore analysisStore)
{
    private const string MissingPublishedDeclaration = "candidate_xml_not_declared_in_published_bundle";
    // Internal test seam only. A future shared export operation owns the publish-then-write
    // composition; callers cannot attach a review packet to an arbitrary XML directory.
    internal async Task<BusinessOntologyCandidateArtifactWriteResult> WriteAsync(
        BusinessOntologyCandidateBundleBuildResult build,
        BusinessOntologyCandidateArtifactWriteRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(build);
        ArgumentNullException.ThrowIfNull(request);
        if (!StringComparer.Ordinal.Equals(build.OntologyId, request.BundleRequest.OntologyId)
            || !StringComparer.Ordinal.Equals(build.GenerationId, request.BundleRequest.GenerationId))
        {
            throw new ArgumentException("Build and artifact request must identify the same analysis workspace.", nameof(request));
        }

        var bundleDirectory = RequirePublishedBundle(request.PublishedBundleDirectory);
        var declaredCandidateIds = ReadCandidateIds(bundleDirectory);
        var artifactName = RequireDirectoryName(request.ArtifactDirectoryName);
        var destination = Path.Combine(bundleDirectory, artifactName);
        if (Directory.Exists(destination) || File.Exists(destination))
        {
            throw new IOException($"Candidate artifact destination must be new: {destination}");
        }

        var workspace = await analysisStore.ReadWorkspaceAsync(
            request.BundleRequest.OntologyId,
            request.BundleRequest.GenerationId,
            cancellationToken);
        if (!string.IsNullOrWhiteSpace(request.BundleRequest.AnalysisRunId) &&
            !workspace.Runs.Any(run => StringComparer.Ordinal.Equals(run.RunId, request.BundleRequest.AnalysisRunId)))
        {
            throw new ArgumentException("Artifact request references an analysis run outside the selected workspace.", nameof(request));
        }
        var selectedRuns = string.IsNullOrWhiteSpace(request.BundleRequest.AnalysisRunId)
            ? workspace.Runs
            : workspace.Runs.Where(run => StringComparer.Ordinal.Equals(run.RunId, request.BundleRequest.AnalysisRunId)).ToArray();
        var selectedRecords = string.IsNullOrWhiteSpace(request.BundleRequest.AnalysisRunId)
            ? workspace.Records
            : workspace.Records.Where(record => StringComparer.Ordinal.Equals(record.RunId, request.BundleRequest.AnalysisRunId)).ToArray();
        var mappingValidator = new OntologyCandidateDraftXmlMappingValidator();
        var availableEvidence = request.BundleRequest.EvidenceFacts
            .Select(fact => new OntologyCandidateDraftEvidence(fact.Id, fact.Grade))
            .ToArray();
        var publishedByRecord = build.Mappings
            .Where(mapping => declaredCandidateIds.Contains(mapping.CandidateXmlId))
            .ToDictionary(mapping => Key(mapping.RunId, mapping.RecordId), StringComparer.Ordinal);
        var missingPublishedByRecord = build.Mappings
            .Where(mapping => !declaredCandidateIds.Contains(mapping.CandidateXmlId))
            .GroupBy(mapping => Key(mapping.RunId, mapping.RecordId), StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(_ => MissingPublishedDeclaration).ToArray(),
                StringComparer.Ordinal);
        var exclusionsByRecord = build.Exclusions
            .GroupBy(exclusion => Key(exclusion.RunId, exclusion.RecordId), StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(item => item.Reason).OrderBy(item => item, StringComparer.Ordinal).ToArray(),
                StringComparer.Ordinal);

        var mappings = selectedRecords
            .OrderBy(record => record.CreatedAt, StringComparer.Ordinal)
            .ThenBy(record => record.RunId, StringComparer.Ordinal)
            .ThenBy(record => record.RecordId, StringComparer.Ordinal)
            .Select(record =>
            {
                var decision = mappingValidator.Evaluate(
                    record,
                    request.BundleRequest.KnownConceptIds,
                    availableEvidence);
                if (publishedByRecord.TryGetValue(Key(record.RunId, record.RecordId), out var published))
                {
                    return BusinessOntologyCandidateMappingRecord.From(record, published.CandidateXmlId, decision);
                }
                return BusinessOntologyCandidateMappingRecord.From(
                    record,
                    null,
                    decision,
                    exclusionsByRecord.TryGetValue(Key(record.RunId, record.RecordId), out var reasons)
                        ? reasons
                        : missingPublishedByRecord.TryGetValue(Key(record.RunId, record.RecordId), out var missingReasons)
                            ? missingReasons
                            : null);
            })
            .ToArray();
        var diagnosis = BusinessOntologyCandidateDiagnosisDocument.Create(
            request.BundleRequest.OntologyId,
            request.BundleRequest.GenerationId,
            request.CreatedAt,
            selectedRuns,
            selectedRecords,
            mappings);
        var reviewPacket = BusinessOntologyAdvisoryReviewPacket.Create(
            request.BundleRequest.OntologyId,
            request.BundleRequest.GenerationId,
            request.CreatedAt,
            mappings,
            request.SuggestedDecisions);

        var staging = Path.Combine(bundleDirectory, ".staging-" + artifactName + "-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(staging);
            await WriteFileAsync(staging, "diagnosis.json", diagnosis.ToCanonicalJson(), cancellationToken);
            await WriteFileAsync(staging, "diagnosis.md", diagnosis.ToMarkdown(), cancellationToken);
            await WriteFileAsync(staging, "advisory-review.json", reviewPacket.ToCanonicalJson(), cancellationToken);
            await WriteFileAsync(staging, "advisory-review.md", reviewPacket.ToMarkdown(), cancellationToken);
            Directory.Move(staging, destination);
            return new BusinessOntologyCandidateArtifactWriteResult(diagnosis, reviewPacket, destination);
        }
        catch
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); } catch { }
            throw;
        }
    }

    private static async Task WriteFileAsync(string directory, string name, string content, CancellationToken cancellationToken) =>
        await File.WriteAllTextAsync(Path.Combine(directory, name), content + Environment.NewLine, cancellationToken);

    private static string RequirePublishedBundle(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("PublishedBundleDirectory is required.", nameof(value));
        var full = Path.GetFullPath(value);
        if (!Directory.Exists(full))
        {
            throw new ArgumentException("PublishedBundleDirectory must be an existing validated candidate bundle.", nameof(value));
        }
        RequireRegularBundleFile(full, Path.Combine(full, "ontology.xml"));
        return full;
    }

    private static string RequireDirectoryName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "." or ".." || value.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
        {
            throw new ArgumentException("ArtifactDirectoryName must be a single non-empty directory name.", nameof(value));
        }
        return value;
    }

    private static HashSet<string> ReadCandidateIds(string bundleDirectory)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var rootPath = Path.Combine(bundleDirectory, "ontology.xml");
        var root = XDocument.Load(rootPath, System.Xml.Linq.LoadOptions.None);
        var moduleReferences = root.Root?.Element("Modules")?.Elements()
            .Select(element => element.Attribute("href")?.Value)
            .Where(href => !string.IsNullOrWhiteSpace(href))
            .Cast<string>()
            .ToArray() ?? [];
        foreach (var href in moduleReferences)
        {
            const string prefix = "vfs://./";
            if (!href.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var path = Path.GetFullPath(Path.Combine(bundleDirectory, href[prefix.Length..].Replace('/', Path.DirectorySeparatorChar)));
            RequireRegularBundleFile(bundleDirectory, path);
            var document = System.Xml.Linq.XDocument.Load(path, System.Xml.Linq.LoadOptions.None);
            foreach (var type in document.Descendants("Type"))
            {
                if (!StringComparer.Ordinal.Equals(type.Attribute("status")?.Value, "hypothesis")) continue;
                var typeId = type.Attribute("id")?.Value;
                if (string.IsNullOrWhiteSpace(typeId)) continue;
                ids.Add(typeId);
                foreach (var attribute in type.Descendants("Attribute"))
                {
                    if (!StringComparer.Ordinal.Equals(attribute.Attribute("status")?.Value, "hypothesis")) continue;
                    var name = attribute.Attribute("name")?.Value;
                    if (!string.IsNullOrWhiteSpace(name)) ids.Add(typeId + "." + name);
                }
            }
            foreach (var elementName in new[] { "Relation", "Rule", "StateMachine" })
            {
                foreach (var element in document.Descendants(elementName))
                {
                    if (!StringComparer.Ordinal.Equals(element.Attribute("status")?.Value, "hypothesis")) continue;
                    var id = element.Attribute("id")?.Value;
                    if (!string.IsNullOrWhiteSpace(id)) ids.Add(id);
                }
            }
        }
        return ids;
    }

    private static void RequireRegularBundleFile(string bundleDirectory, string candidate)
    {
        if (!IsWithin(bundleDirectory, candidate))
        {
            throw new ArgumentException("Published candidate bundle references a file outside its bundle directory.");
        }

        var relative = Path.GetRelativePath(bundleDirectory, candidate);
        var current = bundleDirectory;
        if (new DirectoryInfo(current).LinkTarget is not null)
        {
            throw new ArgumentException("Published candidate bundle must not be a symbolic link.");
        }
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            FileSystemInfo info = StringComparer.Ordinal.Equals(current, candidate)
                ? new FileInfo(current)
                : new DirectoryInfo(current);
            if (info.LinkTarget is not null)
            {
                throw new ArgumentException("Published candidate bundle must not traverse symbolic links.");
            }
        }
        if (!File.Exists(candidate))
        {
            throw new ArgumentException("Published candidate bundle references a missing module.");
        }
    }

    private static bool IsWithin(string root, string target)
    {
        var relative = Path.GetRelativePath(root, target);
        return relative == "." || (!relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && relative != ".." && !Path.IsPathRooted(relative));
    }

    private static string Key(string runId, string recordId) => runId + "\u001f" + recordId;
}

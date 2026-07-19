using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cozo.DotNet.LlmWiki.LlmClient;
using Cozo.DotNet.Om;

namespace Cozo.DotNet.LlmWiki.Tools;

public sealed record BusinessOntologySemanticDeriveCliRequest(
    string OntologyId,
    string GenerationId,
    string SourceFingerprint,
    string RepositoryPath,
    string Mode,
    string? CorroborationDatabasePath,
    string? CorroborationRepositoryPath,
    string? CreatedAt,
    string GeneratorVersion,
    string? Experiment = null,
    int? MaxSlices = null,
    string? ExperimentOutputDirectory = null);

public sealed record BusinessOntologyReviewApplyCliRequest(
    string OntologyId,
    string DecisionsFilePath);

public sealed record BusinessOntologyPurgeCliRequest(
    string OntologyId,
    string GenerationId,
    string Reason);

public sealed record BusinessOntologyReviewPromotionSummary(
    string OntologyId,
    string GenerationId,
    int Decisions,
    int AppendedReviews,
    int ReplayedReviews,
    int Relations,
    int Rules,
    int Lifecycles,
    int States,
    int Transitions,
    int StaleReviews);

/// <summary>
/// Testable contract and orchestration for semantic ontology CLI commands.
/// Database selection and process output remain in the executable wiring.
/// </summary>
public sealed class BusinessOntologySemanticCli
{
    private static readonly Regex OntologyFqn = new(
        "^[A-Z][A-Za-z0-9]*(?:\\.[A-Z][A-Za-z0-9]*)+$",
        RegexOptions.CultureInvariant);

    private readonly CozoOm om;
    private readonly BusinessOntologyStore store;
    private readonly ILlmClient? llmClient;
    private readonly TimeProvider timeProvider;

    public BusinessOntologySemanticCli(
        CozoOm om,
        BusinessOntologyStore store,
        ILlmClient? llmClient = null,
        TimeProvider? timeProvider = null)
    {
        this.om = om ?? throw new ArgumentNullException(nameof(om));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.llmClient = llmClient;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public static BusinessOntologySemanticDeriveCliRequest ParseDeriveSemantics(
        IReadOnlyDictionary<string, string> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var ontologyId = Required(options, "--ontology-id");
        ValidateOntologyId(ontologyId);
        var generationId = Required(options, "--generation-id");
        var sourceFingerprint = Required(options, "--source-fingerprint");
        var repositoryPath = FullPath(Required(options, "--repo"));
        if (!Directory.Exists(repositoryPath))
        {
            throw new ArgumentException($"Repository directory does not exist: {repositoryPath}");
        }

        var mode = Required(options, "--mode").Trim().ToLowerInvariant();
        if (!BusinessOntologySemanticProjectionModes.All.Contains(mode))
        {
            throw new ArgumentException(
                $"--mode must be '{BusinessOntologySemanticProjectionModes.Deterministic}' "
                + $"or '{BusinessOntologySemanticProjectionModes.Assisted}'.");
        }
        var experiment = OptionalValue(options, "--experiment");
        var maxSlicesText = OptionalValue(options, "--max-slices");
        int? maxSlices = maxSlicesText is null
            ? null
            : int.TryParse(maxSlicesText, CultureInfo.InvariantCulture, out var parsedMaxSlices)
                ? parsedMaxSlices
                : throw new ArgumentException("--max-slices must be a positive integer.");
        var experimentOutputDirectory = OptionalValue(options, "--experiment-out");
        if (experiment is null)
        {
            if (maxSlices is not null || experimentOutputDirectory is not null)
            {
                throw new ArgumentException("--max-slices and --experiment-out require --experiment v2|v3.");
            }
        }
        else
        {
            if (mode != BusinessOntologySemanticProjectionModes.Assisted)
            {
                throw new ArgumentException("--experiment v2|v3 requires --mode assisted.");
            }
            var profile = OntologySemanticExperimentProfiles.Resolve(experiment);
            _ = OntologySemanticExperimentBudget.Create(profile, maxSlices);
            if (experimentOutputDirectory is null)
            {
                throw new ArgumentException("--experiment v2|v3 requires --experiment-out <empty-directory>.");
            }
            experimentOutputDirectory = FullPath(experimentOutputDirectory);
            if (Directory.Exists(experimentOutputDirectory)
                && Directory.EnumerateFileSystemEntries(experimentOutputDirectory).Any())
            {
                throw new ArgumentException("--experiment-out must be an empty directory or a new path.");
            }
        }

        var corroborationDatabasePath = OptionalPath(options, "--corroboration-db");
        var corroborationRepositoryPath = OptionalPath(options, "--corroboration-repo");
        if ((corroborationDatabasePath is null) != (corroborationRepositoryPath is null))
        {
            throw new ArgumentException(
                "--corroboration-db and --corroboration-repo must be supplied together.");
        }
        if (corroborationDatabasePath is not null && !File.Exists(corroborationDatabasePath))
        {
            throw new ArgumentException(
                $"Corroboration database does not exist: {corroborationDatabasePath}");
        }
        if (corroborationRepositoryPath is not null && !Directory.Exists(corroborationRepositoryPath))
        {
            throw new ArgumentException(
                $"Corroboration repository directory does not exist: {corroborationRepositoryPath}");
        }

        var createdAt = options.GetValueOrDefault("--created-at");
        if (!string.IsNullOrWhiteSpace(createdAt)
            && !DateTimeOffset.TryParse(
                createdAt,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out _))
        {
            throw new ArgumentException("--created-at must be an RFC3339 timestamp.");
        }

        return new BusinessOntologySemanticDeriveCliRequest(
            ontologyId,
            generationId,
            sourceFingerprint,
            repositoryPath,
            mode,
            corroborationDatabasePath,
            corroborationRepositoryPath,
            string.IsNullOrWhiteSpace(createdAt) ? null : createdAt,
            options.GetValueOrDefault(
                "--generator-version",
                "onto-semantic-projector/1"),
            experiment,
            maxSlices,
            experimentOutputDirectory);
    }

    public static BusinessOntologyReviewApplyCliRequest ParseReviewApply(
        IReadOnlyDictionary<string, string> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var ontologyId = Required(options, "--ontology-id");
        ValidateOntologyId(ontologyId);
        var decisionsFilePath = FullPath(Required(options, "--decisions"));
        if (!File.Exists(decisionsFilePath))
        {
            throw new ArgumentException($"Decision file does not exist: {decisionsFilePath}");
        }
        return new BusinessOntologyReviewApplyCliRequest(ontologyId, decisionsFilePath);
    }

    public static BusinessOntologyPurgeCliRequest ParsePurge(
        IReadOnlyDictionary<string, string> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var ontologyId = Required(options, "--ontology-id");
        ValidateOntologyId(ontologyId);
        var generationId = Required(options, "--generation-id");
        var reason = Required(options, "--reason");
        return new BusinessOntologyPurgeCliRequest(
            ontologyId,
            generationId,
            reason);
    }

    public async Task<BusinessOntologySemanticProjectionResult> DeriveSemanticsAsync(
        BusinessOntologySemanticDeriveCliRequest request,
        CozoOm? corroborationOm = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Mode == BusinessOntologySemanticProjectionModes.Assisted
            && (llmClient is null || !llmClient.IsAvailable))
        {
            throw new InvalidOperationException(
                "Assisted semantic derivation requires an available LLM client: "
                + (llmClient?.UnavailableReason ?? "no client was configured"));
        }
        if (request.CorroborationDatabasePath is not null && corroborationOm is null)
        {
            throw new ArgumentException(
                "A corroboration database was requested but no corroboration store was supplied.",
                nameof(corroborationOm));
        }

        var corroborations = corroborationOm is null
            ? []
            : await ReadCorroborationsAsync(corroborationOm, cancellationToken);
        var createdAt = request.CreatedAt
            ?? timeProvider.GetUtcNow().ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        return await new BusinessOntologySemanticProjector(
            om,
            store,
            llmClient: request.Mode == BusinessOntologySemanticProjectionModes.Assisted
                ? llmClient
                : null)
            .ProjectAsync(
                new BusinessOntologySemanticProjectionRequest(
                    request.OntologyId,
                    request.GenerationId,
                    request.SourceFingerprint,
                    createdAt,
                    request.GeneratorVersion,
                    corroborations,
                    request.Mode,
                    request.Experiment,
                    request.MaxSlices),
                cancellationToken);
    }

    public async Task<BusinessOntologyReviewPromotionSummary> ApplyReviewAsync(
        BusinessOntologyReviewApplyCliRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var reviewResult = await new BusinessOntologyReviewService(store, timeProvider)
            .ApplyDecisionFileAsync(
                request.DecisionsFilePath,
                request.OntologyId,
                cancellationToken);
        var materialized = await new BusinessOntologyMaterializationService(store)
            .MaterializeAsync(request.OntologyId, cancellationToken);
        return new BusinessOntologyReviewPromotionSummary(
            request.OntologyId,
            materialized.GenerationId,
            reviewResult.Reviews.Count,
            reviewResult.Reviews.Count(item => item.Appended),
            reviewResult.Reviews.Count(item => !item.Appended),
            materialized.Relations,
            materialized.Rules,
            materialized.Lifecycles,
            materialized.States,
            materialized.Transitions,
            materialized.StaleReviews);
    }

    public async Task<BusinessOntologyPurgeSummary> PurgeAsync(
        BusinessOntologyPurgeCliRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await store.PurgeGenerationAsync(
            request.OntologyId,
            request.GenerationId,
            request.Reason,
            cancellationToken);
    }

    private static async Task<IReadOnlyList<BusinessOntologyCorroborationObservation>>
        ReadCorroborationsAsync(
            CozoOm corroborationOm,
            CancellationToken cancellationToken)
    {
        var symbols = await corroborationOm.Runtime.Store.RunAsync(
            """
            ?[symbol_id, repository, path, language, name, kind, start_line, end_line, resolver] :=
              *ck_symbol{
                symbol_id, file_id, name, kind, start_line, end_line, resolver
              },
              *ck_file{file_id, repo_id: repository, path, language}
            """,
            cancellationToken: cancellationToken);
        var observations = symbols.Rows
            .Select(row => new
            {
                Symbol = String(row, 0),
                Repository = String(row, 1),
                Path = String(row, 2).Replace('\\', '/'),
                Language = String(row, 3),
                Name = String(row, 4),
                Kind = String(row, 5),
                StartLine = Math.Max(1, Integer(row, 6)),
                EndLine = Math.Max(Math.Max(1, Integer(row, 6)), Integer(row, 7)),
                Resolver = String(row, 8),
            })
            .Where(item =>
                item.Language is "typescript" or "tsx" or "javascript" or "jsx"
                && (item.Name.EndsWith("Page", StringComparison.Ordinal)
                    || item.Name.EndsWith("Form", StringComparison.Ordinal)))
            .OrderBy(item => item.Repository, StringComparer.Ordinal)
            .ThenBy(item => item.Path, StringComparer.Ordinal)
            .ThenBy(item => item.Symbol, StringComparer.Ordinal)
            .Take(2_000)
            .Select(item =>
            {
                var sourceKind = item.Name.EndsWith("Form", StringComparison.Ordinal)
                    ? BusinessOntologyCorroborationKinds.FrontendForm
                    : BusinessOntologyCorroborationKinds.FrontendPage;
                return new BusinessOntologyCorroborationObservation(
                    StableEvidenceId(
                        item.Repository,
                        item.Path,
                        item.Symbol,
                        item.StartLine,
                        item.EndLine,
                        sourceKind),
                    item.Repository,
                    item.Path,
                    item.Symbol,
                    item.StartLine,
                    item.EndLine,
                    sourceKind,
                    item.Name,
                    "type",
                    item.Symbol,
                    $"前端 {item.Name} 为同名业务概念提供呈现层佐证。",
                    string.IsNullOrWhiteSpace(item.Resolver) ? "codeknowledge" : item.Resolver,
                    0.7);
            })
            .ToArray();
        return observations;
    }

    private static string StableEvidenceId(
        string repository,
        string path,
        string symbol,
        int startLine,
        int endLine,
        string sourceKind)
    {
        var identity = string.Join(
            "\n",
            repository,
            path,
            symbol,
            startLine.ToString(CultureInfo.InvariantCulture),
            endLine.ToString(CultureInfo.InvariantCulture),
            sourceKind);
        return "corroboration:"
            + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))
                .ToLowerInvariant();
    }

    private static string Required(
        IReadOnlyDictionary<string, string> options,
        string name)
    {
        var value = options.GetValueOrDefault(name);
        if (string.IsNullOrWhiteSpace(value) || value == "true")
        {
            throw new ArgumentException($"{name} requires a value.");
        }
        return value.Trim();
    }

    private static string? OptionalPath(
        IReadOnlyDictionary<string, string> options,
        string name)
    {
        var value = options.GetValueOrDefault(name);
        return string.IsNullOrWhiteSpace(value) || value == "true"
            ? null
            : FullPath(value);
    }

    private static string? OptionalValue(
        IReadOnlyDictionary<string, string> options,
        string name)
    {
        var value = options.GetValueOrDefault(name);
        return string.IsNullOrWhiteSpace(value) || value == "true" ? null : value.Trim();
    }

    private static string FullPath(string value) =>
        Path.GetFullPath(
            Environment.ExpandEnvironmentVariables(value.Replace(
                "~",
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                StringComparison.Ordinal)));

    private static void ValidateOntologyId(string ontologyId)
    {
        if (!OntologyFqn.IsMatch(ontologyId))
        {
            throw new ArgumentException(
                "--ontology-id must be a PascalCase dotted FQN.");
        }
    }

    private static string String(IReadOnlyList<JsonElement> row, int index) =>
        row[index].ValueKind == JsonValueKind.String
            ? row[index].GetString() ?? ""
            : row[index].ToString();

    private static int Integer(IReadOnlyList<JsonElement> row, int index) =>
        row[index].ValueKind == JsonValueKind.Number
            ? row[index].GetInt32()
            : 0;
}

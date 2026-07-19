using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Cozo.DotNet.LlmWiki.Tools;

public static class BusinessOntologyReviewSuggestions
{
    public const string Accept = "accept";
    public const string Reject = "reject";
    public const string Defer = "defer";
    public const string RequestEvidence = "request_evidence";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        Accept,
        Reject,
        Defer,
        RequestEvidence,
    };
}

public sealed record BusinessOntologyCandidateMappingRecord(
    string RunId,
    string RecordId,
    string Kind,
    string SubjectKind,
    string SubjectId,
    string Title,
    string QueryDigest,
    IReadOnlyList<string> EvidenceIds,
    string? CandidateXmlId,
    BusinessOntologyCandidateMappingDecisionSummary MappingDecision)
{
    public static BusinessOntologyCandidateMappingRecord From(
        BusinessOntologyAnalysisRecord record,
        string? candidateXmlId,
        OntologyCandidateDraftXmlMappingDecision decision,
        IEnumerable<string>? exportSuppressionReasons = null)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(decision);
        var suppressionReasons = (exportSuppressionReasons ?? [])
            .Where(reason => !string.IsNullOrWhiteSpace(reason))
            .Select(reason => reason.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(reason => reason, StringComparer.Ordinal)
            .ToArray();
        if (decision.Mappable && string.IsNullOrWhiteSpace(candidateXmlId) && suppressionReasons.Length == 0)
        {
            throw new ArgumentException("Mappable candidate decisions require a candidate XML id unless export suppression is recorded.", nameof(candidateXmlId));
        }
        if (!string.IsNullOrWhiteSpace(candidateXmlId) && suppressionReasons.Length != 0)
        {
            throw new ArgumentException("Published candidate XML ids cannot also be export-suppressed.", nameof(exportSuppressionReasons));
        }

        return new BusinessOntologyCandidateMappingRecord(
            record.RunId,
            record.RecordId,
            record.Kind,
            record.SubjectKind,
            record.SubjectId,
            record.Title,
            record.QueryDigest,
            record.EvidenceIds.OrderBy(id => id, StringComparer.Ordinal).ToArray(),
            string.IsNullOrWhiteSpace(candidateXmlId) ? null : candidateXmlId.Trim(),
            BusinessOntologyCandidateMappingDecisionSummary.From(decision, suppressionReasons));
    }
}

public sealed record BusinessOntologyCandidateMappingDecisionSummary(
    bool Mappable,
    string? XmlKind,
    string? SemanticId,
    string XmlStatus,
    IReadOnlyList<string> EvidenceIds,
    IReadOnlyList<string> RejectReasons,
    IReadOnlyList<string> DowngradeReasons,
    IReadOnlyList<string> ExportSuppressionReasons)
{
    public static BusinessOntologyCandidateMappingDecisionSummary From(
        OntologyCandidateDraftXmlMappingDecision decision,
        IEnumerable<string>? exportSuppressionReasons = null)
    {
        ArgumentNullException.ThrowIfNull(decision);
        return new BusinessOntologyCandidateMappingDecisionSummary(
            decision.Mappable,
            decision.XmlKind,
            decision.SemanticId,
            decision.XmlStatus,
            decision.EvidenceIds.OrderBy(id => id, StringComparer.Ordinal).ToArray(),
            decision.RejectReasons.OrderBy(id => id, StringComparer.Ordinal).ToArray(),
            decision.DowngradeReasons.OrderBy(id => id, StringComparer.Ordinal).ToArray(),
            (exportSuppressionReasons ?? [])
                .Where(reason => !string.IsNullOrWhiteSpace(reason))
                .Select(reason => reason.Trim())
                .Distinct(StringComparer.Ordinal)
                .OrderBy(reason => reason, StringComparer.Ordinal)
                .ToArray());
    }
}

public sealed record BusinessOntologyCandidateDiagnosisDocument(
    string SchemaVersion,
    string OntologyId,
    string GenerationId,
    string CreatedAt,
    IReadOnlyList<BusinessOntologyCandidateDiagnosisRunRef> Runs,
    IReadOnlyList<BusinessOntologyCandidateDiagnosisItem> Items)
{
    public const string SchemaVersionValue = "business-ontology-candidate-diagnosis-v1";

    public static BusinessOntologyCandidateDiagnosisDocument Create(
        string ontologyId,
        string generationId,
        string createdAt,
        IReadOnlyList<BusinessOntologyAnalysisRun> runs,
        IReadOnlyList<BusinessOntologyAnalysisRecord> records,
        IReadOnlyList<BusinessOntologyCandidateMappingRecord> mappings)
    {
        Require(ontologyId, nameof(ontologyId));
        Require(generationId, nameof(generationId));
        RequireTimestamp(createdAt, nameof(createdAt));
        ArgumentNullException.ThrowIfNull(runs);
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(mappings);

        var runsById = runs.ToDictionary(run => run.RunId, StringComparer.Ordinal);
        var recordsByKey = records.ToDictionary(
            record => Key(record.RunId, record.RecordId),
            StringComparer.Ordinal);
        var items = new List<BusinessOntologyCandidateDiagnosisItem>();
        var mappedRecordKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mapping in mappings)
        {
            if (!runsById.ContainsKey(mapping.RunId))
            {
                throw new ArgumentException($"Unknown analysis run '{mapping.RunId}'.", nameof(mappings));
            }
            if (!recordsByKey.TryGetValue(Key(mapping.RunId, mapping.RecordId), out var record))
            {
                throw new ArgumentException(
                    $"Unknown analysis record '{mapping.RunId}/{mapping.RecordId}'.",
                    nameof(mappings));
            }
            if (!mappedRecordKeys.Add(Key(mapping.RunId, mapping.RecordId)))
            {
                throw new ArgumentException(
                    $"Duplicate diagnosis mapping for analysis record '{mapping.RunId}/{mapping.RecordId}'.",
                    nameof(mappings));
            }
            items.Add(BusinessOntologyCandidateDiagnosisItem.From(record, mapping));
        }
        if (!recordsByKey.Keys.All(mappedRecordKeys.Contains))
        {
            throw new ArgumentException(
                "Diagnosis requires exactly one mapping for every analysis record.",
                nameof(mappings));
        }

        return new BusinessOntologyCandidateDiagnosisDocument(
            SchemaVersionValue,
            ontologyId.Trim(),
            generationId.Trim(),
            createdAt.Trim(),
            runs
                .OrderBy(run => run.StartedAt, StringComparer.Ordinal)
                .ThenBy(run => run.RunId, StringComparer.Ordinal)
                .Select(BusinessOntologyCandidateDiagnosisRunRef.From)
                .ToArray(),
            items.ToArray());
    }

    public string ToCanonicalJson() => CandidateArtifactJson.Canonicalize(this);

    public string ToMarkdown()
    {
        var text = new StringBuilder();
        text.Append("# Candidate Diagnosis").AppendLine().AppendLine();
        text.Append("- Schema: `").Append(SchemaVersion).AppendLine("`");
        text.Append("- Ontology: `").Append(OntologyId).AppendLine("`");
        text.Append("- Generation: `").Append(GenerationId).AppendLine("`");
        text.Append("- Created: `").Append(CreatedAt).AppendLine("`").AppendLine();
        text.AppendLine("| record | kind | candidateXmlId | decision | query | evidence |");
        text.AppendLine("| --- | --- | --- | --- | --- | --- |");
        foreach (var item in Items)
        {
            text.Append("| `").Append(EscapeCell(item.RecordId))
                .Append("` | `").Append(EscapeCell(item.Kind))
                .Append("` | ");
            if (!string.IsNullOrWhiteSpace(item.CandidateXmlId))
            {
                text.Append('`').Append(EscapeCell(item.CandidateXmlId)).Append('`');
            }
            text.Append(" | `").Append(EscapeCell(DecisionLabel(item.MappingDecision)))
                .Append("` | `").Append(EscapeCell(item.QueryDigest))
                .Append("` | `").Append(EscapeCell(string.Join(", ", item.EvidenceIds)))
                .AppendLine("` |");
        }
        return text.ToString();
    }

    private static string DecisionLabel(BusinessOntologyCandidateMappingDecisionSummary decision) =>
        decision.ExportSuppressionReasons.Count != 0
            ? string.Join(",", decision.ExportSuppressionReasons)
            : decision.Mappable
                ? "mappable"
                : string.Join(",", decision.RejectReasons);

    private static string Key(string runId, string recordId) => runId + "\u001f" + recordId;

    private static string EscapeCell(string? value) =>
        (value ?? "")
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Replace("|", "\\|", StringComparison.Ordinal);

    private static void Require(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"{name} is required.", name);
    }

    private static void RequireTimestamp(string value, string name)
    {
        Require(value, name);
        if (!DateTimeOffset.TryParse(value.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _))
        {
            throw new ArgumentException($"{name} must be an ISO-8601 timestamp.", name);
        }
    }
}

public sealed record BusinessOntologyCandidateDiagnosisRunRef(
    string RunId,
    string AgentId,
    string Model,
    string Purpose,
    string Status,
    string StartedAt,
    string CompletedAt,
    string InputDigest)
{
    public static BusinessOntologyCandidateDiagnosisRunRef From(BusinessOntologyAnalysisRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return new BusinessOntologyCandidateDiagnosisRunRef(
            run.RunId,
            run.AgentId,
            run.Model,
            run.Purpose,
            run.Status,
            run.StartedAt,
            run.CompletedAt,
            run.InputDigest);
    }
}

public sealed record BusinessOntologyCandidateDiagnosisItem(
    string RunId,
    string RecordId,
    string Kind,
    string SubjectKind,
    string SubjectId,
    string Title,
    string Status,
    double Uncertainty,
    string QueryDigest,
    string CreatedAt,
    IReadOnlyList<string> EvidenceIds,
    string? CandidateXmlId,
    BusinessOntologyCandidateMappingDecisionSummary MappingDecision)
{
    public static BusinessOntologyCandidateDiagnosisItem From(
        BusinessOntologyAnalysisRecord record,
        BusinessOntologyCandidateMappingRecord mapping)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(mapping);
        return new BusinessOntologyCandidateDiagnosisItem(
            record.RunId,
            record.RecordId,
            record.Kind,
            record.SubjectKind,
            record.SubjectId,
            record.Title,
            record.Status,
            record.Uncertainty,
            record.QueryDigest,
            record.CreatedAt,
            record.EvidenceIds.OrderBy(id => id, StringComparer.Ordinal).ToArray(),
            mapping.CandidateXmlId,
            mapping.MappingDecision);
    }
}

public sealed record BusinessOntologyAdvisoryReviewPacket(
    string SchemaVersion,
    string OntologyId,
    string GenerationId,
    string CreatedAt,
    IReadOnlyList<BusinessOntologyAdvisoryReviewItem> Items)
{
    public const string SchemaVersionValue = "business-ontology-advisory-review-packet-v1";

    public static BusinessOntologyAdvisoryReviewPacket Create(
        string ontologyId,
        string generationId,
        string createdAt,
        IReadOnlyList<BusinessOntologyCandidateMappingRecord> mappings,
        IReadOnlyDictionary<string, string> suggestedDecisions)
    {
        Require(ontologyId, nameof(ontologyId));
        Require(generationId, nameof(generationId));
        RequireTimestamp(createdAt, nameof(createdAt));
        ArgumentNullException.ThrowIfNull(mappings);
        ArgumentNullException.ThrowIfNull(suggestedDecisions);

        var items = new List<BusinessOntologyAdvisoryReviewItem>();
        foreach (var mapping in mappings)
        {
            if (!mapping.MappingDecision.Mappable || string.IsNullOrWhiteSpace(mapping.CandidateXmlId)) continue;
            if (string.IsNullOrWhiteSpace(mapping.CandidateXmlId))
            {
                throw new ArgumentException("Mappable review packet records require candidate XML ids.", nameof(mappings));
            }

            var suggestedDecision = suggestedDecisions.TryGetValue(mapping.CandidateXmlId, out var requested)
                ? requested
                : BusinessOntologyReviewSuggestions.Defer;
            if (!BusinessOntologyReviewSuggestions.All.Contains(suggestedDecision))
            {
                throw new ArgumentException(
                    "Review packet suggestions must be accept, reject, defer, or request_evidence.",
                    nameof(suggestedDecisions));
            }

            items.Add(BusinessOntologyAdvisoryReviewItem.From(mapping, suggestedDecision));
        }

        return new BusinessOntologyAdvisoryReviewPacket(
            SchemaVersionValue,
            ontologyId.Trim(),
            generationId.Trim(),
            createdAt.Trim(),
            items
                .OrderBy(item => item.CandidateXmlId, StringComparer.Ordinal)
                .ToArray());
    }

    public string ToCanonicalJson() => CandidateArtifactJson.Canonicalize(this);

    public string ToMarkdown()
    {
        var text = new StringBuilder();
        text.Append("# Advisory Review Packet").AppendLine().AppendLine();
        text.Append("- Schema: `").Append(SchemaVersion).AppendLine("`");
        text.Append("- Ontology: `").Append(OntologyId).AppendLine("`");
        text.Append("- Generation: `").Append(GenerationId).AppendLine("`");
        text.Append("- Created: `").Append(CreatedAt).AppendLine("`").AppendLine();
        text.AppendLine("| candidateXmlId | xmlKind | semanticId | suggestedDecision | record | query | evidence |");
        text.AppendLine("| --- | --- | --- | --- | --- | --- | --- |");
        foreach (var item in Items)
        {
            text.Append("| `").Append(EscapeCell(item.CandidateXmlId))
                .Append("` | `").Append(EscapeCell(item.MappingDecision.XmlKind))
                .Append("` | `").Append(EscapeCell(item.MappingDecision.SemanticId))
                .Append("` | `").Append(EscapeCell(item.SuggestedDecision))
                .Append("` | `").Append(EscapeCell(item.RecordId))
                .Append("` | `").Append(EscapeCell(item.QueryDigest))
                .Append("` | `").Append(EscapeCell(string.Join(", ", item.EvidenceIds)))
                .AppendLine("` |");
        }
        return text.ToString();
    }

    private static string EscapeCell(string? value) =>
        (value ?? "")
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Replace("|", "\\|", StringComparison.Ordinal);

    private static void Require(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"{name} is required.", name);
    }

    private static void RequireTimestamp(string value, string name)
    {
        Require(value, name);
        if (!DateTimeOffset.TryParse(value.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _))
        {
            throw new ArgumentException($"{name} must be an ISO-8601 timestamp.", name);
        }
    }
}

public sealed record BusinessOntologyAdvisoryReviewItem(
    string CandidateXmlId,
    string SuggestedDecision,
    string RunId,
    string RecordId,
    string SubjectKind,
    string SubjectId,
    string QueryDigest,
    IReadOnlyList<string> EvidenceIds,
    BusinessOntologyCandidateMappingDecisionSummary MappingDecision)
{
    public static BusinessOntologyAdvisoryReviewItem From(
        BusinessOntologyCandidateMappingRecord mapping,
        string suggestedDecision)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        return new BusinessOntologyAdvisoryReviewItem(
            mapping.CandidateXmlId ?? throw new ArgumentException("candidate XML id is required.", nameof(mapping)),
            suggestedDecision,
            mapping.RunId,
            mapping.RecordId,
            mapping.SubjectKind,
            mapping.SubjectId,
            mapping.QueryDigest,
            mapping.EvidenceIds.OrderBy(id => id, StringComparer.Ordinal).ToArray(),
            mapping.MappingDecision);
    }
}

internal static class CandidateArtifactJson
{
    public static string Canonicalize<T>(T value)
    {
        var json = JsonSerializer.Serialize(value, LlmWikiJson.Options);
        return OntologySemanticJson.Canonicalize(json);
    }
}

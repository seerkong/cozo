using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cozo.DotNet.Om.CodeKnowledge;

namespace Cozo.DotNet.LlmWiki.Tools;

internal static class BusinessOntologyAgentActionContract
{
    public const string SchemaVersion = "business-ontology-agent-action-v1";

    public static JsonDocument SchemaJson() => JsonDocument.Parse(
        """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "oneOf": [
            {
              "type": "object",
              "additionalProperties": false,
              "properties": {
                "schemaVersion": {"const": "business-ontology-agent-action-v1"},
                "action": {"const": "query"},
                "identity": {"$ref": "#/$defs/identity"},
                "operation": {
                  "enum": [
                    "ontology_investigation_overview",
                    "find_business_terms",
                    "list_use_case_slices",
                    "get_use_case_slice",
                    "find_semantic_patterns",
                    "get_semantic_evidence",
                    "inspect_ontology_subject",
                    "discover_domain_charters",
                    "list_cross_layer_use_cases",
                    "find_state_rule_clusters",
                    "find_implementation_clusters"
                  ]
                },
                "parameters": {"type": "object"},
                "reason": {"$ref": "#/$defs/nonEmptyString"}
              },
              "required": ["schemaVersion", "action", "identity", "operation", "parameters", "reason"]
            },
            {
              "type": "object",
              "additionalProperties": false,
              "properties": {
                "schemaVersion": {"const": "business-ontology-agent-action-v1"},
                "action": {"const": "record"},
                "identity": {"$ref": "#/$defs/identity"},
                "records": {
                  "type": "array",
                  "minItems": 1,
                  "items": {"$ref": "#/$defs/record"}
                }
              },
              "required": ["schemaVersion", "action", "identity", "records"]
            },
            {
              "type": "object",
              "additionalProperties": false,
              "properties": {
                "schemaVersion": {"const": "business-ontology-agent-action-v1"},
                "action": {"const": "finish"},
                "identity": {"$ref": "#/$defs/identity"},
                "status": {"enum": ["completed", "no_candidate", "blocked", "budget_exhausted"]},
                "reason": {"$ref": "#/$defs/nonEmptyString"},
                "unresolved": {
                  "type": "array",
                  "items": {"$ref": "#/$defs/nonEmptyString"}
                }
              },
              "required": ["schemaVersion", "action", "identity", "status", "reason", "unresolved"]
            },
            {
              "type": "object",
              "additionalProperties": false,
              "properties": {
                "schemaVersion": {"const": "business-ontology-agent-action-v1"},
                "action": {"const": "synthesize"},
                "identity": {"$ref": "#/$defs/identity"},
                "domainCharters": {
                  "type": "array",
                  "minItems": 1,
                  "items": {"$ref": "#/$defs/domainCharter"}
                },
                "clusters": {
                  "type": "array",
                  "minItems": 1,
                  "items": {"$ref": "#/$defs/semanticCluster"}
                }
              },
              "required": ["schemaVersion", "action", "identity", "domainCharters", "clusters"]
            }
          ],
          "$defs": {
            "identityString": {
              "type": "string",
              "pattern": "^[A-Za-z0-9][A-Za-z0-9:_./-]{0,191}$"
            },
            "nonEmptyString": {
              "type": "string",
              "minLength": 1
            },
            "identity": {
              "type": "object",
              "additionalProperties": false,
              "properties": {
                "runId": {"$ref": "#/$defs/identityString"},
                "turnId": {"$ref": "#/$defs/identityString"}
              },
              "required": ["runId", "turnId"]
            },
            "record": {
              "type": "object",
              "additionalProperties": false,
              "properties": {
                "recordId": {"$ref": "#/$defs/identityString"},
                "kind": {"enum": ["observation", "hypothesis", "conflict", "gap", "candidate_draft"]},
                "subjectKind": {"enum": ["ontology", "concept", "attribute", "relation", "rule", "lifecycle", "state", "transition", "use_case", "evidence", "unknown"]},
                "subjectId": {"$ref": "#/$defs/identityString"},
                "title": {"$ref": "#/$defs/nonEmptyString"},
                "body": {},
                "status": {"enum": ["observed", "open", "proposed", "confirmed", "rejected", "resolved", "superseded"]},
                "uncertainty": {"type": "number", "minimum": 0, "maximum": 1},
                "queryDigest": {"$ref": "#/$defs/identityString"},
                "evidenceIds": {
                  "type": "array",
                  "minItems": 1,
                  "items": {"$ref": "#/$defs/identityString"}
                }
              },
              "required": ["recordId", "kind", "subjectKind", "subjectId", "title", "body", "status", "uncertainty", "queryDigest", "evidenceIds"]
            },
            "domainCharter": {
              "type": "object",
              "additionalProperties": false,
              "properties": {
                "id": {"type": "string"},
                "nameZh": {"$ref": "#/$defs/nonEmptyString"},
                "descriptionZh": {"$ref": "#/$defs/nonEmptyString"},
                "evidenceIds": {"type": "array", "minItems": 1, "items": {"$ref": "#/$defs/identityString"}},
                "workflowNames": {"type": "array", "items": {"$ref": "#/$defs/nonEmptyString"}}
              },
              "required": ["id", "nameZh", "descriptionZh", "evidenceIds", "workflowNames"]
            },
            "implementationAnchor": {
              "type": "object",
              "additionalProperties": false,
              "properties": {
                "symbolId": {"$ref": "#/$defs/identityString"},
                "role": {"$ref": "#/$defs/nonEmptyString"},
                "relativePath": {"$ref": "#/$defs/nonEmptyString"},
                "evidenceId": {"$ref": "#/$defs/identityString"}
              },
              "required": ["symbolId", "role", "relativePath", "evidenceId"]
            },
            "semanticCluster": {
              "type": "object",
              "additionalProperties": false,
              "properties": {
                "id": {"$ref": "#/$defs/identityString"},
                "domainId": {"type": "string"},
                "conceptId": {"type": "string"},
                "nameZh": {"$ref": "#/$defs/nonEmptyString"},
                "descriptionZh": {"$ref": "#/$defs/nonEmptyString"},
                "implementationAnchors": {"type": "array", "minItems": 2, "items": {"$ref": "#/$defs/implementationAnchor"}}
              },
              "required": ["id", "domainId", "conceptId", "nameZh", "descriptionZh", "implementationAnchors"]
            }
          }
        }
        """);
}

internal static class BusinessOntologyAgentActionKinds
{
    public const string Query = "query";
    public const string Record = "record";
    public const string Finish = "finish";
    public const string Synthesize = "synthesize";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        Query,
        Record,
        Finish,
        Synthesize,
    };
}

internal static class BusinessOntologyAgentQueryOperations
{
    public const string OntologyInvestigationOverview = "ontology_investigation_overview";
    public const string FindBusinessTerms = "find_business_terms";
    public const string ListUseCaseSlices = "list_use_case_slices";
    public const string GetUseCaseSlice = "get_use_case_slice";
    public const string FindSemanticPatterns = "find_semantic_patterns";
    public const string GetSemanticEvidence = "get_semantic_evidence";
    public const string InspectOntologySubject = "inspect_ontology_subject";
    public const string DiscoverDomainCharters = "discover_domain_charters";
    public const string ListCrossLayerUseCases = "list_cross_layer_use_cases";
    public const string FindStateRuleClusters = "find_state_rule_clusters";
    public const string FindImplementationClusters = "find_implementation_clusters";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        OntologyInvestigationOverview,
        FindBusinessTerms,
        ListUseCaseSlices,
        GetUseCaseSlice,
        FindSemanticPatterns,
        GetSemanticEvidence,
        InspectOntologySubject,
        DiscoverDomainCharters,
        ListCrossLayerUseCases,
        FindStateRuleClusters,
        FindImplementationClusters,
    };
}

internal static class BusinessOntologyAgentFinishStatuses
{
    public const string Completed = "completed";
    public const string NoCandidate = "no_candidate";
    public const string Blocked = "blocked";
    public const string BudgetExhausted = "budget_exhausted";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        Completed,
        NoCandidate,
        Blocked,
        BudgetExhausted,
    };
}

internal sealed record BusinessOntologyAgentActionValidationContext(
    string RunId,
    IReadOnlySet<string> AvailableEvidenceIds,
    IReadOnlySet<string>? AvailableQueryDigests = null,
    DateTimeOffset? RecordCreatedAtUtc = null,
    IReadOnlyDictionary<string, IReadOnlySet<string>>? EvidenceIdsByQueryDigest = null,
    IReadOnlyDictionary<string, BusinessOntologyInvestigationEvidenceRef>? EvidenceRefsById = null);

internal sealed record BusinessOntologyAgentActionIdentity(string RunId, string TurnId);

internal abstract record ValidatedBusinessOntologyAgentAction(
    string SchemaVersion,
    string Action,
    BusinessOntologyAgentActionIdentity Identity);

internal sealed record ValidatedBusinessOntologyQueryAction(
    string SchemaVersion,
    BusinessOntologyAgentActionIdentity Identity,
    string Operation,
    string ParametersJson,
    string Reason)
    : ValidatedBusinessOntologyAgentAction(SchemaVersion, BusinessOntologyAgentActionKinds.Query, Identity);

internal sealed record ValidatedBusinessOntologyRecordAction(
    string SchemaVersion,
    BusinessOntologyAgentActionIdentity Identity,
    IReadOnlyList<BusinessOntologyAnalysisRecordInput> Records)
    : ValidatedBusinessOntologyAgentAction(SchemaVersion, BusinessOntologyAgentActionKinds.Record, Identity);

internal sealed record ValidatedBusinessOntologyFinishAction(
    string SchemaVersion,
    BusinessOntologyAgentActionIdentity Identity,
    string Status,
    string Reason,
    IReadOnlyList<string> Unresolved)
    : ValidatedBusinessOntologyAgentAction(SchemaVersion, BusinessOntologyAgentActionKinds.Finish, Identity);

internal sealed record ValidatedBusinessOntologySynthesizeAction(
    string SchemaVersion,
    BusinessOntologyAgentActionIdentity Identity,
    IReadOnlyList<BusinessOntologySemanticDomainCharter> DomainCharters,
    IReadOnlyList<BusinessOntologySemanticCluster> Clusters)
    : ValidatedBusinessOntologyAgentAction(SchemaVersion, BusinessOntologyAgentActionKinds.Synthesize, Identity);

/// <summary>
/// Strict local trust boundary for one untrusted model action. Validation is pure: it normalizes
/// query/record/finish payloads but never dispatches tools and never appends analysis records.
/// </summary>
internal sealed class BusinessOntologyAgentActionValidator
{
    private static readonly Regex Identity = new("^[A-Za-z0-9][A-Za-z0-9:_./-]{0,191}$", RegexOptions.CultureInvariant);
    private static readonly Regex Fqn = new("^[A-Z][A-Za-z0-9]*(?:\\.[A-Z][A-Za-z0-9]*)+$", RegexOptions.CultureInvariant);
    private static readonly Regex Cursor = new("^[A-Za-z0-9+/=]+\\.[0-9A-Fa-f]+$", RegexOptions.CultureInvariant);

    private static readonly HashSet<string> RecordKinds = new(["observation", "hypothesis", "conflict", "gap", "candidate_draft"], StringComparer.Ordinal);
    private static readonly HashSet<string> RecordStatuses = new(["observed", "open", "proposed", "confirmed", "rejected", "resolved", "superseded"], StringComparer.Ordinal);
    private static readonly HashSet<string> SubjectKinds = new(["ontology", "concept", "attribute", "relation", "rule", "lifecycle", "state", "transition", "use_case", "evidence", "unknown"], StringComparer.Ordinal);
    private static readonly HashSet<string> SemanticPatternKinds = new(
        [
            CodeSemanticClaimKinds.TypedReference,
            CodeSemanticClaimKinds.ValidationConstraint,
            CodeSemanticClaimKinds.PersistenceConstraint,
            CodeSemanticClaimKinds.StateField,
            CodeSemanticClaimKinds.StateValue,
            CodeSemanticClaimKinds.StateAssignment,
            CodeSemanticClaimKinds.TransactionScope,
            CodeSemanticClaimKinds.RouteBinding,
            CodeSemanticClaimKinds.BusinessGuard,
        ],
        StringComparer.Ordinal);

    public ValidatedBusinessOntologyAgentAction Validate(
        string actionJson,
        BusinessOntologyAgentActionValidationContext context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actionJson);
        ArgumentNullException.ThrowIfNull(context);
        ValidateIdentity(context.RunId, "context.RunId");

        using var document = ParseStrict(actionJson);
        var root = RequireObject(document.RootElement, "$");
        RequireExactProperties(
            root,
            "$",
            ["schemaVersion", "action", "identity", "operation", "parameters", "reason", "records", "status", "unresolved", "domainCharters", "clusters"],
            ["schemaVersion", "action", "identity"]);

        var schemaVersion = RequireString(root, "schemaVersion", "$");
        if (schemaVersion != BusinessOntologyAgentActionContract.SchemaVersion)
        {
            throw Invalid("$.schemaVersion", $"must be '{BusinessOntologyAgentActionContract.SchemaVersion}'.");
        }

        var action = RequireString(root, "action", "$");
        if (!BusinessOntologyAgentActionKinds.All.Contains(action))
        {
            throw Invalid("$.action", "must be query, record, finish, or synthesize.");
        }

        var identity = ValidateActionIdentity(root.GetProperty("identity"), context);
        return action switch
        {
            BusinessOntologyAgentActionKinds.Query => ValidateQuery(schemaVersion, identity, root, context),
            BusinessOntologyAgentActionKinds.Record => ValidateRecord(schemaVersion, identity, root, context),
            BusinessOntologyAgentActionKinds.Finish => ValidateFinish(schemaVersion, identity, root),
            BusinessOntologyAgentActionKinds.Synthesize => ValidateSynthesize(schemaVersion, identity, root, context),
            _ => throw new InvalidOperationException($"Unsupported action '{action}'."),
        };
    }

    private static ValidatedBusinessOntologyQueryAction ValidateQuery(
        string schemaVersion,
        BusinessOntologyAgentActionIdentity identity,
        JsonElement root,
        BusinessOntologyAgentActionValidationContext context)
    {
        RequireExactProperties(
            root,
            "$",
            ["schemaVersion", "action", "identity", "operation", "parameters", "reason"],
            ["schemaVersion", "action", "identity", "operation", "parameters", "reason"]);
        var operation = RequireString(root, "operation", "$");
        if (!BusinessOntologyAgentQueryOperations.All.Contains(operation))
        {
            throw Invalid("$.operation", "is not an allowed business ontology investigation operation.");
        }

        var parameters = RequireObject(root.GetProperty("parameters"), "$.parameters");
        ValidateQueryParameters(operation, parameters, context);
        var reason = RequireString(root, "reason", "$");
        return new ValidatedBusinessOntologyQueryAction(
            schemaVersion,
            identity,
            operation,
            Canonicalize(parameters),
            reason);
    }

    private static ValidatedBusinessOntologyRecordAction ValidateRecord(
        string schemaVersion,
        BusinessOntologyAgentActionIdentity identity,
        JsonElement root,
        BusinessOntologyAgentActionValidationContext context)
    {
        RequireExactProperties(
            root,
            "$",
            ["schemaVersion", "action", "identity", "records"],
            ["schemaVersion", "action", "identity", "records"]);
        var recordsElement = root.GetProperty("records");
        if (recordsElement.ValueKind != JsonValueKind.Array)
        {
            throw Invalid("$.records", "must be an array.");
        }

        var records = new List<BusinessOntologyAnalysisRecordInput>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var item in recordsElement.EnumerateArray())
        {
            var path = $"$.records[{index++}]";
            var record = ValidateRecordInput(item, path, identity.RunId, context);
            if (!ids.Add(record.RecordId))
            {
                throw Invalid(path + ".recordId", $"duplicates record identity '{record.RecordId}'.");
            }
            records.Add(record);
        }
        if (records.Count == 0)
        {
            throw Invalid("$.records", "must contain at least one record.");
        }

        return new ValidatedBusinessOntologyRecordAction(schemaVersion, identity, records);
    }

    private static ValidatedBusinessOntologyFinishAction ValidateFinish(
        string schemaVersion,
        BusinessOntologyAgentActionIdentity identity,
        JsonElement root)
    {
        RequireExactProperties(
            root,
            "$",
            ["schemaVersion", "action", "identity", "status", "reason", "unresolved"],
            ["schemaVersion", "action", "identity", "status", "reason", "unresolved"]);
        var status = RequireString(root, "status", "$");
        if (!BusinessOntologyAgentFinishStatuses.All.Contains(status))
        {
            throw Invalid("$.status", "must be completed, no_candidate, blocked, or budget_exhausted.");
        }

        return new ValidatedBusinessOntologyFinishAction(
            schemaVersion,
            identity,
            status,
            RequireString(root, "reason", "$"),
            RequireStringArray(root.GetProperty("unresolved"), "$.unresolved"));
    }

    private static ValidatedBusinessOntologySynthesizeAction ValidateSynthesize(
        string schemaVersion,
        BusinessOntologyAgentActionIdentity identity,
        JsonElement root,
        BusinessOntologyAgentActionValidationContext context)
    {
        RequireExactProperties(
            root,
            "$",
            ["schemaVersion", "action", "identity", "domainCharters", "clusters"],
            ["schemaVersion", "action", "identity", "domainCharters", "clusters"]);
        if (context.EvidenceRefsById is not { } evidenceRefsById)
        {
            throw Invalid("$", "synthesize actions require runtime-owned evidence references.");
        }

        var charters = RequireArray(root.GetProperty("domainCharters"), "$.domainCharters");
        var charterIds = new HashSet<string>(StringComparer.Ordinal);
        var domainCharters = new List<BusinessOntologySemanticDomainCharter>();
        var charterIndex = 0;
        foreach (var charterElement in charters)
        {
            var path = $"$.domainCharters[{charterIndex++}]";
            var charter = RequireObject(charterElement, path);
            RequireExactProperties(charter, path, ["id", "nameZh", "descriptionZh", "evidenceIds", "workflowNames"], ["id", "nameZh", "descriptionZh", "evidenceIds", "workflowNames"]);
            var id = RequireDomainId(charter, "id", path);
            if (!charterIds.Add(id))
            {
                throw Invalid(path + ".id", $"duplicates domain charter '{id}'.");
            }
            var nameZh = RequireChineseString(charter, "nameZh", path);
            var descriptionZh = RequireChineseString(charter, "descriptionZh", path);
            var evidenceIds = RequireKnownEvidenceIds(charter.GetProperty("evidenceIds"), path + ".evidenceIds", context);
            var workflowNames = RequireUniqueStrings(charter.GetProperty("workflowNames"), path + ".workflowNames");
            domainCharters.Add(new BusinessOntologySemanticDomainCharter(id, nameZh, descriptionZh, evidenceIds, workflowNames));
        }
        if (domainCharters.Count == 0)
        {
            throw Invalid("$.domainCharters", "must contain at least one domain charter.");
        }

        var clusters = RequireArray(root.GetProperty("clusters"), "$.clusters");
        var clusterIds = new HashSet<string>(StringComparer.Ordinal);
        var conceptIds = new HashSet<string>(StringComparer.Ordinal);
        var semanticClusters = new List<BusinessOntologySemanticCluster>();
        var clusterIndex = 0;
        foreach (var clusterElement in clusters)
        {
            var path = $"$.clusters[{clusterIndex++}]";
            var cluster = RequireObject(clusterElement, path);
            RequireExactProperties(cluster, path, ["id", "domainId", "conceptId", "nameZh", "descriptionZh", "implementationAnchors"], ["id", "domainId", "conceptId", "nameZh", "descriptionZh", "implementationAnchors"]);
            var id = RequireIdentity(cluster, "id", path);
            if (!clusterIds.Add(id))
            {
                throw Invalid(path + ".id", $"duplicates semantic cluster '{id}'.");
            }
            var domainId = RequireDomainId(cluster, "domainId", path);
            if (!charterIds.Contains(domainId))
            {
                throw Invalid(path + ".domainId", $"does not name a declared domain charter '{domainId}'.");
            }
            var conceptId = RequireString(cluster, "conceptId", path);
            if (!BusinessOntologySemanticIdentifierGrammar.IsValidConceptId(conceptId, domainId))
            {
                throw Invalid(path + ".conceptId", "must be exactly two PascalCase business segments whose first segment is the domain root and whose final segment is not an implementation technical suffix.");
            }
            if (!conceptIds.Add(conceptId))
            {
                throw Invalid(path + ".conceptId", $"duplicates semantic concept '{conceptId}'.");
            }

            var anchors = ValidateImplementationAnchors(cluster.GetProperty("implementationAnchors"), path + ".implementationAnchors", context, evidenceRefsById);
            semanticClusters.Add(new BusinessOntologySemanticCluster(
                id,
                domainId,
                conceptId,
                RequireChineseString(cluster, "nameZh", path),
                RequireChineseString(cluster, "descriptionZh", path),
                anchors));
        }
        if (semanticClusters.Count == 0)
        {
            throw Invalid("$.clusters", "must contain at least one semantic cluster.");
        }

        return new ValidatedBusinessOntologySynthesizeAction(schemaVersion, identity, domainCharters, semanticClusters);
    }

    private static BusinessOntologyAgentActionIdentity ValidateActionIdentity(
        JsonElement element,
        BusinessOntologyAgentActionValidationContext context)
    {
        var identity = RequireObject(element, "$.identity");
        RequireExactProperties(identity, "$.identity", ["runId", "turnId"], ["runId", "turnId"]);
        var runId = RequireIdentity(identity, "runId", "$.identity");
        if (!StringComparer.Ordinal.Equals(runId, context.RunId))
        {
            throw Invalid("$.identity.runId", "does not match the active analysis run.");
        }

        return new BusinessOntologyAgentActionIdentity(
            runId,
            RequireIdentity(identity, "turnId", "$.identity"));
    }

    private static BusinessOntologyAnalysisRecordInput ValidateRecordInput(
        JsonElement element,
        string path,
        string runId,
        BusinessOntologyAgentActionValidationContext context)
    {
        var record = RequireObject(element, path);
        RequireExactProperties(
            record,
            path,
            ["recordId", "kind", "subjectKind", "subjectId", "title", "body", "status", "uncertainty", "queryDigest", "evidenceIds"],
            ["recordId", "kind", "subjectKind", "subjectId", "title", "body", "status", "uncertainty", "queryDigest", "evidenceIds"]);

        var recordId = RequireIdentity(record, "recordId", path);
        var kind = RequireString(record, "kind", path);
        if (!RecordKinds.Contains(kind))
        {
            throw Invalid(path + ".kind", "must be observation, hypothesis, conflict, gap, or candidate_draft.");
        }

        var subjectKind = RequireString(record, "subjectKind", path);
        if (!SubjectKinds.Contains(subjectKind))
        {
            throw Invalid(path + ".subjectKind", "is not supported for ontology analysis records.");
        }

        var status = RequireString(record, "status", path);
        if (!RecordStatuses.Contains(status))
        {
            throw Invalid(path + ".status", "is not supported for ontology analysis records.");
        }

        var queryDigest = RequireIdentity(record, "queryDigest", path);
        if (context.AvailableQueryDigests is not { } digests || !digests.Contains(queryDigest))
        {
            throw Invalid(path + ".queryDigest", $"references unknown query digest '{queryDigest}'.");
        }
        if (context.EvidenceIdsByQueryDigest is not { } evidenceIdsByQueryDigest
            || !evidenceIdsByQueryDigest.TryGetValue(queryDigest, out var directEvidenceIds)
            || directEvidenceIds is null)
        {
            throw Invalid(path + ".queryDigest", $"has no direct evidence provenance for query digest '{queryDigest}'.");
        }

        var evidenceIds = RequireStringArray(record.GetProperty("evidenceIds"), path + ".evidenceIds");
        if (evidenceIds.Count == 0)
        {
            throw Invalid(path + ".evidenceIds", "must contain at least one evidence id.");
        }
        if (evidenceIds.Count != evidenceIds.Distinct(StringComparer.Ordinal).Count())
        {
            throw Invalid(path + ".evidenceIds", "must not contain duplicate identities.");
        }
        foreach (var evidenceId in evidenceIds)
        {
            ValidateIdentity(evidenceId, path + ".evidenceIds");
            if (!context.AvailableEvidenceIds.Contains(evidenceId))
            {
                throw Invalid(path + ".evidenceIds", $"references unknown evidence '{evidenceId}'.");
            }
            if (!directEvidenceIds.Contains(evidenceId))
            {
                throw Invalid(path + ".evidenceIds", $"references evidence '{evidenceId}' outside query digest '{queryDigest}' provenance.");
            }
        }

        var uncertainty = RequireNumber(record, "uncertainty", path);
        if (uncertainty is < 0 or > 1 || double.IsNaN(uncertainty))
        {
            throw Invalid(path + ".uncertainty", "must be 0..1.");
        }

        return new BusinessOntologyAnalysisRecordInput(
            runId,
            recordId,
            kind,
            subjectKind,
            RequireIdentity(record, "subjectId", path),
            RequireString(record, "title", path),
            Canonicalize(record.GetProperty("body")),
            status,
            uncertainty,
            queryDigest,
            (context.RecordCreatedAtUtc ?? DateTimeOffset.UtcNow).ToString("O", CultureInfo.InvariantCulture),
            evidenceIds.OrderBy(id => id, StringComparer.Ordinal).ToArray());
    }

    private static void ValidateQueryParameters(
        string operation,
        JsonElement parameters,
        BusinessOntologyAgentActionValidationContext context)
    {
        switch (operation)
        {
            case BusinessOntologyAgentQueryOperations.OntologyInvestigationOverview:
                RequireExactProperties(parameters, "$.parameters", ["ontologyId"], []);
                OptionalFqn(parameters, "ontologyId", "$.parameters");
                break;
            case BusinessOntologyAgentQueryOperations.FindBusinessTerms:
                RequireExactProperties(parameters, "$.parameters", ["term", "ontologyId", "cursor", "limit"], ["term"]);
                RequireBoundedString(parameters, "term", "$.parameters", 160);
                OptionalFqn(parameters, "ontologyId", "$.parameters");
                OptionalCursor(parameters, "cursor", "$.parameters");
                OptionalLimit(parameters, "limit", "$.parameters");
                break;
            case BusinessOntologyAgentQueryOperations.ListUseCaseSlices:
                RequireExactProperties(parameters, "$.parameters", ["ontologyId", "cursor", "limit"], ["ontologyId"]);
                RequireFqn(parameters, "ontologyId", "$.parameters");
                OptionalCursor(parameters, "cursor", "$.parameters");
                OptionalLimit(parameters, "limit", "$.parameters");
                break;
            case BusinessOntologyAgentQueryOperations.GetUseCaseSlice:
                RequireExactProperties(parameters, "$.parameters", ["ontologyId", "sliceId"], ["ontologyId", "sliceId"]);
                RequireFqn(parameters, "ontologyId", "$.parameters");
                RequireIdentity(parameters, "sliceId", "$.parameters");
                break;
            case BusinessOntologyAgentQueryOperations.FindSemanticPatterns:
                RequireExactProperties(parameters, "$.parameters", ["kind", "term", "cursor", "limit"], ["kind"]);
                var kind = RequireString(parameters, "kind", "$.parameters");
                if (!SemanticPatternKinds.Contains(kind))
                {
                    throw Invalid("$.parameters.kind", "is not a supported semantic claim kind.");
                }
                OptionalBoundedString(parameters, "term", "$.parameters", 160);
                OptionalCursor(parameters, "cursor", "$.parameters");
                OptionalLimit(parameters, "limit", "$.parameters");
                break;
            case BusinessOntologyAgentQueryOperations.GetSemanticEvidence:
                RequireExactProperties(parameters, "$.parameters", ["evidenceIds"], ["evidenceIds"]);
                var evidenceIds = RequireStringArray(parameters.GetProperty("evidenceIds"), "$.parameters.evidenceIds");
                if (evidenceIds.Count is < 1 or > SemanticEvidencePackBuilder.MaxAnchors)
                {
                    throw Invalid("$.parameters.evidenceIds", $"must contain 1..{SemanticEvidencePackBuilder.MaxAnchors} ids.");
                }
                if (evidenceIds.Count != evidenceIds.Distinct(StringComparer.Ordinal).Count())
                {
                    throw Invalid("$.parameters.evidenceIds", "must not contain duplicate identities.");
                }
                foreach (var evidenceId in evidenceIds)
                {
                    ValidateIdentity(evidenceId, "$.parameters.evidenceIds");
                    if (!context.AvailableEvidenceIds.Contains(evidenceId))
                    {
                        throw Invalid("$.parameters.evidenceIds", $"references unknown evidence '{evidenceId}'.");
                    }
                }
                break;
            case BusinessOntologyAgentQueryOperations.InspectOntologySubject:
                RequireExactProperties(parameters, "$.parameters", ["ontologyId", "subjectKind", "subjectId"], ["ontologyId", "subjectKind", "subjectId"]);
                RequireFqn(parameters, "ontologyId", "$.parameters");
                var subjectKind = RequireString(parameters, "subjectKind", "$.parameters");
                if (subjectKind is not ("concept" or "relation" or "rule" or "lifecycle" or "candidate"))
                {
                    throw Invalid("$.parameters.subjectKind", "must be concept, relation, rule, lifecycle, or candidate.");
                }
                RequireString(parameters, "subjectId", "$.parameters");
                break;
            case BusinessOntologyAgentQueryOperations.DiscoverDomainCharters:
                RequireExactProperties(parameters, "$.parameters", ["term", "cursor", "limit"], ["term"]);
                RequireBoundedString(parameters, "term", "$.parameters", 160);
                OptionalCursor(parameters, "cursor", "$.parameters");
                OptionalLimit(parameters, "limit", "$.parameters");
                break;
            case BusinessOntologyAgentQueryOperations.ListCrossLayerUseCases:
                RequireExactProperties(parameters, "$.parameters", ["entrySymbolId", "domainSeed", "cursor", "limit"], []);
                var hasEntry = parameters.TryGetProperty("entrySymbolId", out var entryValue) && entryValue.ValueKind != JsonValueKind.Null;
                var hasDomain = parameters.TryGetProperty("domainSeed", out var domainValue) && domainValue.ValueKind != JsonValueKind.Null;
                if (hasEntry == hasDomain)
                {
                    throw Invalid("$.parameters", "must specify exactly one of entrySymbolId or domainSeed.");
                }
                if (hasEntry)
                {
                    var entrySymbolId = RequireString(parameters, "entrySymbolId", "$.parameters");
                    if (!entrySymbolId.StartsWith("symbol:", StringComparison.Ordinal) || !Identity.IsMatch(entrySymbolId))
                    {
                        throw Invalid("$.parameters.entrySymbolId", "must be a bounded symbol id.");
                    }
                }
                if (hasDomain) RequireBoundedString(parameters, "domainSeed", "$.parameters", 160);
                OptionalCursor(parameters, "cursor", "$.parameters");
                OptionalLimit(parameters, "limit", "$.parameters");
                break;
            case BusinessOntologyAgentQueryOperations.FindStateRuleClusters:
                RequireExactProperties(parameters, "$.parameters", ["term", "cursor", "limit"], ["term"]);
                RequireBoundedString(parameters, "term", "$.parameters", 160);
                OptionalCursor(parameters, "cursor", "$.parameters");
                OptionalLimit(parameters, "limit", "$.parameters");
                break;
            case BusinessOntologyAgentQueryOperations.FindImplementationClusters:
                RequireExactProperties(parameters, "$.parameters", ["domainSeed", "evidenceIds", "cursor", "limit"], []);
                var hasClusterDomain = parameters.TryGetProperty("domainSeed", out var clusterDomain) && clusterDomain.ValueKind != JsonValueKind.Null;
                var hasEvidenceIds = parameters.TryGetProperty("evidenceIds", out var evidenceValues) && evidenceValues.ValueKind != JsonValueKind.Null;
                if (hasClusterDomain == hasEvidenceIds)
                {
                    throw Invalid("$.parameters", "must specify exactly one of domainSeed or evidenceIds.");
                }
                if (hasClusterDomain) RequireBoundedString(parameters, "domainSeed", "$.parameters", 160);
                if (hasEvidenceIds)
                {
                    var clusterEvidenceIds = RequireStringArray(parameters.GetProperty("evidenceIds"), "$.parameters.evidenceIds");
                    if (clusterEvidenceIds.Count is < 1 or > SemanticEvidencePackBuilder.MaxAnchors
                        || clusterEvidenceIds.Distinct(StringComparer.Ordinal).Count() != clusterEvidenceIds.Count)
                    {
                        throw Invalid("$.parameters.evidenceIds", $"must contain 1..{SemanticEvidencePackBuilder.MaxAnchors} unique ids.");
                    }
                    foreach (var evidenceId in clusterEvidenceIds)
                    {
                        if (evidenceId.Contains('/', StringComparison.Ordinal) || !Identity.IsMatch(evidenceId) || !context.AvailableEvidenceIds.Contains(evidenceId))
                        {
                            throw Invalid("$.parameters.evidenceIds", $"references unknown evidence '{evidenceId}'.");
                        }
                    }
                }
                OptionalCursor(parameters, "cursor", "$.parameters");
                OptionalLimit(parameters, "limit", "$.parameters");
                break;
        }
    }

    private static IReadOnlyList<BusinessOntologySemanticImplementationAnchor> ValidateImplementationAnchors(
        JsonElement element,
        string path,
        BusinessOntologyAgentActionValidationContext context,
        IReadOnlyDictionary<string, BusinessOntologyInvestigationEvidenceRef> evidenceRefsById)
    {
        var elements = RequireArray(element, path);
        if (elements.Length < 2)
        {
            throw Invalid(path, "must contain at least two implementation anchors.");
        }

        var symbolIds = new HashSet<string>(StringComparer.Ordinal);
        var relativePaths = new HashSet<string>(StringComparer.Ordinal);
        var roles = new HashSet<string>(StringComparer.Ordinal);
        var anchors = new List<BusinessOntologySemanticImplementationAnchor>();
        for (var index = 0; index < elements.Length; index++)
        {
            var anchorPath = $"{path}[{index}]";
            var anchor = RequireObject(elements[index], anchorPath);
            RequireExactProperties(anchor, anchorPath, ["symbolId", "role", "relativePath", "evidenceId"], ["symbolId", "role", "relativePath", "evidenceId"]);
            var symbolId = RequireIdentity(anchor, "symbolId", anchorPath);
            var role = RequireString(anchor, "role", anchorPath);
            var relativePath = RequireRelativePath(anchor, "relativePath", anchorPath);
            var evidenceId = RequireIdentity(anchor, "evidenceId", anchorPath);
            if (!symbolIds.Add(symbolId))
            {
                throw Invalid(anchorPath + ".symbolId", $"duplicates implementation symbol '{symbolId}'.");
            }
            if (!relativePaths.Add(relativePath))
            {
                throw Invalid(anchorPath + ".relativePath", $"duplicates implementation path '{relativePath}'.");
            }
            roles.Add(role);
            if (!context.AvailableEvidenceIds.Contains(evidenceId)
                || !evidenceRefsById.TryGetValue(evidenceId, out var evidenceRef)
                || !StringComparer.Ordinal.Equals(evidenceRef.EvidenceId, evidenceId)
                || !StringComparer.Ordinal.Equals(evidenceRef.SymbolId, symbolId)
                || !StringComparer.Ordinal.Equals(evidenceRef.Path, relativePath))
            {
                throw Invalid(anchorPath + ".evidenceId", "must match a runtime-owned evidence id, symbol id, and relative path.");
            }
            anchors.Add(new BusinessOntologySemanticImplementationAnchor(symbolId, role, relativePath, evidenceId));
        }
        if (roles.Count < 2)
        {
            throw Invalid(path, "must span at least two implementation roles.");
        }
        return anchors;
    }

    private static IReadOnlyList<string> RequireKnownEvidenceIds(
        JsonElement element,
        string path,
        BusinessOntologyAgentActionValidationContext context)
    {
        var evidenceIds = RequireUniqueStrings(element, path);
        if (evidenceIds.Count == 0)
        {
            throw Invalid(path, "must contain at least one evidence id.");
        }
        foreach (var evidenceId in evidenceIds)
        {
            ValidateIdentity(evidenceId, path);
            if (!context.AvailableEvidenceIds.Contains(evidenceId))
            {
                throw Invalid(path, $"references unknown evidence '{evidenceId}'.");
            }
        }
        return evidenceIds;
    }

    private static IReadOnlyList<string> RequireUniqueStrings(JsonElement element, string path)
    {
        var values = RequireStringArray(element, path);
        if (values.Count != values.Distinct(StringComparer.Ordinal).Count())
        {
            throw Invalid(path, "must not contain duplicate values.");
        }
        return values;
    }

    private static string RequireChineseString(JsonElement element, string propertyName, string path)
    {
        var value = RequireString(element, propertyName, path);
        if (!value.Any(character => character is >= '\u4e00' and <= '\u9fff'))
        {
            throw Invalid(path + "." + propertyName, "must contain Chinese business text.");
        }
        return value;
    }

    private static string RequireDomainId(JsonElement element, string propertyName, string path)
    {
        var value = RequireString(element, propertyName, path);
        if (!BusinessOntologySemanticIdentifierGrammar.IsValidDomainId(value))
        {
            throw Invalid(path + "." + propertyName, "must be one PascalCase business root, optionally followed by one PascalCase business-domain segment, and must not end with an implementation technical suffix.");
        }
        return value;
    }

    private static string RequireRelativePath(JsonElement element, string propertyName, string path)
    {
        var relativePath = RequireString(element, propertyName, path);
        if (relativePath.StartsWith("/", StringComparison.Ordinal)
            || relativePath.StartsWith("\\", StringComparison.Ordinal)
            || Regex.IsMatch(relativePath, "^[A-Za-z]:[\\\\/]", RegexOptions.CultureInvariant)
            || relativePath.Split(['/', '\\']).Any(segment => segment == ".."))
        {
            throw Invalid(path + "." + propertyName, "must be a relative path without parent traversal.");
        }
        return relativePath;
    }

    private static JsonElement[] RequireArray(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            throw Invalid(path, "must be an array.");
        }
        return element.EnumerateArray().ToArray();
    }

    private static JsonDocument ParseStrict(string json)
    {
        try
        {
            return JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
            });
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("Business ontology agent action must be strict JSON.", nameof(json), ex);
        }
    }

    private static JsonElement RequireObject(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw Invalid(path, "must be a JSON object.");
        }
        RejectDuplicateProperties(element, path);
        return element;
    }

    private static void RequireExactProperties(
        JsonElement element,
        string path,
        string[] allowed,
        string[] required)
    {
        var properties = element.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
        var unknown = properties.Except(allowed, StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal).FirstOrDefault();
        if (unknown is not null)
        {
            throw Invalid(path + "." + unknown, "is not an allowed field.");
        }
        var missing = required.Except(properties, StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal).FirstOrDefault();
        if (missing is not null)
        {
            throw Invalid(path, $"is missing required field '{missing}'.");
        }
    }

    private static string RequireFqn(JsonElement element, string propertyName, string path)
    {
        var value = RequireString(element, propertyName, path);
        if (!Fqn.IsMatch(value))
        {
            throw Invalid(path + "." + propertyName, "must be a dotted ontology FQN.");
        }
        return value;
    }

    private static void OptionalFqn(JsonElement element, string propertyName, string path)
    {
        if (element.TryGetProperty(propertyName, out var value) && value.ValueKind != JsonValueKind.Null)
        {
            if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()) || !Fqn.IsMatch(value.GetString()!))
            {
                throw Invalid(path + "." + propertyName, "must be a dotted ontology FQN.");
            }
        }
    }

    private static string RequireIdentity(JsonElement element, string propertyName, string path)
    {
        var value = RequireString(element, propertyName, path);
        ValidateIdentity(value, path + "." + propertyName);
        return value;
    }

    private static void OptionalIdentity(JsonElement element, string propertyName, string path)
    {
        if (element.TryGetProperty(propertyName, out var value) && value.ValueKind != JsonValueKind.Null)
        {
            if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            {
                throw Invalid(path + "." + propertyName, "must be a valid identity string.");
            }
            ValidateIdentity(value.GetString()!, path + "." + propertyName);
        }
    }

    private static string RequireString(JsonElement element, string propertyName, string path)
    {
        var property = element.GetProperty(propertyName);
        if (property.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw Invalid(path + "." + propertyName, "must be a non-empty string.");
        }
        return property.GetString()!;
    }

    private static void RequireBoundedString(JsonElement element, string propertyName, string path, int maxLength)
    {
        var value = RequireString(element, propertyName, path);
        if (value.Length > maxLength)
        {
            throw Invalid(path + "." + propertyName, $"must not exceed {maxLength} characters.");
        }
    }

    private static void OptionalBoundedString(JsonElement element, string propertyName, string path, int maxLength)
    {
        if (element.TryGetProperty(propertyName, out var value) && value.ValueKind != JsonValueKind.Null)
        {
            if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            {
                throw Invalid(path + "." + propertyName, "must be a non-empty string.");
            }
            if (value.GetString()!.Length > maxLength)
            {
                throw Invalid(path + "." + propertyName, $"must not exceed {maxLength} characters.");
            }
        }
    }

    private static void OptionalCursor(JsonElement element, string propertyName, string path)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return;
        }
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: > 0 and <= 1024 } cursor || !Cursor.IsMatch(cursor))
        {
            throw Invalid(path + "." + propertyName, "must be a bounded signed investigation cursor.");
        }
    }

    private static double RequireNumber(JsonElement element, string propertyName, string path)
    {
        var property = element.GetProperty(propertyName);
        if (property.ValueKind != JsonValueKind.Number || !property.TryGetDouble(out var value))
        {
            throw Invalid(path + "." + propertyName, "must be a number.");
        }
        return value;
    }

    private static IReadOnlyList<string> RequireStringArray(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            throw Invalid(path, "must be an array.");
        }
        var values = new List<string>();
        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
            {
                throw Invalid($"{path}[{index}]", "must be a non-empty string.");
            }
            values.Add(item.GetString()!);
            index++;
        }
        return values;
    }

    private static void OptionalLimit(JsonElement element, string propertyName, string path)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return;
        }
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var limit) || limit is < 1 or > BusinessOntologyInvestigationService.MaxPageSize)
        {
            throw Invalid(path + "." + propertyName, $"must be an integer from 1 to {BusinessOntologyInvestigationService.MaxPageSize}.");
        }
    }

    private static string Canonicalize(JsonElement element)
    {
        RejectDuplicateProperties(element, "$");
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteCanonical(writer, element);
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void RejectDuplicateProperties(JsonElement element, string path)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var properties = element.EnumerateObject().ToArray();
                var duplicate = properties
                    .GroupBy(property => property.Name, StringComparer.Ordinal)
                    .FirstOrDefault(group => group.Count() > 1);
                if (duplicate is not null)
                {
                    throw Invalid(path + "." + duplicate.Key, "is duplicated.");
                }
                foreach (var property in properties)
                {
                    RejectDuplicateProperties(property.Value, path + "." + property.Name);
                }
                break;
            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    RejectDuplicateProperties(item, $"{path}[{index++}]");
                }
                break;
        }
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonical(writer, item);
                }
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static void ValidateIdentity(string value, string path)
    {
        if (string.IsNullOrWhiteSpace(value) || !Identity.IsMatch(value.Trim()))
        {
            throw Invalid(path, "contains unsupported identity characters.");
        }
    }

    private static ArgumentException Invalid(string path, string message) =>
        new($"Business ontology agent action field '{path}' {message}");
}

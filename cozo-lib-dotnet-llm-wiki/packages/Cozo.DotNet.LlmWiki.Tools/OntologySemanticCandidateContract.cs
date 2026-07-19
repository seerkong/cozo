using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Cozo.DotNet.LlmWiki.Tools;

public static class OntologySemanticCandidateKinds
{
    public const string Relation = "relation";
    public const string Rule = "rule";
    public const string Lifecycle = "lifecycle";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        Relation,
        Rule,
        Lifecycle,
    };
}

public sealed record ValidatedOntologySemanticCandidate(
    string Id,
    string SchemaVersion,
    string Kind,
    string SemanticId,
    string CanonicalPayloadJson,
    string CanonicalSemanticJson,
    IReadOnlyList<string> EvidenceIds,
    string Basis,
    string Rationale);

public static class OntologyCandidateDraftXmlKinds
{
    public const string Type = "type";
    public const string Property = "property";
    public const string Relation = "relation";
    public const string Rule = "rule";
    public const string Lifecycle = "lifecycle";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        Type,
        Property,
        Relation,
        Rule,
        Lifecycle,
    };
}

public static class OntologyCandidateDraftRejectReasons
{
    public const string NotCandidateDraftRecord = "not_candidate_draft_record";
    public const string NonExportableRecordStatus = "non_exportable_record_status";
    public const string InvalidDraftSchema = "invalid_candidate_draft_schema";
    public const string UnsupportedXmlKind = "unsupported_xml_kind";
    public const string InvalidDraftShape = "invalid_candidate_draft_shape";
    public const string UnknownConceptReference = "unknown_concept_reference";
    public const string UnavailableEvidence = "unavailable_evidence";
    public const string MissingInferredEvidence = "missing_inferred_evidence";
}

public static class OntologyCandidateDraftDowngradeReasons
{
    public const string RequestedAcceptedStatus = "requested_accepted_status";
}

public sealed record OntologyCandidateDraftEvidence(string Id, string Grade);

public sealed record OntologyCandidateDraftXmlMappingDecision(
    bool Mappable,
    string? XmlKind,
    string? SemanticId,
    string XmlStatus,
    string CanonicalSemanticJson,
    IReadOnlyList<string> EvidenceIds,
    IReadOnlyList<string> RejectReasons,
    IReadOnlyList<string> DowngradeReasons);

public sealed class OntologyCandidateDraftXmlMappingValidator
{
    public const string SchemaVersion = "candidate-draft-to-ontology-xml-v1";
    private const string HypothesisStatus = "hypothesis";

    private static readonly Regex Fqn = new(
        "^[A-Z][A-Za-z0-9]*(?:\\.[A-Za-z][A-Za-z0-9]*)+$",
        RegexOptions.CultureInvariant);

    private static readonly Regex Name = new(
        "^[a-z][A-Za-z0-9]*$",
        RegexOptions.CultureInvariant);

    private static readonly Regex StateName = new(
        "^[A-Za-z][A-Za-z0-9_]*$",
        RegexOptions.CultureInvariant);

    private static readonly HashSet<string> PrimitiveTypes = new(["String", "Number", "Bool", "Json", "Validity"], StringComparer.Ordinal);
    private static readonly HashSet<string> RuleKinds = new(["Conditional", "CrossEntity", "ComputedDependency", "Existential", "Uniqueness", "Cardinality", "Custom"], StringComparer.Ordinal);
    private static readonly HashSet<string> EvidenceGrades = new(["authoritative", "enforced", "contractual", "presentational", "inferred"], StringComparer.Ordinal);

    public OntologyCandidateDraftXmlMappingDecision Evaluate(
        BusinessOntologyAnalysisRecord record,
        IEnumerable<string> existingConceptIds,
        IEnumerable<OntologyCandidateDraftEvidence> availableEvidence)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(existingConceptIds);
        ArgumentNullException.ThrowIfNull(availableEvidence);

        var rejectReasons = new SortedSet<string>(StringComparer.Ordinal);
        var downgradeReasons = new SortedSet<string>(StringComparer.Ordinal);
        var concepts = existingConceptIds.ToHashSet(StringComparer.Ordinal);
        var evidenceById = availableEvidence
            .Where(item => !string.IsNullOrWhiteSpace(item.Id))
            .GroupBy(item => item.Id.Trim(), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Grade.Trim(), StringComparer.Ordinal);

        if (!StringComparer.Ordinal.Equals(record.Kind, "candidate_draft"))
        {
            rejectReasons.Add(OntologyCandidateDraftRejectReasons.NotCandidateDraftRecord);
            return Decision(false, null, null, "", record.EvidenceIds, rejectReasons, downgradeReasons);
        }

        if (!StringComparer.Ordinal.Equals(record.Status, "proposed"))
        {
            rejectReasons.Add(OntologyCandidateDraftRejectReasons.NonExportableRecordStatus);
        }

        var evidenceIds = record.EvidenceIds.OrderBy(id => id, StringComparer.Ordinal).ToArray();
        foreach (var evidenceId in evidenceIds)
        {
            if (!evidenceById.ContainsKey(evidenceId))
            {
                rejectReasons.Add(OntologyCandidateDraftRejectReasons.UnavailableEvidence);
            }
        }
        if (!evidenceIds.Any(id => evidenceById.TryGetValue(id, out var grade) && StringComparer.Ordinal.Equals(grade, "inferred")))
        {
            rejectReasons.Add(OntologyCandidateDraftRejectReasons.MissingInferredEvidence);
        }
        if (evidenceIds.Any(id => evidenceById.TryGetValue(id, out var grade) && !EvidenceGrades.Contains(grade)))
        {
            rejectReasons.Add(OntologyCandidateDraftRejectReasons.UnavailableEvidence);
        }

        string? xmlKind = null;
        string? semanticId = null;
        var canonicalSemantic = "";
        try
        {
            using var document = JsonDocument.Parse(record.BodyJson, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
            });
            var root = RequireObject(document.RootElement, "$");
            RequireExactProperties(root, "$", ["schemaVersion", "xmlKind", "semantic"], ["schemaVersion", "xmlKind", "semantic"]);
            if (!StringComparer.Ordinal.Equals(RequireString(root, "schemaVersion", "$"), SchemaVersion))
            {
                rejectReasons.Add(OntologyCandidateDraftRejectReasons.InvalidDraftSchema);
            }

            xmlKind = RequireString(root, "xmlKind", "$");
            if (!OntologyCandidateDraftXmlKinds.All.Contains(xmlKind))
            {
                rejectReasons.Add(OntologyCandidateDraftRejectReasons.UnsupportedXmlKind);
            }
            else
            {
                var semantic = RequireObject(root.GetProperty("semantic"), "$.semantic");
                RegisterStatusDowngrade(semantic, "$.semantic", downgradeReasons);
                semanticId = xmlKind switch
                {
                    OntologyCandidateDraftXmlKinds.Type => ValidateType(semantic, concepts),
                    OntologyCandidateDraftXmlKinds.Property => ValidateProperty(semantic, concepts),
                    OntologyCandidateDraftXmlKinds.Relation => ValidateRelationDraft(semantic, concepts),
                    OntologyCandidateDraftXmlKinds.Rule => ValidateRuleDraft(semantic, concepts),
                    OntologyCandidateDraftXmlKinds.Lifecycle => ValidateLifecycleDraft(semantic, concepts, downgradeReasons),
                    _ => null,
                };
                canonicalSemantic = OntologySemanticJson.Canonicalize(semantic.GetRawText());
            }
        }
        catch (UnknownConceptReferenceException)
        {
            rejectReasons.Add(OntologyCandidateDraftRejectReasons.UnknownConceptReference);
        }
        catch (ArgumentException)
        {
            rejectReasons.Add(OntologyCandidateDraftRejectReasons.InvalidDraftShape);
        }
        catch (JsonException)
        {
            rejectReasons.Add(OntologyCandidateDraftRejectReasons.InvalidDraftSchema);
        }

        return Decision(
            rejectReasons.Count == 0,
            xmlKind,
            semanticId,
            canonicalSemantic,
            evidenceIds,
            rejectReasons,
            downgradeReasons);
    }

    private static OntologyCandidateDraftXmlMappingDecision Decision(
        bool mappable,
        string? xmlKind,
        string? semanticId,
        string canonicalSemantic,
        IReadOnlyList<string> evidenceIds,
        IEnumerable<string> rejectReasons,
        IEnumerable<string> downgradeReasons) =>
        new(
            mappable,
            mappable ? xmlKind : null,
            mappable ? semanticId : null,
            HypothesisStatus,
            mappable ? canonicalSemantic : "",
            evidenceIds.OrderBy(id => id, StringComparer.Ordinal).ToArray(),
            rejectReasons.OrderBy(id => id, StringComparer.Ordinal).ToArray(),
            downgradeReasons.OrderBy(id => id, StringComparer.Ordinal).ToArray());

    private static string ValidateType(JsonElement semantic, IReadOnlySet<string> concepts)
    {
        RequireExactProperties(
            semantic,
            "$.semantic",
            ["id", "status", "descriptionZh", "parentConceptId", "abstract"],
            ["id", "descriptionZh"]);
        var id = RequireFqn(semantic, "id", "$.semantic");
        RequireString(semantic, "descriptionZh", "$.semantic");
        if (semantic.TryGetProperty("parentConceptId", out var parent))
        {
            RequireKnownConcept(parent, concepts, "$.semantic.parentConceptId");
        }
        if (semantic.TryGetProperty("abstract", out var abstractValue)
            && abstractValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw Invalid("$.semantic.abstract", "must be boolean.");
        }
        return id;
    }

    private static string ValidateProperty(JsonElement semantic, IReadOnlySet<string> concepts)
    {
        RequireExactProperties(
            semantic,
            "$.semantic",
            ["ownerConceptId", "status", "name", "valueType", "required", "descriptionZh"],
            ["ownerConceptId", "name", "valueType", "required", "descriptionZh"]);
        var owner = RequireKnownConcept(semantic.GetProperty("ownerConceptId"), concepts, "$.semantic.ownerConceptId");
        RequireLowerCamel(semantic, "name", "$.semantic");
        var valueType = RequireString(semantic, "valueType", "$.semantic");
        if (!PrimitiveTypes.Contains(valueType))
        {
            throw Invalid("$.semantic.valueType", "must be a supported ontology XML primitive type.");
        }
        if (semantic.GetProperty("required").ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw Invalid("$.semantic.required", "must be boolean.");
        }
        RequireString(semantic, "descriptionZh", "$.semantic");
        return owner + "." + semantic.GetProperty("name").GetString();
    }

    private static string ValidateRelationDraft(JsonElement semantic, IReadOnlySet<string> concepts)
    {
        RequireExactProperties(
            semantic,
            "$.semantic",
            ["id", "status", "name", "fromConceptId", "toConceptId", "directed", "min", "max", "descriptionZh"],
            ["id", "name", "fromConceptId", "toConceptId", "directed", "min", "max", "descriptionZh"]);
        var id = RequireFqn(semantic, "id", "$.semantic");
        RequireLowerCamel(semantic, "name", "$.semantic");
        RequireKnownConcept(semantic.GetProperty("fromConceptId"), concepts, "$.semantic.fromConceptId");
        RequireKnownConcept(semantic.GetProperty("toConceptId"), concepts, "$.semantic.toConceptId");
        if (semantic.GetProperty("directed").ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw Invalid("$.semantic.directed", "must be boolean.");
        }
        RequireNonNegativeIntegerString(semantic, "min", "$.semantic");
        var max = RequireString(semantic, "max", "$.semantic");
        if (max != "*") RequireNonNegativeIntegerString(semantic, "max", "$.semantic");
        RequireString(semantic, "descriptionZh", "$.semantic");
        return id;
    }

    private static string ValidateRuleDraft(JsonElement semantic, IReadOnlySet<string> concepts)
    {
        RequireExactProperties(
            semantic,
            "$.semantic",
            ["id", "status", "scope", "kind", "statementZh", "when", "require", "violationCode", "violationMessageZh"],
            ["id", "scope", "kind", "statementZh", "require", "violationCode", "violationMessageZh"]);
        var id = RequireFqn(semantic, "id", "$.semantic");
        RequireKnownConcept(semantic.GetProperty("scope"), concepts, "$.semantic.scope");
        var kind = RequireString(semantic, "kind", "$.semantic");
        if (!RuleKinds.Contains(kind))
        {
            throw Invalid("$.semantic.kind", "must be an ontology XML rule kind.");
        }
        RequireString(semantic, "statementZh", "$.semantic");
        if (semantic.TryGetProperty("when", out var when))
        {
            ValidatePredicate(when, concepts, "$.semantic.when");
        }
        ValidatePredicate(semantic.GetProperty("require"), concepts, "$.semantic.require");
        RequireString(semantic, "violationCode", "$.semantic");
        RequireString(semantic, "violationMessageZh", "$.semantic");
        return id;
    }

    private static string ValidateLifecycleDraft(
        JsonElement semantic,
        IReadOnlySet<string> concepts,
        ISet<string> downgradeReasons)
    {
        RequireExactProperties(
            semantic,
            "$.semantic",
            ["id", "status", "subject", "stateProperty", "initial", "descriptionZh", "states", "transitions"],
            ["id", "subject", "stateProperty", "initial", "descriptionZh", "states", "transitions"]);
        var id = RequireFqn(semantic, "id", "$.semantic");
        RequireKnownConcept(semantic.GetProperty("subject"), concepts, "$.semantic.subject");
        RequireLowerCamel(semantic, "stateProperty", "$.semantic");
        var initial = RequireStateToken(semantic, "initial", "$.semantic");
        RequireString(semantic, "descriptionZh", "$.semantic");

        var statesElement = semantic.GetProperty("states");
        if (statesElement.ValueKind != JsonValueKind.Array)
        {
            throw Invalid("$.semantic.states", "must be an array.");
        }
        var states = new HashSet<string>(StringComparer.Ordinal);
        var stateIndex = 0;
        foreach (var state in statesElement.EnumerateArray())
        {
            var path = $"$.semantic.states[{stateIndex++}]";
            var stateObject = RequireObject(state, path);
            RequireExactProperties(stateObject, path, ["id", "terminal", "descriptionZh"], ["id", "terminal", "descriptionZh"]);
            var stateId = RequireStateToken(stateObject, "id", path);
            if (!states.Add(stateId)) throw Invalid(path + ".id", "duplicates a lifecycle state.");
            if (stateObject.GetProperty("terminal").ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                throw Invalid(path + ".terminal", "must be boolean.");
            }
            RequireString(stateObject, "descriptionZh", path);
        }
        if (states.Count < 2 || !states.Contains(initial))
        {
            throw Invalid("$.semantic.initial", "must reference one of at least two declared states.");
        }

        var transitionsElement = semantic.GetProperty("transitions");
        if (transitionsElement.ValueKind != JsonValueKind.Array)
        {
            throw Invalid("$.semantic.transitions", "must be an array.");
        }
        var transitionIds = new HashSet<string>(StringComparer.Ordinal);
        var transitionTuples = new HashSet<string>(StringComparer.Ordinal);
        var transitionIndex = 0;
        foreach (var transition in transitionsElement.EnumerateArray())
        {
            var path = $"$.semantic.transitions[{transitionIndex++}]";
            var transitionObject = RequireObject(transition, path);
            RequireExactProperties(
                transitionObject,
                path,
                ["id", "status", "action", "from", "to", "descriptionZh", "guard", "effects"],
                ["id", "action", "from", "to", "descriptionZh"]);
            RegisterStatusDowngrade(transitionObject, path, downgradeReasons);
            var transitionId = RequireFqn(transitionObject, "id", path);
            if (!transitionIds.Add(transitionId)) throw Invalid(path + ".id", "duplicates a transition.");
            var action = RequireLowerCamel(transitionObject, "action", path);
            var from = RequireStateToken(transitionObject, "from", path);
            var to = RequireStateToken(transitionObject, "to", path);
            if (!states.Contains(from) || !states.Contains(to))
            {
                throw Invalid(path, "transition endpoints must reference declared states.");
            }
            if (!transitionTuples.Add(string.Join('\u001f', action, from, to)))
            {
                throw Invalid(path, "duplicates an action/from/to transition tuple.");
            }
            RequireString(transitionObject, "descriptionZh", path);
            if (transitionObject.TryGetProperty("guard", out var guard)) ValidatePredicate(guard, concepts, path + ".guard");
            if (transitionObject.TryGetProperty("effects", out var effects)) ValidateEffects(effects, path + ".effects");
        }
        if (transitionIds.Count == 0)
        {
            throw Invalid("$.semantic.transitions", "must contain at least one transition.");
        }
        return id;
    }

    private static void ValidatePredicate(JsonElement element, IReadOnlySet<string> concepts, string path)
    {
        var predicate = RequireObject(element, path);
        var properties = predicate.EnumerateObject().ToArray();
        if (properties.Length != 1) throw Invalid(path, "must contain exactly one XML predicate.");
        var (name, value) = (properties[0].Name, properties[0].Value);
        switch (name)
        {
            case "All":
            case "Any":
                if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() < 2) throw Invalid(path + "." + name, "must contain at least two predicates.");
                var index = 0;
                foreach (var child in value.EnumerateArray()) ValidatePredicate(child, concepts, $"{path}.{name}[{index++}]");
                break;
            case "Not":
                ValidatePredicate(value, concepts, path + ".Not");
                break;
            case "PropertyPresent":
                RequireExactPredicateProperties(value, path + ".PropertyPresent", ["property"]);
                RequireLowerCamel(value, "property", path + ".PropertyPresent");
                break;
            case "PropertyEquals":
            case "PropertyNotEquals":
                RequireExactPredicateProperties(value, path + "." + name, ["property", "value"]);
                RequireLowerCamel(value, "property", path + "." + name);
                RequireString(value, "value", path + "." + name);
                break;
            case "PropertyCompare":
                RequireExactPredicateProperties(value, path + ".PropertyCompare", ["property", "op", "value"]);
                RequireLowerCamel(value, "property", path + ".PropertyCompare");
                RequireOneOf(value, "op", path + ".PropertyCompare", ["lt", "lte", "gt", "gte"]);
                RequireString(value, "value", path + ".PropertyCompare");
                break;
            case "TypeIs":
                RequireExactPredicateProperties(value, path + ".TypeIs", ["type"]);
                RequireKnownConcept(value.GetProperty("type"), concepts, path + ".TypeIs.type");
                break;
            case "RelatedExists":
            case "EveryRelated":
                RequireExactPredicateProperties(value, path + "." + name, ["relation", "predicate"]);
                RequireFqn(value, "relation", path + "." + name);
                ValidatePredicate(value.GetProperty("predicate"), concepts, path + "." + name + ".predicate");
                break;
            case "RelatedCount":
                RequireExactPredicateProperties(value, path + ".RelatedCount", ["relation", "op", "value"]);
                RequireFqn(value, "relation", path + ".RelatedCount");
                RequireOneOf(value, "op", path + ".RelatedCount", ["eq", "neq", "lt", "lte", "gt", "gte"]);
                RequireNonNegativeIntegerValue(value.GetProperty("value"), path + ".RelatedCount.value");
                break;
            case "ExistsRelated":
                RequireExactPredicateProperties(value, path + ".ExistsRelated", ["relation", "direction", "targetType"]);
                RequireFqn(value, "relation", path + ".ExistsRelated");
                RequireOneOf(value, "direction", path + ".ExistsRelated", ["out", "in"]);
                RequireKnownConcept(value.GetProperty("targetType"), concepts, path + ".ExistsRelated.targetType");
                break;
            default:
                throw Invalid(path + "." + name, "is not an ontology XML predicate.");
        }
    }

    private static void ValidateEffects(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() == 0)
        {
            throw Invalid(path, "must be a non-empty array.");
        }
        var index = 0;
        foreach (var effect in element.EnumerateArray())
        {
            var effectObject = RequireObject(effect, $"{path}[{index++}]");
            var properties = effectObject.EnumerateObject().ToArray();
            if (properties.Length != 1) throw Invalid(path, "effect must contain exactly one XML effect.");
            var (name, value) = (properties[0].Name, properties[0].Value);
            switch (name)
            {
                case "SetProperty":
                    RequireExactPredicateProperties(value, path + ".SetProperty", ["property", "value"]);
                    RequireLowerCamel(value, "property", path + ".SetProperty");
                    RequireString(value, "value", path + ".SetProperty");
                    break;
                case "ClearProperty":
                    RequireExactPredicateProperties(value, path + ".ClearProperty", ["property"]);
                    RequireLowerCamel(value, "property", path + ".ClearProperty");
                    break;
                case "CreateRelation":
                case "RemoveRelation":
                    RequireExactPredicateProperties(value, path + "." + name, ["relation", "targetRef"]);
                    RequireFqn(value, "relation", path + "." + name);
                    RequireString(value, "targetRef", path + "." + name);
                    break;
                default:
                    throw Invalid(path + "." + name, "is not an ontology XML effect.");
            }
        }
    }

    private static void RegisterStatusDowngrade(JsonElement element, string path, ISet<string> downgradeReasons)
    {
        if (!element.TryGetProperty("status", out var statusElement)) return;
        var status = statusElement.ValueKind == JsonValueKind.String ? statusElement.GetString() : "";
        if (StringComparer.Ordinal.Equals(status, "accepted"))
        {
            downgradeReasons.Add(OntologyCandidateDraftDowngradeReasons.RequestedAcceptedStatus);
            return;
        }
        if (!StringComparer.Ordinal.Equals(status, HypothesisStatus))
        {
            throw Invalid(path + ".status", "must be accepted or hypothesis.");
        }
    }

    private static JsonElement RequireObject(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.Object) throw Invalid(path, "must be a JSON object.");
        OntologySemanticJson.RejectDuplicateProperties(element, path);
        return element;
    }

    private static void RequireExactPredicateProperties(JsonElement element, string path, string[] required)
    {
        var value = RequireObject(element, path);
        RequireExactProperties(value, path, required, required);
    }

    private static void RequireExactProperties(
        JsonElement element,
        string path,
        string[] allowed,
        string[] required)
    {
        var properties = element.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
        var unknown = properties.Except(allowed, StringComparer.Ordinal).Order(StringComparer.Ordinal).FirstOrDefault();
        if (unknown is not null) throw Invalid(path + "." + unknown, "is not an allowed field.");
        var missing = required.Except(properties, StringComparer.Ordinal).Order(StringComparer.Ordinal).FirstOrDefault();
        if (missing is not null) throw Invalid(path, $"is missing required field '{missing}'.");
    }

    private static string RequireKnownConcept(JsonElement element, IReadOnlySet<string> concepts, string path)
    {
        var value = RequireFqnValue(element, path);
        if (!concepts.Contains(value)) throw new UnknownConceptReferenceException();
        return value;
    }

    private static string RequireFqn(JsonElement element, string propertyName, string path) =>
        RequireFqnValue(element.GetProperty(propertyName), path + "." + propertyName);

    private static string RequireFqnValue(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(element.GetString()))
        {
            throw Invalid(path, "must be a non-empty string.");
        }
        var value = element.GetString()!;
        if (!Fqn.IsMatch(value)) throw Invalid(path, "must be a dotted ontology FQN.");
        return value;
    }

    private static string RequireLowerCamel(JsonElement element, string propertyName, string path)
    {
        var value = RequireString(element, propertyName, path);
        if (!Name.IsMatch(value)) throw Invalid(path + "." + propertyName, "must be lowerCamelCase.");
        return value;
    }

    private static string RequireStateToken(JsonElement element, string propertyName, string path)
    {
        var value = RequireString(element, propertyName, path);
        if (!StateName.IsMatch(value)) throw Invalid(path + "." + propertyName, "must be a lifecycle state token.");
        return value;
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

    private static void RequireOneOf(JsonElement element, string propertyName, string path, string[] allowed)
    {
        var value = RequireString(element, propertyName, path);
        if (!allowed.Contains(value, StringComparer.Ordinal))
        {
            throw Invalid(path + "." + propertyName, $"must be one of: {string.Join(", ", allowed)}.");
        }
    }

    private static void RequireNonNegativeIntegerString(JsonElement element, string propertyName, string path)
    {
        var value = RequireString(element, propertyName, path);
        if (!int.TryParse(value, out var number) || number < 0)
        {
            throw Invalid(path + "." + propertyName, "must be a non-negative integer string.");
        }
    }

    private static void RequireNonNegativeIntegerValue(JsonElement element, string path)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var number) && number >= 0) return;
        if (element.ValueKind == JsonValueKind.String && int.TryParse(element.GetString(), out number) && number >= 0) return;
        throw Invalid(path, "must be a non-negative integer.");
    }

    private static ArgumentException Invalid(string path, string message) =>
        new($"Candidate draft XML mapping field '{path}' {message}");

    private sealed class UnknownConceptReferenceException : Exception;
}

/// <summary>
/// Strict local trust boundary for deterministic and assisted semantic proposals.
/// It validates only existing ontology concepts and supplied evidence identities.
/// </summary>
public sealed class OntologySemanticCandidateValidator
{
    public const string SchemaVersion = "onto-semantic-v1";

    private static readonly Regex Fqn = new(
        "^[A-Z][A-Za-z0-9]*(?:\\.[A-Za-z][A-Za-z0-9]*)+$",
        RegexOptions.CultureInvariant);

    private static readonly Regex Name = new(
        "^[a-z][A-Za-z0-9]*$",
        RegexOptions.CultureInvariant);

    private static readonly Regex StateName = new(
        "^[A-Za-z][A-Za-z0-9_]*$",
        RegexOptions.CultureInvariant);

    private static readonly HashSet<string> Bases = new(["deterministic", "assisted"], StringComparer.Ordinal);
    private static readonly HashSet<string> RuleKinds = new(["required", "min", "max", "pattern", "unique", "businessCondition"], StringComparer.Ordinal);

    public ValidatedOntologySemanticCandidate Validate(
        string payloadJson,
        IEnumerable<string> existingConceptIds,
        IEnumerable<string> availableEvidenceIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadJson);
        ArgumentNullException.ThrowIfNull(existingConceptIds);
        ArgumentNullException.ThrowIfNull(availableEvidenceIds);

        var concepts = existingConceptIds.ToHashSet(StringComparer.Ordinal);
        var evidence = availableEvidenceIds.ToHashSet(StringComparer.Ordinal);

        using var document = JsonDocument.Parse(payloadJson, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
        });
        var root = RequireObject(document.RootElement, "$");
        RequireExactProperties(
            root,
            "$",
            ["schemaVersion", "kind", "semantic", "evidenceIds", "basis", "rationale"],
            ["schemaVersion", "kind", "semantic", "evidenceIds", "basis", "rationale"]);

        var schemaVersion = RequireString(root, "schemaVersion", "$");
        if (schemaVersion != SchemaVersion)
        {
            throw Invalid("$.schemaVersion", $"must be '{SchemaVersion}'.");
        }

        var kind = RequireString(root, "kind", "$");
        if (!OntologySemanticCandidateKinds.All.Contains(kind))
        {
            throw Invalid("$.kind", $"must be one of: {string.Join(", ", OntologySemanticCandidateKinds.All.Order(StringComparer.Ordinal))}.");
        }

        var basis = RequireString(root, "basis", "$");
        if (!Bases.Contains(basis))
        {
            throw Invalid("$.basis", "must be 'deterministic' or 'assisted'.");
        }

        var rationale = RequireString(root, "rationale", "$");
        if (rationale.Length > 2_000)
        {
            throw Invalid("$.rationale", "must not exceed 2000 characters.");
        }

        var evidenceIds = RequireStringArray(root.GetProperty("evidenceIds"), "$.evidenceIds");
        if (evidenceIds.Count == 0)
        {
            throw Invalid("$.evidenceIds", "must contain at least one direct evidence identity.");
        }
        if (evidenceIds.Count != evidenceIds.Distinct(StringComparer.Ordinal).Count())
        {
            throw Invalid("$.evidenceIds", "must not contain duplicate identities.");
        }
        foreach (var evidenceId in evidenceIds)
        {
            if (!evidence.Contains(evidenceId))
            {
                throw Invalid("$.evidenceIds", $"references unavailable evidence '{evidenceId}'.");
            }
        }

        var semantic = RequireObject(root.GetProperty("semantic"), "$.semantic");
        var semanticId = kind switch
        {
            OntologySemanticCandidateKinds.Relation => ValidateRelation(semantic, concepts),
            OntologySemanticCandidateKinds.Rule => ValidateRule(semantic, concepts),
            OntologySemanticCandidateKinds.Lifecycle => ValidateLifecycle(semantic, concepts),
            _ => throw new InvalidOperationException($"Unsupported semantic candidate kind '{kind}'."),
        };

        var sortedEvidenceIds = evidenceIds.Order(StringComparer.Ordinal).ToArray();
        var canonicalSemantic = OntologySemanticJson.Canonicalize(semantic.GetRawText());
        var normalizedPayload = JsonSerializer.Serialize(new
        {
            schemaVersion,
            kind,
            semantic = JsonSerializer.Deserialize<JsonElement>(canonicalSemantic),
            evidenceIds = sortedEvidenceIds,
            basis,
            rationale,
        });
        var canonicalPayload = OntologySemanticJson.Canonicalize(normalizedPayload);
        var identityMaterial = string.Join(
            '\u001f',
            schemaVersion,
            kind,
            canonicalSemantic,
            string.Join('\u001e', sortedEvidenceIds));
        var id = "candidate:semantic:" +
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identityMaterial))).ToLowerInvariant();

        return new ValidatedOntologySemanticCandidate(
            id,
            schemaVersion,
            kind,
            semanticId,
            canonicalPayload,
            canonicalSemantic,
            sortedEvidenceIds,
            basis,
            rationale);
    }

    private static string ValidateRelation(JsonElement semantic, IReadOnlySet<string> concepts)
    {
        RequireExactProperties(
            semantic,
            "$.semantic",
            ["id", "fromConceptId", "toConceptId", "name", "min", "max", "descriptionZh"],
            ["id", "fromConceptId", "toConceptId", "name", "min", "max", "descriptionZh"]);
        var id = RequireFqn(semantic, "id", "$.semantic");
        RequireKnownConcept(semantic, "fromConceptId", concepts, "$.semantic");
        RequireKnownConcept(semantic, "toConceptId", concepts, "$.semantic");
        var name = RequireString(semantic, "name", "$.semantic");
        if (!Name.IsMatch(name))
        {
            throw Invalid("$.semantic.name", "must be lowerCamelCase.");
        }

        var min = RequireString(semantic, "min", "$.semantic");
        var max = RequireString(semantic, "max", "$.semantic");
        if (min is not ("0" or "1") || max is not ("1" or "many"))
        {
            throw Invalid("$.semantic", "relation cardinality must use min '0|1' and max '1|many'.");
        }
        RequireString(semantic, "descriptionZh", "$.semantic");
        return id;
    }

    private static string ValidateRule(JsonElement semantic, IReadOnlySet<string> concepts)
    {
        RequireExactProperties(
            semantic,
            "$.semantic",
            ["id", "subjectConceptId", "ruleKind", "descriptionZh", "predicate", "effect"],
            ["id", "subjectConceptId", "ruleKind", "descriptionZh", "predicate", "effect"]);
        var id = RequireFqn(semantic, "id", "$.semantic");
        RequireKnownConcept(semantic, "subjectConceptId", concepts, "$.semantic");
        var ruleKind = RequireString(semantic, "ruleKind", "$.semantic");
        if (!RuleKinds.Contains(ruleKind))
        {
            throw Invalid("$.semantic.ruleKind", $"must be one of: {string.Join(", ", RuleKinds.Order(StringComparer.Ordinal))}.");
        }
        RequireString(semantic, "descriptionZh", "$.semantic");
        RequireDeclarativeObject(semantic.GetProperty("predicate"), "$.semantic.predicate");
        RequireDeclarativeObject(semantic.GetProperty("effect"), "$.semantic.effect");
        return id;
    }

    private static string ValidateLifecycle(JsonElement semantic, IReadOnlySet<string> concepts)
    {
        RequireExactProperties(
            semantic,
            "$.semantic",
            ["id", "subjectConceptId", "stateProperty", "initialState", "descriptionZh", "states", "transitions"],
            ["id", "subjectConceptId", "stateProperty", "initialState", "descriptionZh", "states", "transitions"]);
        var id = RequireFqn(semantic, "id", "$.semantic");
        RequireKnownConcept(semantic, "subjectConceptId", concepts, "$.semantic");
        var stateProperty = RequireString(semantic, "stateProperty", "$.semantic");
        if (!Name.IsMatch(stateProperty))
        {
            throw Invalid("$.semantic.stateProperty", "must be lowerCamelCase.");
        }
        var initialState = RequireStateName(semantic, "initialState", "$.semantic");
        RequireString(semantic, "descriptionZh", "$.semantic");

        var statesElement = semantic.GetProperty("states");
        if (statesElement.ValueKind != JsonValueKind.Array)
        {
            throw Invalid("$.semantic.states", "must be an array.");
        }
        var states = new HashSet<string>(StringComparer.Ordinal);
        var stateIndex = 0;
        foreach (var state in statesElement.EnumerateArray())
        {
            var path = $"$.semantic.states[{stateIndex++}]";
            var stateObject = RequireObject(state, path);
            RequireExactProperties(stateObject, path, ["id", "terminal", "descriptionZh"], ["id", "terminal", "descriptionZh"]);
            var stateId = RequireStateName(stateObject, "id", path);
            if (!states.Add(stateId))
            {
                throw Invalid(path + ".id", $"duplicates state '{stateId}'.");
            }
            if (stateObject.GetProperty("terminal").ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                throw Invalid(path + ".terminal", "must be boolean.");
            }
            RequireString(stateObject, "descriptionZh", path);
        }
        if (states.Count < 2)
        {
            throw Invalid("$.semantic.states", "must contain at least two distinct states.");
        }
        if (!states.Contains(initialState))
        {
            throw Invalid("$.semantic.initialState", $"references unknown lifecycle state '{initialState}'.");
        }

        var transitionsElement = semantic.GetProperty("transitions");
        if (transitionsElement.ValueKind != JsonValueKind.Array)
        {
            throw Invalid("$.semantic.transitions", "must be an array.");
        }
        var transitionIds = new HashSet<string>(StringComparer.Ordinal);
        var transitionTuples = new HashSet<string>(StringComparer.Ordinal);
        var transitionIndex = 0;
        foreach (var transition in transitionsElement.EnumerateArray())
        {
            var path = $"$.semantic.transitions[{transitionIndex++}]";
            var transitionObject = RequireObject(transition, path);
            RequireExactProperties(
                transitionObject,
                path,
                ["id", "action", "fromState", "toState", "descriptionZh", "guard", "effect"],
                ["id", "action", "fromState", "toState", "descriptionZh", "guard", "effect"]);
            var transitionId = RequireFqn(transitionObject, "id", path);
            if (!transitionIds.Add(transitionId))
            {
                throw Invalid(path + ".id", $"duplicates transition '{transitionId}'.");
            }
            var action = RequireString(transitionObject, "action", path);
            if (!Name.IsMatch(action))
            {
                throw Invalid(path + ".action", "must be lowerCamelCase.");
            }
            var fromState = RequireStateName(transitionObject, "fromState", path);
            var toState = RequireStateName(transitionObject, "toState", path);
            if (!states.Contains(fromState) || !states.Contains(toState))
            {
                throw Invalid(path, "transition endpoints must reference states declared by this lifecycle.");
            }
            if (!transitionTuples.Add(string.Join('\u001f', action, fromState, toState)))
            {
                throw Invalid(path, "duplicates an existing action/from/to transition tuple.");
            }
            RequireString(transitionObject, "descriptionZh", path);
            RequireDeclarativeObject(transitionObject.GetProperty("guard"), path + ".guard");
            RequireDeclarativeObject(transitionObject.GetProperty("effect"), path + ".effect");
        }
        return id;
    }

    private static JsonElement RequireObject(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw Invalid(path, "must be a JSON object.");
        }
        OntologySemanticJson.RejectDuplicateProperties(element, path);
        return element;
    }

    private static void RequireExactProperties(
        JsonElement element,
        string path,
        string[] allowed,
        string[] required)
    {
        var properties = element.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
        var unknown = properties.Except(allowed, StringComparer.Ordinal).Order(StringComparer.Ordinal).FirstOrDefault();
        if (unknown is not null)
        {
            throw Invalid(path + "." + unknown, "is not an allowed field.");
        }
        var missing = required.Except(properties, StringComparer.Ordinal).Order(StringComparer.Ordinal).FirstOrDefault();
        if (missing is not null)
        {
            throw Invalid(path, $"is missing required field '{missing}'.");
        }
    }

    private static string RequireKnownConcept(
        JsonElement element,
        string propertyName,
        IReadOnlySet<string> concepts,
        string path)
    {
        var value = RequireFqn(element, propertyName, path);
        if (!concepts.Contains(value))
        {
            throw Invalid(path + "." + propertyName, $"references unknown concept '{value}'.");
        }
        return value;
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

    private static string RequireStateName(JsonElement element, string propertyName, string path)
    {
        var value = RequireString(element, propertyName, path);
        if (!StateName.IsMatch(value))
        {
            throw Invalid(path + "." + propertyName, "must be a finite state token.");
        }
        return value;
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

    private static void RequireDeclarativeObject(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw Invalid(path, "must be a declarative JSON object.");
        }
        OntologySemanticJson.RejectDuplicateProperties(element, path);
    }

    private static ArgumentException Invalid(string path, string message) =>
        new($"Semantic candidate field '{path}' {message}");
}

internal static class OntologySemanticJson
{
    public static string Canonicalize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Canonical semantic JSON must be an object.", nameof(json));
        }
        RejectDuplicateProperties(document.RootElement, "$");

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteCanonical(writer, document.RootElement);
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static void RejectDuplicateProperties(JsonElement element, string path)
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
                    throw new ArgumentException($"Semantic JSON field '{path}.{duplicate.Key}' is duplicated.");
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
}

using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Cozo.DotNet.Om;

namespace Cozo.DotNet.LlmWiki.Tools;

public sealed record BusinessOntologyDerivationRequest(
    string OntologyId,
    string GenerationId,
    string SourceFingerprint,
    string CreatedAt,
    string GeneratorVersion = "onto-candidate-deriver/1");

public sealed record BusinessOntologyDerivationResult(
    string OntologyId,
    string GenerationId,
    int Concepts,
    int Attributes,
    int Mappings,
    int Candidates,
    int Evidence);

/// <summary>
/// Conservative, deterministic projection from CodeKnowledge observations into business-ontology
/// hypotheses. It intentionally has no dependency on the DEPA package or depa_* relations.
/// </summary>
public sealed class BusinessOntologyCandidateDeriver(CozoOm om, BusinessOntologyStore store)
{
    private static readonly HashSet<string> BusinessRoles = new(StringComparer.Ordinal)
    {
        "spring:role:dto", "spring:role:entity", "spring:role:vo"
    };

    private static readonly HashSet<string> ImplementationRoles = new(StringComparer.Ordinal)
    {
        "spring:role:controller", "spring:role:service", "spring:role:repository"
    };

    public async Task<BusinessOntologyDerivationResult> DeriveAsync(
        BusinessOntologyDerivationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var symbols = await ReadSymbolsAsync(cancellationToken);
        var roles = await ReadRolesAsync(cancellationToken);
        var entryPoints = await ReadEntryPointsAsync(cancellationToken);
        var byId = symbols.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var rolesBySymbol = roles
            .GroupBy(item => item.SymbolId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderBy(item => item.Role, StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);

        var evidence = new Dictionary<string, BusinessOntologyEvidence>(StringComparer.Ordinal);
        var concepts = new List<BusinessOntologyConcept>();
        var attributes = new List<BusinessOntologyAttribute>();
        var mappings = new List<BusinessOntologyMapping>();
        var candidates = new Dictionary<string, BusinessOntologyCandidate>(StringComparer.Ordinal);
        var usedConceptIds = new HashSet<string>(StringComparer.Ordinal);

        BusinessOntologyEvidence EvidenceFor(CodeObservation source, string grade, string summary)
        {
            var id = "evidence:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source.Id))).ToLowerInvariant();
            if (!evidence.TryGetValue(id, out var item))
            {
                item = new BusinessOntologyEvidence(
                    id, source.Repository, source.Path, source.Id, Math.Max(1, source.StartLine), Math.Max(Math.Max(1, source.StartLine), source.EndLine),
                    grade, string.IsNullOrWhiteSpace(source.Resolver) ? "codeknowledge" : source.Resolver,
                    Confidence(source.Confidence), "code", summary);
                evidence.Add(id, item);
            }
            return item;
        }

        void AddCandidate(BusinessOntologyCandidate candidate) => candidates.TryAdd(candidate.Id, candidate);

        var carrierDrafts = symbols
            .OrderBy(item => item.Id, StringComparer.Ordinal)
            .Select(symbol =>
            {
                var symbolRoles = rolesBySymbol.GetValueOrDefault(symbol.Id, []);
                var role = symbolRoles.FirstOrDefault(item => BusinessRoles.Contains(item.Role));
                var carrierRole = CarrierRole(symbol, role?.Role);
                var localName = NormalizeConceptName(symbol.Name, carrierRole);
                return carrierRole.Length == 0 || localName.Length == 0
                    ? null
                    : new CarrierDraft(symbol, role, carrierRole, localName);
            })
            .Where(item => item is not null)
            .Select(item => item!)
            .ToArray();

        foreach (var draft in carrierDrafts)
        {
            var evidenceItem = EvidenceFor(
                draft.Symbol,
                "inferred",
                $"从 {RoleLabel(draft.CarrierRole)} 代码观察到的实现载体：{draft.Symbol.Name}");
            AddCandidate(new BusinessOntologyCandidate("candidate:implementation:" + draft.Symbol.Id + ":" + draft.CarrierRole,
                "implementation", request.OntologyId + ".Implementation." + draft.LocalName,
                JsonSerializer.Serialize(new
                {
                    sourceSymbol = draft.Symbol.Id,
                    sourceName = draft.Symbol.Name,
                    role = draft.CarrierRole,
                    canonicalFamily = draft.LocalName,
                }),
                "DTO/entity/VO/request/response 等代码载体只能作为实现证据；需跨层收敛或显式领域锚点后才可提出业务概念。", Confidence(draft.Role?.Confidence ?? draft.Symbol.Confidence), "pending", [evidenceItem.Id]));
        }

        foreach (var group in carrierDrafts
            .GroupBy(item => CanonicalFamilyKey(item, carrierDrafts), StringComparer.Ordinal)
            .OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            var members = group.ToArray();
            if (!HasCanonicalConceptSupport(members))
            {
                continue;
            }

            var localName = group.Key;
            var conceptId = request.OntologyId + "." + localName;
            if (!usedConceptIds.Add(conceptId))
            {
                continue;
            }

            var conceptEvidence = members
                .Select(member => EvidenceFor(
                    member.Symbol,
                    "inferred",
                    $"从 {RoleLabel(member.CarrierRole)} 代码观察参与归并的业务概念候选：{member.Symbol.Name}"))
                .OrderBy(item => item.Id, StringComparer.Ordinal)
                .ToArray();
            var confidence = members.Select(item => Confidence(item.Role?.Confidence ?? item.Symbol.Confidence)).Min();
            var sourceNames = members.Select(item => item.Symbol.Name).Order(StringComparer.Ordinal).ToArray();
            concepts.Add(new BusinessOntologyConcept(conceptId, "businessObject", localName, $"根据后端 {string.Join("、", sourceNames)} 的跨角色收敛推导的业务概念候选。", "hypothesis", confidence, conceptEvidence.Select(item => item.Id).ToArray()));
            AddCandidate(new BusinessOntologyCandidate("candidate:concept:" + conceptId, "concept", conceptId,
                JsonSerializer.Serialize(new
                {
                    canonicalFamily = localName,
                    carriers = members
                        .OrderBy(item => item.Symbol.Id, StringComparer.Ordinal)
                        .Select(item => new { sourceSymbol = item.Symbol.Id, sourceName = item.Symbol.Name, role = item.CarrierRole })
                        .ToArray(),
                }),
                "同一 canonical family 至少两个独立实现角色收敛，因此提出业务概念候选，尚待人工确认。", confidence, "pending", conceptEvidence.Select(item => item.Id).ToArray()));

            foreach (var member in members.OrderBy(item => item.Symbol.Id, StringComparer.Ordinal))
            {
                var evidenceItem = EvidenceFor(
                    member.Symbol,
                    "inferred",
                    $"从 {RoleLabel(member.CarrierRole)} 代码观察参与归并的业务概念候选：{member.Symbol.Name}");
                mappings.Add(new BusinessOntologyMapping(
                    request.OntologyId + ".Mapping." + localName + "." + PascalToken(member.CarrierRole) + "." + HashToken(member.Symbol.Id),
                    "concept", conceptId, "representedBy",
                    member.Symbol.Repository, member.Symbol.Language, string.IsNullOrWhiteSpace(member.Symbol.Kind) ? "type" : member.Symbol.Kind, member.Symbol.Id, member.Symbol.Path,
                    evidenceItem.Resolver, Confidence(member.Role?.Confidence ?? member.Symbol.Confidence), "hypothesis", [evidenceItem.Id]));
            }

            var fieldDrafts = members
                .SelectMany(member => symbols
                    .Where(item => item.ParentId == member.Symbol.Id && item.Kind == "field")
                    .Select(field => (Member: member, Field: field, PropertyName: LowerCamel(field.Name))))
                .Where(item => item.PropertyName.Length > 0 && IsBusinessAttributeName(item.PropertyName))
                .ToArray();
            var domainFields = fieldDrafts
                .Where(item => IsDomainAttributeCarrier(item.Member))
                .ToArray();
            var fields = (domainFields.Length > 0
                    ? domainFields
                    : fieldDrafts
                        .GroupBy(item => item.PropertyName, StringComparer.Ordinal)
                        .Where(group => group.Select(item => CarrierRoleFamily(item.Member.CarrierRole)).Distinct(StringComparer.Ordinal).Count() >= 2)
                        .SelectMany(group => group))
                .GroupBy(item => item.PropertyName, StringComparer.Ordinal)
                .OrderBy(item => item.Key, StringComparer.Ordinal);
            foreach (var fieldGroup in fields)
            {
                var fieldItems = fieldGroup
                    .OrderBy(item => item.Field.Id, StringComparer.Ordinal)
                    .ToArray();
                var fieldEvidence = fieldItems
                    .Select(item => EvidenceFor(item.Field, "inferred", $"从 {localName}.{item.Field.Name} 字段声明推导的属性候选。"))
                    .OrderBy(item => item.Id, StringComparer.Ordinal)
                    .ToArray();
                attributes.Add(new BusinessOntologyAttribute(conceptId, fieldGroup.Key, "String", false,
                    $"根据跨载体代码字段 {fieldGroup.Key} 推导的属性候选。", "hypothesis", fieldItems.Select(item => Confidence(item.Field.Confidence)).Min(), fieldEvidence.Select(item => item.Id).ToArray()));
            }
        }

        foreach (var role in roles.Where(item => ImplementationRoles.Contains(item.Role)).OrderBy(item => item.SymbolId, StringComparer.Ordinal).ThenBy(item => item.Role, StringComparer.Ordinal))
        {
            if (!byId.TryGetValue(role.SymbolId, out var symbol))
            {
                continue;
            }
            if (symbol.Kind is not ("class" or "record" or "interface"))
            {
                continue;
            }
            var evidenceItem = EvidenceFor(symbol, "inferred", $"从 {RoleLabel(role.Role)} 观察到的实现候选：{symbol.Name}");
            AddCandidate(new BusinessOntologyCandidate("candidate:implementation:" + symbol.Id + ":" + role.Role,
                "implementation", request.OntologyId + ".Implementation." + NormalizeConceptName(symbol.Name, role.Role),
                JsonSerializer.Serialize(new { sourceSymbol = symbol.Id, role = role.Role }),
                "框架实现角色用于定位业务实现，不直接等同于业务类型或业务规则。", Confidence(role.Confidence), "pending", [evidenceItem.Id]));
        }

        foreach (var entryPoint in entryPoints.Where(item => item.Kind == "http_route").OrderBy(item => item.SymbolId, StringComparer.Ordinal))
        {
            if (!byId.TryGetValue(entryPoint.SymbolId, out var symbol))
            {
                continue;
            }
            var evidenceItem = EvidenceFor(symbol, "inferred", $"从 HTTP 路由入口观察到的业务流程实现候选：{symbol.Name}");
            AddCandidate(new BusinessOntologyCandidate("candidate:route:" + symbol.Id, "implementation", request.OntologyId + ".Route." + NormalizeConceptName(symbol.Name, "route"),
                JsonSerializer.Serialize(new { sourceSymbol = symbol.Id, kind = entryPoint.Kind, metadata = entryPoint.Metadata }),
                "HTTP 路由可能承载业务流程，但仅凭入口观察不足以确认生命周期或规则。", Confidence(symbol.Confidence), "pending", [evidenceItem.Id]));
        }

        foreach (var symbol in symbols.Where(IsFrontendCorroboration).OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            var evidenceItem = EvidenceFor(symbol, "presentational", $"从前端页面或表单观察到的领域佐证：{symbol.Name}");
            AddCandidate(new BusinessOntologyCandidate("candidate:frontend:" + symbol.Id, "corroboration", request.OntologyId + "." + NormalizeConceptName(symbol.Name, "frontend"),
                JsonSerializer.Serialize(new { sourceSymbol = symbol.Id, language = symbol.Language, path = symbol.Path }),
                "前端呈现只能补强候选，不单独确认业务概念、关系或规则。", Confidence(symbol.Confidence), "pending", [evidenceItem.Id]));
        }

        var input = new BusinessOntologyGenerationInput(
            request.OntologyId, request.GenerationId, request.SourceFingerprint, request.GeneratorVersion, request.CreatedAt,
            concepts, attributes, [], [], [], [], [], mappings, evidence.Values.OrderBy(item => item.Id, StringComparer.Ordinal).ToArray(),
            candidates.Values.OrderBy(item => item.Id, StringComparer.Ordinal).ToArray(), [], []);
        await store.ReplaceGenerationAsync(input, cancellationToken);
        return new BusinessOntologyDerivationResult(request.OntologyId, request.GenerationId, concepts.Count, attributes.Count, mappings.Count, candidates.Count, evidence.Count);
    }

    private async Task<IReadOnlyList<CodeObservation>> ReadSymbolsAsync(CancellationToken cancellationToken) =>
        (await om.Runtime.Store.RunAsync("""
            ?[id, repo, path, language, name, kind, start_line, end_line, parent_id, lang, resolver] :=
              *ck_symbol{symbol_id: id, file_id, name, kind, start_line, end_line, parent_id, lang, resolver},
              *ck_file{file_id, repo_id: repo, path, language}
            """, cancellationToken: cancellationToken)).Rows
            .Select(row => new CodeObservation(S(row, 0), S(row, 1), S(row, 2), S(row, 3), S(row, 4), S(row, 5), I(row, 6), I(row, 7), S(row, 8), S(row, 9), S(row, 10), 0.6))
            .ToArray();

    private async Task<IReadOnlyList<RoleObservation>> ReadRolesAsync(CancellationToken cancellationToken) =>
        (await om.Runtime.Store.RunAsync("""
            ?[symbol_id, role, confidence] := *ck_edge{from_id: symbol_id, to_id: role, kind: "SPRING_ROLE", confidence}
            """, cancellationToken: cancellationToken)).Rows
            .Select(row => new RoleObservation(S(row, 0), S(row, 1), D(row, 2))).ToArray();

    private async Task<IReadOnlyList<EntryPointObservation>> ReadEntryPointsAsync(CancellationToken cancellationToken) =>
        (await om.Runtime.Store.RunAsync("?[symbol_id, kind, metadata] := *ck_entry_point{symbol_id, kind, metadata}", cancellationToken: cancellationToken)).Rows
            .Select(row => new EntryPointObservation(S(row, 0), S(row, 1), S(row, 2))).ToArray();

    private static string CarrierRole(CodeObservation item, string? explicitRole)
    {
        if (explicitRole is not null && BusinessRoles.Contains(explicitRole))
        {
            return explicitRole;
        }
        if (explicitRole is not null
            && ImplementationRoles.Contains(explicitRole)
            && item.Kind is "class" or "record" or "interface")
        {
            return explicitRole;
        }
        if (!item.Language.Equals("java", StringComparison.OrdinalIgnoreCase)
            || item.Kind is not ("class" or "record"))
        {
            return "";
        }
        return item.Name switch
        {
            var name when EndsWithAny(name, "Dto", "DTO") => "carrier:dto",
            var name when EndsWithAny(name, "Entity") => "carrier:entity",
            var name when EndsWithAny(name, "Vo", "VO") => "carrier:vo",
            var name when EndsWithAny(name, "Request", "Req", "Query", "Save", "Update") => "carrier:request",
            var name when EndsWithAny(name, "Response", "Resp") => "carrier:response",
            var name when EndsWithAny(name, "Controller", "Service", "ServiceImpl", "Action", "ActionImpl") => "carrier:implementation",
            _ => "",
        };
    }

    private static bool HasCanonicalConceptSupport(IReadOnlyCollection<CarrierDraft> members) =>
        members.Any(item => item.CarrierRole == "domain:anchor")
        || (members.Any(IsDataOrDomainCarrier)
            && members.Select(item => CarrierRoleFamily(item.CarrierRole)).Distinct(StringComparer.Ordinal).Count() >= 2);

    private static bool IsDataOrDomainCarrier(CarrierDraft item) =>
        item.CarrierRole is "spring:role:entity" or "carrier:entity" or "domain:anchor";

    private static bool IsDomainAttributeCarrier(CarrierDraft item) =>
        item.CarrierRole is "spring:role:entity" or "carrier:entity" or "domain:anchor";

    private static string CarrierRoleFamily(string role) => role switch
    {
        "spring:role:dto" => "carrier:dto",
        "spring:role:entity" => "carrier:entity",
        "spring:role:vo" => "carrier:vo",
        "spring:role:controller" => "controller",
        "spring:role:service" => "service",
        "spring:role:repository" => "repository",
        "carrier:implementation" => "implementation",
        _ => role,
    };

    private static bool LooksLikeBusinessCarrier(CodeObservation item) =>
        item.Language.Equals("java", StringComparison.OrdinalIgnoreCase) && item.Kind is "class" or "record" &&
        CarrierRole(item, null).Length > 0;

    private static bool IsFrontendCorroboration(CodeObservation item) =>
        (item.Language.Equals("typescript", StringComparison.OrdinalIgnoreCase) || item.Language.Equals("tsx", StringComparison.OrdinalIgnoreCase) || item.Path.EndsWith(".tsx", StringComparison.OrdinalIgnoreCase)) &&
        (item.Name.EndsWith("Page", StringComparison.Ordinal) || item.Name.EndsWith("Form", StringComparison.Ordinal));

    private static string CanonicalFamilyKey(CarrierDraft item, IReadOnlyList<CarrierDraft> all)
    {
        if (item.LocalName.EndsWith("Infos", StringComparison.Ordinal)
            && all.Any(other => string.Equals(other.LocalName, item.LocalName[..^1], StringComparison.Ordinal)))
        {
            return item.LocalName[..^1];
        }
        if (item.LocalName.EndsWith("Info", StringComparison.Ordinal)
            && all.Any(other => string.Equals(other.LocalName, item.LocalName + "s", StringComparison.Ordinal)))
        {
            return item.LocalName;
        }
        return item.LocalName;
    }

    private static string NormalizeConceptName(string value, string carrierRole)
    {
        foreach (var suffix in new[]
        {
            "ServiceImpl", "ActionImpl", "Controller", "Service", "Action",
            "Response", "Request", "Entity", "Query", "Update", "Save", "Resp", "Req", "Dto", "DTO", "Vo", "VO",
        })
        {
            if (value.EndsWith(suffix, StringComparison.Ordinal) && value.Length > suffix.Length)
            {
                value = value[..^suffix.Length];
                break;
            }
        }
        var normalized = new string(value.Where(char.IsLetterOrDigit).ToArray()) switch
        {
            { Length: 0 } => "",
            var token => char.ToUpperInvariant(token[0]) + token[1..]
        };
        return normalized;
    }

    private static string LowerCamel(string value)
    {
        var normalized = new string(value.Where(char.IsLetterOrDigit).ToArray());
        return normalized.Length == 0 || !char.IsLetter(normalized[0])
            ? ""
            : char.ToLowerInvariant(normalized[0]) + normalized[1..];
    }

    private static bool IsBusinessAttributeName(string value)
    {
        if (value.Equals("serialVersionUID", StringComparison.Ordinal)
            || value.Equals("class", StringComparison.Ordinal)
            || value.Equals("logger", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var lower = value.ToLowerInvariant();
        if (lower.Contains("cache", StringComparison.Ordinal)
            || lower.Contains("lock", StringComparison.Ordinal)
            || lower.EndsWith("entitylist", StringComparison.Ordinal)
            || lower.EndsWith("entities", StringComparison.Ordinal)
            || lower.EndsWith("dtolist", StringComparison.Ordinal)
            || lower.EndsWith("volist", StringComparison.Ordinal)
            || lower.EndsWith("list", StringComparison.Ordinal) && lower.Contains("data", StringComparison.Ordinal))
        {
            return false;
        }
        return true;
    }

    private static string PascalToken(string value)
    {
        var parts = value.Split([':', '-', '_'], StringSplitOptions.RemoveEmptyEntries);
        return string.Concat(parts.Select(part =>
            part.Length == 0 ? "" : char.ToUpperInvariant(part[0]) + part[1..].ToLowerInvariant()));
    }

    private static string HashToken(string value) =>
        "H" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..12];

    private static bool EndsWithAny(string value, params string[] suffixes) =>
        suffixes.Any(suffix => value.EndsWith(suffix, StringComparison.Ordinal));

    private static string RoleLabel(string? role) => role switch
    {
        "spring:role:dto" => "DTO",
        "spring:role:entity" => "实体",
        "spring:role:vo" => "值对象",
        "carrier:dto" => "DTO",
        "carrier:entity" => "实体",
        "carrier:vo" => "值对象",
        "carrier:request" => "请求",
        "carrier:response" => "响应",
        "spring:role:controller" => "控制器",
        "spring:role:service" => "服务",
        "spring:role:repository" => "仓储",
        _ => "命名模式"
    };

    private static double Confidence(double value) => double.IsNaN(value) || value <= 0 ? 0.6 : Math.Min(1, value);
    private static string S(IReadOnlyList<JsonElement> row, int index) => row[index].ValueKind == JsonValueKind.String ? row[index].GetString() ?? "" : row[index].ToString();
    private static int I(IReadOnlyList<JsonElement> row, int index) => row[index].ValueKind == JsonValueKind.Number ? row[index].GetInt32() : 0;
    private static double D(IReadOnlyList<JsonElement> row, int index) => row[index].ValueKind == JsonValueKind.Number ? row[index].GetDouble() : 0;

    private sealed record CodeObservation(string Id, string Repository, string Path, string Language, string Name, string Kind, int StartLine, int EndLine, string ParentId, string Lang, string Resolver, double Confidence);
    private sealed record RoleObservation(string SymbolId, string Role, double Confidence);
    private sealed record EntryPointObservation(string SymbolId, string Kind, string Metadata);
    private sealed record CarrierDraft(CodeObservation Symbol, RoleObservation? Role, string CarrierRole, string LocalName);
}

using System.Text.Json;
using Cozo.DotNet.Om.Support;

namespace Cozo.DotNet.Om.CodeKnowledge;

internal static class CodeKnowledgeSchema
{
    internal const int SchemaVersion = 3;
    internal const string SemanticClaimRelation = "ck_semantic_claim";

    // v3 relations. v2 established the typed graph; v3 adds source semantic observations.
    // ck_symbol value columns beyond the v1 set carry defaults so partial (subset :put)
    // writers remain valid; the batch write path (T1.2) fills all columns explicitly.
    private static readonly string[] Creates =
    [
        ":create ck_meta {key => value}",
        ":create ck_repo {repo_id => root_path, name, commit}",
        ":create ck_file {file_id => repo_id, path, language, hash, updated_at}",
        """
        :create ck_symbol {symbol_id =>
            file_id, name, kind,
            start_line default 0, end_line default 0, signature default "",
            parent_id default "", lang default "", visibility default "",
            exported default false, sym_key default "", doc_id default "",
            resolver default ""}
        """,
        """
        :create ck_edge {from_id, to_id, kind, file_id, line =>
            confidence default 0.0, resolver default "", evidence default ""}
        """,
        """:create ck_entry_point {symbol_id, kind => metadata default ""}""",
        ":create ck_community {community_id => label, cohesion, symbol_count, algo}",
        ":create ck_member {symbol_id => community_id}",
        ":create ck_process {process_id => name, entry_symbol_id, entry_kind, process_type, step_count}",
        ":create ck_process_step {process_id, step => symbol_id, via_kind}",
        ":create ck_doc_block {doc_id => file_id, anchor, text, hash, updated_at}",
        ":create ck_concept {concept_id => name, description}",
        ":create ck_diagnostic {diagnostic_id => target_id, kind, message, severity}",
        ":create ck_owner {target_id, owner => kind}",
        ":create ck_wiki_page {page_id => title, source_file_ids, symbol_ids, doc_ids}",
        // add-llm-wiki-depa-ontology track (design §4.2, decisions #2): low-fidelity summary of
        // out-of-repo calls. This was add-only in v2 and remains part of the v3 rebuild schema.
        // Only the first call site is stored as evidence; count carries the rest.
        """
        :create ck_external_call {caller_id, target_key =>
            count default 0, category default "",
            first_file_id default "", first_line default 0,
            resolver default ""}
        """,
        """
        :create ck_semantic_claim {claim_id =>
            subject_id, kind, payload_json, file_id, start_line, end_line,
            confidence, resolver, evidence}
        """,
    ];

    internal static async Task InitAsync(CozoOm om, bool reindex, CancellationToken cancellationToken)
    {
        var existing = await ListCkRelationsAsync(om, cancellationToken);
        var hasMeta = existing.Contains("ck_meta");
        var version = hasMeta ? await ReadSchemaVersionAsync(om, cancellationToken) : null;
        var hasExistingSchema = existing.Count > 0;
        var requiresReindex = hasExistingSchema && (!hasMeta || version != SchemaVersion);

        if (requiresReindex && !reindex)
        {
            throw new CozoException(
                $"CodeKnowledge schema version {FormatVersion(version, hasMeta)} is incompatible with required version {SchemaVersion}. " +
                $"The required {SemanticClaimRelation} source-observation relation is installed only by the indexing schema lifecycle. " +
                "CodeKnowledge uses rebuild-style migration because index data is recomputable; existing data is not migrated in place. " +
                "Re-run initialization with the reindex option (InitCodeKnowledgeAsync(reindex: true) " +
                "or the --reindex flag of the indexing tool) to drop the existing ck_* relations, " +
                $"recreate schema v{SchemaVersion}, and then re-index the repository.");
        }

        if (reindex)
        {
            // Search indexing creates this FTS index outside the relation schema. Cozo requires
            // it to be removed before its owning stored relation can be rebuilt.
            if (existing.Contains("ck_search_text"))
            {
                try
                {
                    await om.Runtime.Store.RunAsync(
                        "::fts drop ck_search_text:fts",
                        cancellationToken: cancellationToken);
                }
                catch (CozoException ex) when (IsMissingIndex(ex))
                {
                }
            }
            foreach (var relation in existing)
            {
                await om.Runtime.Store.RunAsync($"::remove {relation}", cancellationToken: cancellationToken);
            }
        }

        foreach (var create in Creates)
        {
            try
            {
                await om.Runtime.Store.RunAsync(create, cancellationToken: cancellationToken);
            }
            catch (CozoException ex) when (IsCreateConflict(ex))
            {
            }
        }

        await om.Runtime.Store.RunAsync(
            """
            ?[key, value] <- [["schema_version", $version]]
            :put ck_meta {key => value}
            """,
            new Dictionary<string, object?> { ["version"] = SchemaVersion },
            cancellationToken: cancellationToken);
    }

    internal static async Task<CodeSemanticClaimPreflight> PreflightSemanticClaimsAsync(
        CozoOm om,
        CancellationToken cancellationToken)
    {
        var existing = await ListCkRelationsAsync(om, cancellationToken);
        var hasMeta = existing.Contains("ck_meta");
        var version = hasMeta ? await ReadSchemaVersionAsync(om, cancellationToken) : null;
        var ready = version == SchemaVersion && existing.Contains(SemanticClaimRelation);
        if (ready)
        {
            return new CodeSemanticClaimPreflight(true, version, "");
        }

        return new CodeSemanticClaimPreflight(
            false,
            version,
            $"CodeKnowledge semantic claims are unavailable: required schema v{SchemaVersion} relation " +
            $"{SemanticClaimRelation} was not found. Reindex required; run repository indexing with " +
            "--reindex. Semantic projection does not initialize or rewrite CodeKnowledge implicitly.");
    }

    private static async Task<IReadOnlyList<string>> ListCkRelationsAsync(CozoOm om, CancellationToken cancellationToken)
    {
        var result = await om.Runtime.Store.RunAsync("::relations", cancellationToken: cancellationToken);
        return result.Rows
            .Select(row => row.Count > 0 && row[0].ValueKind == JsonValueKind.String ? row[0].GetString() : null)
            .Where(name => name is not null
                && name.StartsWith("ck_", StringComparison.Ordinal)
                // ::relations also exposes attached index names such as ck_search_text:fts.
                // Schema lifecycle owns stored relations, while index lifecycle drops its own indexes.
                && !name.Contains(':', StringComparison.Ordinal))
            .Select(name => name!)
            .ToArray();
    }

    private static async Task<int?> ReadSchemaVersionAsync(CozoOm om, CancellationToken cancellationToken)
    {
        var result = await om.Runtime.Store.RunAsync(
            """?[value] := *ck_meta{ key: "schema_version", value }""",
            cancellationToken: cancellationToken);
        if (result.Rows.Count == 0)
        {
            return null;
        }

        var value = result.Rows[0][0];
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetInt32(),
            JsonValueKind.String when int.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    private static bool IsCreateConflict(CozoException ex)
    {
        var text = $"{ex.Message}\n{ex.RawResponse}".ToLowerInvariant();
        return text.Contains("conflict", StringComparison.Ordinal) ||
               text.Contains("already", StringComparison.Ordinal) ||
               text.Contains("exists", StringComparison.Ordinal);
    }

    private static bool IsMissingIndex(CozoException ex)
    {
        var text = $"{ex.Message}\n{ex.RawResponse}".ToLowerInvariant();
        return text.Contains("not found", StringComparison.Ordinal)
            || text.Contains("does not exist", StringComparison.Ordinal)
            || text.Contains("unknown index", StringComparison.Ordinal);
    }

    private static string FormatVersion(int? version, bool hasMeta) =>
        hasMeta ? version?.ToString() ?? "unknown" : "legacy/unknown";
}

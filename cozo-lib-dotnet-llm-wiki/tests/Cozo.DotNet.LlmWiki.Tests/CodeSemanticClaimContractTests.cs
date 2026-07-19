using System.Text.Json;
using Cozo.DotNet;
using Cozo.DotNet.LlmWiki.Core;
using Cozo.DotNet.LlmWiki.Indexing;
using Cozo.DotNet.Om;
using Cozo.DotNet.Om.CodeKnowledge;
using Cozo.DotNet.Om.Contracts;
using Cozo.DotNet.Om.Support;

namespace Cozo.DotNet.LlmWiki.Tests;

internal static class CodeSemanticClaimContractTests
{
    public static async Task RunAsync(Action<bool, string> assert)
    {
        await AssertReadOnlyPreflightAndExplicitMigrationAsync(assert);
        await AssertWriteAndFileCleanupContractAsync(assert);
    }

    private static async Task AssertReadOnlyPreflightAndExplicitMigrationAsync(Action<bool, string> assert)
    {
        using var db = new CozoDb("mem", "");
        var innerStore = new CozoDbOmStore(db);
        await innerStore.RunAsync(":create depa_semantic_claim_sentinel {id => value}");
        await innerStore.RunAsync(
            """
            ?[id, value] <- [["sentinel", "unchanged"]]
            :put depa_semantic_claim_sentinel {id => value}
            """);
        var guardedStore = new ForbiddenRelationStore(innerStore, "depa_");
        var om = new CozoOm(guardedStore);

        var before = await RelationNamesAsync(om);
        var emptyPreflight = await om.PreflightSemanticClaimsAsync();
        var after = await RelationNamesAsync(om);
        assert(!emptyPreflight.Ready
               && emptyPreflight.Diagnostic.Contains("Reindex required", StringComparison.Ordinal)
               && before.SetEquals(after)
               && guardedStore.ForbiddenAccessCount == 0,
            "semantic-claim preflight without ck schema must be read-only, avoid depa_* queries, and return an actionable reindex diagnostic");

        await om.Runtime.Store.RunAsync(":create ck_meta {key => value}");
        await om.Runtime.Store.RunAsync(
            """
            ?[key, value] <- [["schema_version", 2]]
            :put ck_meta {key => value}
            """);

        var v2Preflight = await om.PreflightSemanticClaimsAsync();
        assert(!v2Preflight.Ready && v2Preflight.SchemaVersion == 2,
            "schema v2 must fail semantic-claim preflight without being mutated");

        Exception? migrationError = null;
        try
        {
            await om.InitCodeKnowledgeAsync();
        }
        catch (Exception ex)
        {
            migrationError = ex;
        }

        assert(migrationError is not null
               && migrationError.Message.Contains("reindex", StringComparison.OrdinalIgnoreCase)
               && migrationError.Message.Contains("version 3", StringComparison.Ordinal),
            "initializing over schema v2 without reindex must fail with an explicit schema-v3 rebuild instruction");

        await om.InitCodeKnowledgeAsync(reindex: true);
        var ready = await om.PreflightSemanticClaimsAsync();
        var sentinel = await innerStore.RunAsync(
            """?[value] := *depa_semantic_claim_sentinel{id: "sentinel", value}""");
        assert(ready is { Ready: true, SchemaVersion: 3 } && ready.Diagnostic.Length == 0,
            "explicit reindex must install schema v3 and ck_semantic_claim");
        assert(guardedStore.ForbiddenAccessCount == 0
               && sentinel.Rows.Count == 1
               && sentinel.Rows[0][0].GetString() == "unchanged",
            "CodeKnowledge preflight and schema rebuild must neither query nor mutate depa_* data");
    }

    private static async Task AssertWriteAndFileCleanupContractAsync(Action<bool, string> assert)
    {
        using var db = new CozoDb("mem", "");
        var om = new CozoOm(db);
        await om.InitCodeKnowledgeAsync();

        const string repoId = "repo:semantic-contract";
        const string fileId = "file:repo:semantic-contract:Record.java";
        const string symbolId = "symbol:file:repo:semantic-contract:Record.java:Record";
        const string payload = """{"z":1,"a":{"y":2,"b":1}}""";
        var claimId = CodeSemanticClaimIdentity.Create(
            fileId,
            CodeSemanticClaimKinds.TypedReference,
            payload,
            3,
            3);
        var result = await om.IndexCodeKnowledgeAsync(new CodeKnowledgeBatch(
            Repositories: [new CodeRepositoryFact(repoId, "/fixture", "fixture")],
            Files: [new CodeFileFact(fileId, repoId, "Record.java", "java")],
            Symbols: [new CodeSymbolFact(symbolId, fileId, "Record", "class", 1, 8, Resolver: "treesitter")],
            SemanticClaims:
            [
                new CodeSemanticClaimFact(
                    claimId,
                    symbolId,
                    CodeSemanticClaimKinds.TypedReference,
                    payload,
                    fileId,
                    3,
                    3,
                    0.95,
                    "treesitter",
                    "Record.java:3")
            ]));

        var stored = await om.Runtime.Store.RunAsync(
            """
            ?[kind, payload_json, file_id, start_line, end_line, confidence, resolver, evidence] :=
              *ck_semantic_claim{
                claim_id: $claim_id,
                kind,
                payload_json,
                file_id,
                start_line,
                end_line,
                confidence,
                resolver,
                evidence
              }
            """,
            new Dictionary<string, object?> { ["claim_id"] = claimId });
        assert(result.SemanticClaims == 1 && stored.Rows.Count == 1,
            "batch write must report and persist semantic claims");
        assert(stored.Rows[0][1].GetString() == """{"a":{"b":1,"y":2},"z":1}""",
            "semantic claim payloads must be canonical JSON with recursively sorted object keys");

        Exception? invalidKindError = null;
        try
        {
            await om.IndexCodeKnowledgeAsync(new CodeKnowledgeBatch(
                SemanticClaims:
                [
                    new CodeSemanticClaimFact(
                        "semantic:invalid",
                        symbolId,
                        "invented_kind",
                        "{}",
                        fileId,
                        3,
                        3,
                        0.5,
                        "treesitter",
                        "Record.java:3")
                ]));
        }
        catch (ArgumentException ex)
        {
            invalidKindError = ex;
        }

        var invalidRows = await om.Runtime.Store.RunAsync(
            """?[claim_id] := *ck_semantic_claim{ claim_id }, claim_id = "semantic:invalid" """);
        assert(invalidKindError is not null && invalidRows.Rows.Count == 0,
            "the write contract must reject kinds outside the closed vocabulary before opening a write transaction");

        var root = Path.Combine(Path.GetTempPath(), $"semantic-claim-cleanup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var sourcePath = Path.Combine(root, "Changed.java");
            await File.WriteAllTextAsync(sourcePath, "package fixture;\npublic class Changed { int value; }\n");
            var request = new RepositoryIndexRequest(
                root,
                RepositoryId: "repo:semantic-cleanup",
                UseGitIgnore: false);
            var indexer = new RepositoryIndexer();
            await indexer.IndexAsync(om, request);

            var anchors = await om.Runtime.Store.RunAsync(
                """
                ?[file_id, symbol_id] :=
                  *ck_file{ file_id, repo_id: "repo:semantic-cleanup", path: "Changed.java" },
                  *ck_symbol{ symbol_id, file_id, kind: "class" }
                """);
            assert(anchors.Rows.Count == 1, "cleanup fixture must produce one owning Java class symbol");
            var changedFileId = anchors.Rows[0][0].GetString()!;
            var changedSymbolId = anchors.Rows[0][1].GetString()!;
            const string stalePayload = """{"field":"status"}""";
            var staleClaimId = CodeSemanticClaimIdentity.Create(
                changedFileId,
                CodeSemanticClaimKinds.StateField,
                stalePayload,
                2,
                2);

            await om.IndexCodeKnowledgeAsync(new CodeKnowledgeBatch(
                SemanticClaims:
                [
                    new CodeSemanticClaimFact(
                        staleClaimId,
                        changedSymbolId,
                        CodeSemanticClaimKinds.StateField,
                        stalePayload,
                        changedFileId,
                        2,
                        2,
                        0.9,
                        "treesitter",
                        "Changed.java:2")
                ]));

            await File.WriteAllTextAsync(sourcePath, "package fixture;\npublic class Changed { int changedValue; }\n");
            var incremental = await indexer.IndexAsync(om, request);
            var staleRows = await om.Runtime.Store.RunAsync(
                """?[claim_id] := *ck_semantic_claim{ claim_id }, claim_id = $claim_id""",
                new Dictionary<string, object?> { ["claim_id"] = staleClaimId });
            assert(incremental.IncrementalUsed && incremental.ChangedFiles == 1 && staleRows.Rows.Count == 0,
                "incremental replacement of a file must remove its old semantic claims in the file cleanup transaction");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task<HashSet<string>> RelationNamesAsync(CozoOm om)
    {
        var result = await om.Runtime.Store.RunAsync("::relations");
        return result.Rows
            .Where(row => row.Count > 0 && row[0].ValueKind == JsonValueKind.String)
            .Select(row => row[0].GetString()!)
            .ToHashSet(StringComparer.Ordinal);
    }
}

internal sealed class ForbiddenRelationStore(
    ICozoOmStore inner,
    string forbiddenRelationPrefix) : ICozoOmStore
{
    public int ForbiddenAccessCount { get; private set; }

    public Task<OmQueryResult> RunAsync(
        string script,
        object? parameters = null,
        bool immutable = false,
        CancellationToken cancellationToken = default)
    {
        RejectForbiddenAccess(script);
        return inner.RunAsync(script, parameters, immutable, cancellationToken);
    }

    public async Task<ICozoOmTransaction> BeginTransactionAsync(
        bool write = true,
        CancellationToken cancellationToken = default) =>
        new GuardedTransaction(
            await inner.BeginTransactionAsync(write, cancellationToken),
            RejectForbiddenAccess);

    private void RejectForbiddenAccess(string script)
    {
        if (!script.Contains(forbiddenRelationPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        ForbiddenAccessCount++;
        throw new InvalidOperationException(
            $"Unexpected access to relation prefix '{forbiddenRelationPrefix}'.");
    }

    private sealed class GuardedTransaction(
        ICozoOmTransaction inner,
        Action<string> rejectForbiddenAccess) : ICozoOmTransaction
    {
        public Task<OmQueryResult> RunAsync(
            string script,
            object? parameters = null,
            bool immutable = false,
            CancellationToken cancellationToken = default)
        {
            rejectForbiddenAccess(script);
            return inner.RunAsync(script, parameters, immutable, cancellationToken);
        }

        public async Task<ICozoOmTransaction> BeginTransactionAsync(
            bool write = true,
            CancellationToken cancellationToken = default) =>
            new GuardedTransaction(
                await inner.BeginTransactionAsync(write, cancellationToken),
                rejectForbiddenAccess);

        public Task CommitAsync(CancellationToken cancellationToken = default) =>
            inner.CommitAsync(cancellationToken);

        public Task AbortAsync(CancellationToken cancellationToken = default) =>
            inner.AbortAsync(cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}

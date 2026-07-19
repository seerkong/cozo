# Design: Codex CLI as a bounded `ILlmClient`

## 1. Integration Point

`CodexCliLlmClient` lives in `Cozo.DotNet.LlmWiki.LlmClient` and implements the existing `ILlmClient` contract. The semantic projector continues to depend only on that contract:

```text
depa-wiki ontology derive-semantics --mode assisted
  -> LlmClientFactory.FromEnvironment()
  -> CodexCliLlmClient.CompleteAsync()
  -> codex exec (isolated process)
  -> OntologySemanticAssistedProposalClient local validation
  -> onto_candidate(status=pending)
  -> explicit review -> optional materialization/export
```

This is a transport addition, not a second ontology inference pipeline. The existing `OntologySemanticAssistedProposalClient` remains responsible for candidate count/input/output bounds, strict proposal JSON parsing, canonical IDs, allowed concepts, evidence references, retry, and all-or-nothing semantics.

## 2. Configuration and Availability

| Setting | Meaning |
|---|---|
| `DEPA_WIKI_LLM_PROVIDER=codex-cli` | Select the CLI provider. |
| `DEPA_WIKI_CODEX_CLI_PATH` | Optional command path; default resolves `codex` from `PATH`. |
| `DEPA_WIKI_LLM_MODEL` | Optional Codex model override; absent defaults to `gpt-5.6-terra`. |
| `DEPA_WIKI_LLM_TIMEOUT_SECONDS` | Optional positive timeout; default remains 120 seconds. |

The CLI provider does not require `DEPA_WIKI_LLM_API_KEY`. Availability verifies only locally knowable facts: a nonempty safe command configuration and a resolvable executable. Login, account, model, or network failures happen at completion time and are converted to a bounded `LlmException`; no startup probe may invoke a model.

## 3. Child Process Contract

For every completion the client creates one private temporary directory containing an output JSON schema and a final-message output path. It launches without a shell or inherited command text:

```text
codex exec --ephemeral --skip-git-repo-check --ignore-user-config --ignore-rules \
  --sandbox read-only --cd <empty-temp-dir> \
  --output-schema <temp-schema.json> --output-last-message <temp-final.json> \
  [--model <configured-model>] -
```

The stdin body is a local wrapper around the existing system and user prompts. It labels the system contract and evidence as data, asks for exactly the schema-constrained final JSON, and never adds a target repository path. `ProcessStartInfo.ArgumentList` is mandatory; no shell interpolation is allowed.

The client does not pass `--add-dir`, does not use the target repository as cwd, and does not read a prompt file from it. `--ignore-user-config` and `--ignore-rules` prevent incidental user configuration or repository instructions from changing the task. The empty cwd and read-only sandbox reduce accidental project access, but the design does not falsely claim host-wide read isolation from a locally trusted CLI.

The child process starts from an explicit allowlist, not the host environment. It retains only non-secret runtime values required by an installed CLI or OS launcher (`HOME`, `CODEX_HOME`, `PATH`, locale, temp and platform launcher variables); it MUST NOT inherit `DEPA_WIKI_LLM_API_KEY`, `OPENAI_API_KEY`, `ANTHROPIC_API_KEY`, proxy credentials, arbitrary `CODEX_*` values, or unrelated host variables. Codex authentication is expected through its local credential store under `HOME`/`CODEX_HOME`, not a copied API-key environment. The temporary directory is set to Unix `0700`; on Windows its ACL is protected and grants only the current user. Failure to establish that privacy boundary fails before launching the CLI.

## 4. Bounds, Errors, and Cleanup

- Create a linked cancellation token with the configured timeout. On cancellation or timeout, kill the complete child process tree and await exit.
- Drain stdout/stderr asynchronously to avoid pipe deadlock, retaining only bounded diagnostic tails. Never embed full prompt, stdin, credentials, or response body into exception messages.
- Require a successful exit and a nonempty final-message file within the existing assisted response byte bound. Treat absent, oversized, unreadable, or malformed final output as `LlmException`.
- Delete the private temporary directory in `finally`, including failure and cancellation paths.
- Return `LlmCompletion(finalJson, effectiveModel)`; the effective model is request override, configured model, or `gpt-5.6-terra`, in that order. The local semantic layer, not this adapter, decides whether the text is valid ontology proposal JSON.

## 5. Output Schema

`--output-schema` uses a local, closed envelope schema requiring only `schemaVersion` and `candidates`, with no additional top-level properties. Candidate semantics remain intentionally validated by the existing stricter local parser, so the CLI schema is defense in depth rather than a duplicate, divergent source of truth.

## 6. Testing Strategy

Tests use a per-test executable shim, never `codex` itself. The shim records cwd, arguments and stdin, writes controlled final output or simulates failure/sleep. Coverage includes:

- environment factory selection, path resolution and unavailable reasons;
- exact non-shell process flags, isolated cwd, no repository/add-dir argument, model forwarding, prompt envelope and temporary cleanup;
- successful completion, nonzero exit, timeout, cancellation, missing/empty output and bounded output/stderr diagnostics;
- an inherited secret probe is absent from the child environment, only the explicit test runtime variables are passed, and owner-only temporary directory creation is verified on Unix;
- semantic integration that proves valid CLI JSON produces only pending, locally evidence-backed candidates and invalid/hallucinated output is rejected before writes.

An optional manual smoke command may be documented for an operator with an authenticated Codex CLI. It is not part of CI and must use a disposable copied database/output location.

# Proposal: Codex CLI assisted semantic recognition

## Goal

Allow the existing business-ontology `assisted` projection to obtain bounded semantic proposals through the locally installed Codex CLI, without changing the meaning of source evidence, ontology candidates, review, or export.

## Why

The current assisted path accepts an injected `ILlmClient`, but the built-in factory only creates OpenAI-compatible and Anthropic HTTP clients. The user wants to use their authenticated local `codex` command to help interpret business rules, relations, and lifecycle evidence. A first-class provider makes that path operable from the existing `depa-wiki ontology derive-semantics --mode assisted` command.

## Scope

- Add a `codex-cli` implementation of `ILlmClient` in `Cozo.DotNet.LlmWiki.LlmClient`.
- Select it through `DEPA_WIKI_LLM_PROVIDER=codex-cli`, with an optional executable path, `gpt-5.6-terra` as the default model, and existing timeout configuration.
- Run `codex exec` through a non-shell, bounded, cancellable process adapter in an empty temporary working directory.
- Seal the child environment to a documented non-secret allowlist and create an owner-private temporary directory before the CLI starts.
- Use CLI output-schema and a final-message file as defense in depth; retain existing local proposal validation as the authority.
- Wire the existing semantic CLI through the factory, add fake-executable tests, and document safe invocation.

## Non-Goals

- No direct access by Codex CLI to a target repository, `.depa-wiki` database, or export directory through cwd/add-dir configuration.
- No new `onto_*` schema, no automatic candidate acceptance, no review bypass, and no change to the XML DSL.
- No real model/network invocation in automated tests.
- No attempt to make a local CLI sandbox an absolute host-level confidentiality boundary.

## Success Criteria

1. A configured local executable produces an available `ILlmClient` without HTTP API key configuration; an unavailable executable degrades predictably.
2. Tests prove the child process receives only the fixed argument contract, an isolated cwd and stdin prompt; timeout/cancellation/nonzero exit/oversize output clean up without leaking input content.
3. An end-to-end fixture sends a Codex-shaped strict JSON reply through `derive-semantics --mode assisted` and proves only evidence-backed `pending` candidates result.
4. Focused LLM client and assisted semantic tests pass without a real Codex account; CLI usage documents the opt-in runtime configuration.

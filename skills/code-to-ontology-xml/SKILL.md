---
name: code-to-ontology-xml
description: Read revision-pinned application source and author a BO-first, evidence-material-backed ontology XML bundle. Use when interpreting business objects, associations, processes, policies, events, local behaviour, or runtime extension points from real code.
---

# Code To BO-First Ontology XML

Read `../ontology-xml-dsl/SKILL.md` before authoring. It owns the grammar.

1. Pin repository, revision, scope, exclusions, and expected business areas.
2. Read source and trace representative flows before interpreting anything. Deterministic inventories, `ck_*`, routes, names, and topology only accelerate navigation.
3. Store revision-pinned code and document excerpts directly inside `Evidence` nodes; source metadata stays on attributes. Preserve raw observation and inferred interpretation separately.
4. Organize the result BO-first: each `BusinessObject.xml` indexes identity, properties, and local members; put cross-BO associations, processes, policies, and events in their dedicated top-level concepts.
5. Perform an explicit action-coverage sweep per BO across command endpoints, service methods, callbacks, and writes. Record an actionless conclusion only after this sweep; otherwise emit an action-coverage gap.
6. Convert arbitrary callback code into an `ExtensionPoint` plus a target `RuntimeBinding`, never into portable XML. Emit `Portable` only for the closed declarative subset.
7. Stage, validate, generate, and publish only after evidence/material and FQN closure pass. Default new interpretations to hypothesis and preserve unresolved gaps.

Never use existing ontology output as semantic evidence or silently merge source paths, symbols, or naming guesses into business claims.

For an ExtensionPoint, write concise free-text `InternalLogic` from inspected evidence; do not force unknown or complex implementation flow into a synthetic XML control-flow grammar.

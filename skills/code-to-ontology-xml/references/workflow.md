# Source Reading And Authoring Workflow

This workflow makes direct model understanding of real code the semantic recognition path. Deterministic inventory and `ck_*` observations may improve navigation and auditability, but they do not interpret business meaning, replace source reading, or authorize `accepted` semantics.

## Contents

1. Establish scope and revisions.
2. Write a read plan.
3. Read source in layers.
4. Use observation accelerators safely.
5. Maintain the evidence inventory.
6. Maintain the candidate ledger.
7. Reconcile cross-repository identity.
8. Apply rule evidence gates.
9. Apply lifecycle evidence gates.
10. Decide acceptance.

## 1. Establish Scope And Revisions

Create a run-local scope record before interpretation:

- stable repository key, local root, language, and immutable revision for every repository;
- included packages, modules, paths, and bounded contexts;
- excluded generated code, dependencies, vendor trees, build output, examples, and unrelated applications;
- all `depa_*` stores, exports, sidecars, skills, and ontology artifacts as hard exclusions;
- requested ontology families and known business entry points;
- known cross-repository boundaries such as HTTP contracts, events, schemas, shared identifiers, and database ownership;
- dirty-worktree treatment; uncommitted content is a separate content-hashed snapshot, never part of the pinned commit by implication; and
- explicit limitations, inaccessible components, and completion expectations.

Do not use current application ontology output to choose concepts, expected counts, names, or acceptance outcomes. The target is the selected source, not an existing generated result.

## Output Location

Create a fresh staging bundle outside existing ontology output directories. The staging root contains `ontology.xml` and its `types/`, `relations/`, `rules/`, `lifecycles/`, `mappings/`, `evidence/`, and `generation/` modules. After strict XML, source fact, coverage, and evidence-anchor gates pass, publish it to the caller-selected `cozo-ontology/<version>/ontology/<ontology-id>/` destination. Do not read an existing ontology bundle, including `cozo-ontology/baseline/ontology/`, as a modeling input, sample, fixture, expected result, or oracle.

## 2. Write A Read Plan

The read plan is a coverage commitment, not a list of files already known to be important. For each repository and bounded context, record:

- orientation files: build manifests, module roots, routing, dependency injection, schemas, migrations, and configuration that reveal ownership;
- domain-bearing declarations: entities, value objects, contracts, persistence models, events, commands, state vocabularies, and shared identifiers;
- business entry points: routes, controllers, consumers, jobs, UI actions, and integration adapters;
- vertical slices to trace from entry point through orchestration, domain logic, persistence, events, and response or presentation;
- rule paths to inspect, including success, rejection, authorization, validation, and transaction boundaries;
- lifecycle paths to inspect, including creation, state mutation, transition guards, side effects, and terminal behavior;
- cross-repository joins to trace on both sides; and
- expected evidence gaps or follow-up reads.

Prioritize by business criticality and semantic risk. Update the plan when source reading discovers a new dispatcher, indirect call, generated client, event consumer, mapper, or persistence boundary. A file is covered only after it was opened in context or reached through a traced path; inventory enumeration alone does not count as reading.

## 3. Read Source In Layers

Read enough implementation to understand behavior, not only declarations.

### Layer A: System Orientation

Map repository responsibilities, module boundaries, entry points, dependency direction, persistence ownership, integration boundaries, and generated-code boundaries. Identify where business behavior can actually be enforced.

### Layer B: Domain Vocabulary And State

Read domain-bearing types, contracts, schemas, tables, migrations, state vocabularies, and tests. Distinguish business identity and state from DTO duplication, framework roles, view models, transport wrappers, and incidental fields.

### Layer C: Vertical Call Chains

Trace representative entry points into their concrete callees. Follow controller or handler, service or use case, domain checks, repositories or mappers, SQL or storage constraints, emitted events, and returned contracts. Resolve interfaces and indirection far enough to locate the real behavior.

For frontend paths, trace user action, local validation or visibility, API client, request and response identity, state update, and navigation. Frontend evidence remains presentational unless the backend or persisted layer independently enforces the same claim.

### Layer D: Business Processes

Reconstruct end-to-end flows across files and repositories: initiating actor, preconditions, commands, state changes, related objects, rejection paths, side effects, events, and terminal outcomes. Compare alternate paths and asynchronous continuations. This layer is required for rules and lifecycles.

### Layer E: Contradictions And Negative Paths

Read tests, guards, exception paths, database constraints, retries, compensations, feature flags, and stale or unused implementations. Record conflicts rather than silently choosing the most convenient source.

After each layer, update the read plan, evidence inventory, candidate ledger, and unresolved questions.

## 4. Use Observation Accelerators Safely

Direct source inspection is always the primary input. Optional accelerators may be used after scope is pinned:

- deterministic inventory to enumerate likely symbols, routes, state writes, rule signals, and lifecycle signals;
- `ck_*` facts that resolve to the selected repository and revision; and
- text, symbol, reference, and call-hierarchy search used to navigate to real source.

For the current Java and TypeScript inventory helper:

```bash
bun run skills/code-to-ontology-xml/scripts/inventory-code-domain.ts \
  --java-repo <java-root> \
  --typescript-repo <typescript-root> \
  --java-key <stable-java-key> \
  --typescript-key <stable-typescript-key> \
  --java-revision <immutable-revision> \
  --typescript-revision <immutable-revision> \
  --out <run-dir>/domain-inventory.json
```

Validate the resulting inventory, or any same-contract code fact packet, before using its observations:

```bash
bun run skills/code-to-ontology-xml/scripts/validate-code-fact-packet.ts \
  <run-dir>/domain-inventory.json \
  --report <run-dir>/code-fact-validation-report.json
```

Treat every accelerator result as an observation pointer. Reopen its source anchor and inspect relevant surrounding code before using it as semantic evidence. Reconstruct the call or process context when the observation concerns a rule, state change, relation, or cross-repository identity.

Reject or quarantine an observation when its repository, revision, resolver, symbol, or source anchor is missing; the anchor cannot be reopened; it points into excluded scope; or the source contradicts the observation. Tool labels such as `rule-signal` and `lifecycle-signal` are search hints, not semantic declarations.

No deterministic tool may:

- name or accept an ontology concept by itself;
- assign a bounded context from topology alone;
- promote evidence grade;
- decide that two repository objects share one business identity;
- infer a complete rule or lifecycle from one structural signal; or
- change a candidate from `hypothesis` to `accepted`.

## 5. Maintain The Evidence Inventory

Record each observation with stable repository key, immutable revision, repository-relative path, symbol or route identity, line range when available, resolver, raw observation summary, evidence grade, and confidence.

Grades follow `ontology-xml-dsl`. Keep the source's actual authority:

- persisted constraints and designated records may be authoritative;
- backend rejection or prevention may be enforced;
- APIs, schemas, DTOs, and protocols may be contractual;
- UI visibility and interaction remain presentational; and
- names, topology, inventory signals, unresolved calls, and model interpretations remain inferred.

Reading more files can increase confidence without raising grade. Preserve contradictory evidence as separate records.

## 6. Maintain The Candidate Ledger

The model writes and owns the candidate ledger. Create one entry for every proposed type, mixin, attribute family, relation, rule, lifecycle, implementation mapping, alias, or migration. Include:

- stable candidate ID, semantic family, proposed identity, and bounded context;
- plain-language meaning and why it matters to a business process;
- source observations and traced call or process paths;
- strongest evidence grade plus conflicting evidence;
- cross-repository identity links, if any;
- proposed status: `hypothesis` by default;
- disposition: `mapped`, `rejected`, or `unresolved`;
- rejection or unresolved reason;
- required follow-up reads and review decision; and
- projected ontology and evidence references when mapped.

Do not silently drop inventory signals or model-discovered candidates. Duplicate code representations normally share one candidate plus implementation mappings. Similar names are insufficient to merge, and different names are insufficient to split.

Deterministic coverage tools may verify ledger references and totals. They do not decide candidate meaning, disposition, status, or acceptance.

## 7. Reconcile Cross-Repository Identity

Compare candidates inside a bounded context only after reading the relevant code in every participating repository.

Merge representations only when the traced source demonstrates at least one durable identity bridge:

- shared or generated request and response contract;
- route plus compatible payload identity;
- stable identifier propagated across the boundary;
- event schema and correlation identity;
- persistence or mapping declaration tying representations to one record; or
- an already reviewed implementation mapping whose evidence still resolves.

Check lifecycle and rule compatibility before merging. Keep separate candidates for homonyms, incompatible identities, different lifecycle ownership, or context-specific meanings. When sources disagree, retain both observations and keep the semantic decision unresolved or hypothetical.

## 8. Apply Rule Evidence Gates

A rule candidate requires a scoped business condition and a required outcome, rejection, invariant, cardinality, uniqueness constraint, or related-object requirement.

To recommend `accepted`, the model must trace the relevant control path and identify:

- the domain scope and participating concepts;
- the exact condition and required result;
- the enforcing or authoritative location;
- the success and violation behavior;
- indirect callees, transaction or persistence effects, and bypass paths; and
- evidence strong enough for the claim.

Names, comments, routes, inventory signals, UI conditions, and tests without an enforcing implementation may discover a rule but cannot accept it. Ambiguous calls, presentation-only behavior, incomplete branches, or unsupported expression semantics remain hypotheses. XML projection must use only the rule vocabulary routed by `ontology-xml-dsl`; never paste host-language conditions into XML.

## 9. Apply Lifecycle Evidence Gates

A lifecycle candidate requires a stable subject, state property, state vocabulary, and business process.

To recommend `accepted`, the model must trace:

- creation and initial-state evidence;
- every claimed transition's source, destination, action, guard, and side effect;
- the concrete state write or authoritative transition contract;
- invalid or rejected transitions;
- asynchronous handlers and cross-repository continuations;
- terminal-state evidence and reachability; and
- conflicts between enums, persisted values, APIs, and UI labels.

An enum alone is a vocabulary, not a lifecycle. A state write without known source and destination is incomplete. UI actions, guessed initial values, wildcard updates, unreachable states, and partial transition paths remain hypotheses or unresolved candidates.

## 10. Decide Acceptance

The model is responsible for semantic recognition and may recommend acceptance, but a newly generated interpretation remains `hypothesis` unless the run contains:

1. an explicit reviewer decision or predeclared acceptance policy;
2. source evidence meeting the family-specific gate;
3. resolved contradictions or an explicit bounded exception; and
4. a recorded rationale linked to the candidate and evidence.

No inventory, `ck_*` fact, validator, confidence score, naming pattern, or model assertion can independently produce `accepted` semantics. Preserve accepted objects from an existing reviewed XML owner only when the task is a refresh and their meaning remains supported; otherwise flag them for review rather than silently downgrading or reaccepting them.

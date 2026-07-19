# Projection, Validation, And Coverage Review

Projection turns reviewed candidates into canonical XML. Validation proves grammar and reference integrity. Coverage review separately asks whether the model read and accounted for enough real code to justify the ontology. None of these stages may use an existing application ontology as a gold result or oracle.

## Contents

1. Output ownership.
2. XML projection.
3. Strict generated validation.
4. Deterministic reconciliation.
5. Coverage matrix.
6. Semantic coverage review.
7. Completion decision.

## 1. Output Ownership

Write a self-contained bundle with:

- canonical XML resources owned by `ontology-xml-dsl`;
- run-local source scope and read plan;
- evidence inventory;
- model-authored candidate ledger;
- unresolved and rejected candidate report;
- coverage matrix and review report; and
- optional deterministic inventory and reconciliation reports.

Keep audit sidecars outside canonical semantic modules. They are rebuildable observations and review records, not ontology meaning. Persist stable repository keys, immutable revisions, and repository-relative paths; keep local roots run-local.

No output may contain or depend on `depa_*` objects, judgments, exports, or ontology artifacts.

## 2. XML Projection

Before writing XML, load `ontology-xml-dsl/SKILL.md` and the routed specifications for every resource being emitted. That skill alone owns grammar, module placement, identities, references, ordering, evidence grades, status, schema evolution, and validation behavior.

Project according to these semantic boundaries:

- one semantic owner for each business fact;
- evidence resources own source coordinates and grades;
- implementation mappings connect repository-specific code to semantic identities;
- observations, read plans, candidate ledgers, and coverage reports remain noncanonical sidecars;
- unresolved candidates stay outside XML or project as explicit hypotheses when the DSL can represent the uncertainty;
- every newly projected interpretation is a hypothesis unless the recorded acceptance decision satisfies the workflow gates; and
- no generated XML embeds source code, callback bodies, query strings, business data, or target runtime instructions.

Do not duplicate XML schema snippets in this reference. Any schema-shaped example encountered in older material is illustrative only and is never a gold oracle. Resolve all authoring details from the current DSL owner.

## 3. Strict Generated Validation

Strict generated validation is mandatory for every produced bundle:

```bash
bun run skills/code-to-ontology-xml/scripts/audit-generated-ontology.ts \
  --ontology <bundle-root>/ontology.xml \
  --workspace-root <workspace-root> \
  --report <run-dir>/generated-ontology-audit.json
```

The audit entrypoint always invokes the `ontology-xml-dsl` validator with `--generated`; callers cannot fall back to default mode. A pass establishes closed grammar, references, and the DSL's generator evidence contract. It does not establish that the ontology is semantically complete or correct.

After validation, search the canonical bundle and audit sidecars for:

- forbidden `depa_*` dependencies;
- absolute or escaping source paths;
- revisions missing from evidence;
- unresolved evidence anchors;
- host-language implementation bodies in XML; and
- accepted objects without a recorded review decision and evidence-gate rationale.

Any failure blocks completion. Fix the source interpretation or projection; do not weaken the DSL validator or rewrite its grammar locally.

## 4. Deterministic Reconciliation

When the selected scope is represented by the existing inventory and coverage sidecars, run the deterministic reconciliation:

```bash
bun run skills/code-to-ontology-xml/scripts/audit-generated-ontology.ts \
  --ontology <bundle-root>/ontology.xml \
  --workspace-root <workspace-root> \
  --inventory <run-dir>/domain-inventory.json \
  --manifest <run-dir>/coverage-manifest.json \
  --report <run-dir>/generated-ontology-audit.json
```

This single entrypoint runs strict generated XML validation first and runs the existing coverage validator only after that stage passes. Its stable report records both stage outcomes, and either failure returns nonzero. The coverage stage checks anchors, revisions, references, dispositions, counts, and signal accounting. Every canonical Type, Relation, Rule, StateMachine, and ImplementationMapping must be covered by a mapped candidate's `ontologyRefs`; a Transition may instead be covered by its owning StateMachine candidate. It is a useful omission detector and audit accelerator.

Its pass does not prove that:

- source files were read;
- call chains or business processes were understood;
- a candidate has the right meaning or bounded context;
- two repositories share one business identity;
- a rule or lifecycle is complete; or
- any semantic object deserves `accepted` status.

When the deterministic helper does not support the selected language or repository shape, record that limitation and perform the same accounting in the model-authored coverage matrix. The strict DSL validator and semantic coverage review remain mandatory.

## 5. Coverage Matrix

Build the matrix from the declared scope and read plan before judging completeness. Track at least:

- repository, revision, bounded context, and source layer;
- planned orientation files and domain-bearing declarations;
- business entry points and representative vertical slices;
- rules, lifecycle paths, persistence constraints, events, and negative paths inspected;
- cross-repository joins inspected on both sides;
- files or paths actually opened;
- candidate IDs and evidence IDs produced;
- unresolved calls, parser failures, inaccessible components, and waivers; and
- coverage disposition: covered, partial, missing, or not applicable with rationale.

Inventory file counts may populate navigation statistics, but only opened source and traced paths satisfy read coverage. A high symbol count or mapped-candidate ratio cannot compensate for an unread business flow.

## 6. Semantic Coverage Review

Perform a fresh review after projection and deterministic checks. Reopen representative source anchors while reviewing; do not review only summaries or generated reports.

### Scope And Reading

- Every selected repository and revision matches the run record.
- Every core bounded context has orientation, domain-state, call-chain, business-process, and contradiction coverage, or an explicit limitation.
- Representative entry points reach concrete business behavior and persistence or integration effects.
- New paths discovered during reading were added to the plan and resolved or reported.

### Candidate Accounting

- Every model-discovered candidate and every accelerator signal is mapped, rejected, or unresolved with evidence and rationale.
- Rejected and unresolved candidates are visible and not hidden by aggregate totals.
- DTOs, entities, pages, routes, and tables are mapped as implementations unless source reading demonstrates independent business identity.
- Thin skeletons are rejected even when XML and numeric reconciliation pass.

### Cross-Repository Identity

- Every merged identity has a traced bridge and compatible meaning, identity, rules, and lifecycle.
- Lexical similarity, shared field names, or graph proximity never serve as the only merge evidence.
- Conflicts remain explicit hypotheses or unresolved candidates.

### Rules And Lifecycles

- Accepted rule recommendations satisfy the rule evidence gate and include violation or invariant behavior.
- Accepted lifecycle recommendations satisfy the lifecycle evidence gate, including initial state, transitions, guards or effects, invalid paths, and reachability.
- UI-only, naming-only, enum-only, and inventory-only signals remain hypotheses.

### Evidence And Acceptance

- Evidence anchors reopen at the pinned revision and support the exact claim.
- Grade reflects source authority; confidence does not upgrade grade.
- Every new accepted object has an explicit decision or policy, rationale, and qualifying evidence.
- Deterministic tools have not assigned semantic status or acceptance.

### Boundaries

- No `depa_*` input, evidence, interpretation, or output was used.
- No current application ontology output influenced concept selection, expected shape, counts, or review verdict.
- No gold sample, counterexample, or domain fixture was created.
- XML grammar is referenced only through `ontology-xml-dsl`.

## 7. Completion Decision

Report `PASS` only when:

1. scope and revisions are explicit;
2. the read plan is substantially completed and limitations are visible;
3. required call chains and business processes were traced in real source;
4. candidate and evidence ledgers reconcile;
5. cross-repository identities, rules, and lifecycles meet their evidence gates;
6. strict `--generated` validation passes;
7. deterministic reconciliation passes when applicable;
8. semantic coverage review finds no unaccounted core area or thin skeleton; and
9. all exclusion and oracle-independence boundaries hold.

Otherwise report `FAIL` or `PARTIAL` with stable, actionable gaps. Do not declare success because XML is valid, an inventory is deterministic, or every script exits zero.

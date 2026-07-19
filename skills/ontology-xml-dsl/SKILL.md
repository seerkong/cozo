---
name: ontology-xml-dsl
description: Author, review, validate, or evolve modular XML ontology bundles that describe domain types, mixins, attributes, relations with edge-owned properties, declarative rules, lifecycles, implementation mappings, evidence provenance, aliases, migrations, and generation snapshots, with optional projection to Cozo .NET OM. Use when creating or changing ontology.xml, TypeModule, RelationModule, RuleModule, LifecycleModule, ImplementationMappingModule, EvidenceModule, SchemaEvolutionModule, or generated C# OM schema projections.
---

# Ontology XML DSL

Keep ontology meaning in canonical XML resources and treat C# facade calls, Cozo `om_*` rows, diagrams, and prose catalogs as derived projections.

## Route

1. Read [foundation/axioms.md](foundation/axioms.md) before changing ownership, truth grades, inference policy, or runtime boundaries.
2. Read [std/ontology-bundle.md](std/ontology-bundle.md) when creating a bundle, splitting modules, or deciding where a fact belongs.
3. Read [std/naming-and-references.md](std/naming-and-references.md) when adding identities, references, imports, aliases, or versions.
4. Read [spec/ontology-resource.md](spec/ontology-resource.md) for the root `ontology.xml` grammar.
5. Read [spec/type-and-relation-resource.md](spec/type-and-relation-resource.md) for types, mixin declarations and composition, attributes, computed declarations, relations, and edge-owned properties.
6. Read [spec/rule-resource.md](spec/rule-resource.md) for constraints, predicates, violations, existential requirements, and runtime bindings.
7. Read [spec/lifecycle-resource.md](spec/lifecycle-resource.md) for state machines, transitions, guards, and effects.
8. Read [spec/implementation-mapping-resource.md](spec/implementation-mapping-resource.md) when connecting domain concepts or rules to Java, TypeScript, API, SQL, or other implementation facts.
9. Read [spec/evidence-resource.md](spec/evidence-resource.md) when recording provenance, confidence, resolver, repository, commit, symbol, and source location.
10. Read [spec/schema-evolution-resource.md](spec/schema-evolution-resource.md) for aliases, migrations, generation snapshots, and the boundary between semantic version and generation provenance.
11. Read [spec/csharp-om-projection.md](spec/csharp-om-projection.md) before generating or reviewing C# facade calls.

## Authoring workflow

1. Inspect the current bundle and source evidence before proposing ontology facts.
2. Separate observed code facts from interpreted domain meaning.
3. Choose the one owner module for each fact; reference it elsewhere instead of copying it.
4. Write strict declarative XML. Do not embed C#, JavaScript, callbacks, free-form query code, or target-specific loading instructions.
5. Give every identity-bearing interpretation its own `EvidenceRefs`, including types, mixins, relations, rules, lifecycles, aliases, migrations, and implementation mappings. Give attributes, computed attributes, transitions, and edge properties direct refs unless their owning object's evidence unambiguously covers the child claim.
6. Mark uncertain interpretation through evidence grade and confidence; never promote naming heuristics to authoritative business rules.
7. Run the bundled validator:

   ```bash
   bun run skills/ontology-xml-dsl/scripts/validate-ontology-xml.ts <bundle-root>/ontology.xml --workspace-root <workspace-root>
   ```

   For a bundle produced by a generator, enable the additional evidence gate:

   ```bash
   bun run skills/ontology-xml-dsl/scripts/validate-ontology-xml.ts <bundle-root>/ontology.xml --workspace-root <workspace-root> --generated
   ```

   Candidate-only publication may additionally require every status-bearing semantic declaration
   to remain a hypothesis:

   ```bash
   bun run skills/ontology-xml-dsl/scripts/validate-ontology-xml.ts <bundle-root>/ontology.xml --workspace-root <workspace-root> --generated --hypothesis-only
   ```

8. Generate C# OM projection only after XML validation succeeds. Never hand-edit a generated `.g.cs` file as the ontology source.

## Validation modes

- Default mode validates the closed element/attribute/parent-child grammar, required fields and cardinalities, module and semantic references, forbidden `depa_*` text, cycles, evolution consistency, and lifecycle reachability. It intentionally remains compatible with existing authored bundles whose child claims rely on owner evidence.
- `--generated` adds a stricter generation contract. Every generated Type, Mixin, Attribute, ComputedAttribute, Relation, Property, Rule, StateMachine, Transition, ImplementationMapping, Alias, and Migration requires direct `EvidenceRefs`. Any such object with `status="hypothesis"` must resolve at least one direct ref to `Evidence@grade="inferred"`.
- Child order stated by the resource specifications remains normative for authors and generators. The current object parser groups children by key and does not preserve enough cross-key ordering information for reliable validation, so this validator checks allowed placement and cardinality but does not claim to verify cross-key order.

## Non-negotiable boundaries

- XML owns ontology declarations; indexed `ck_*` facts are observations and `om_*` facts are runtime materialization.
- XML resources must not read, import, reference, or emit `depa_*` objects. A DEPA judgment is not source evidence for this DSL.
- Rules use the predicate vocabulary in the rule spec. Natural-language `Statement` explains a rule but never replaces its machine-readable condition.
- XML may declare constraints and state transitions but may not contain host-language executable bodies.
- `Evidence` owns source coordinates and inference metadata. Other resources reference evidence IDs and do not duplicate paths or line numbers.
- `Ontology@version` identifies semantic schema meaning. `GenerationSnapshot` records how one artifact generation was produced and never advances semantic version by itself.
- Relation `Property` declarations describe values owned by an edge. Identity-bearing, independently referenced, or lifecycle-bearing associations must be modeled as `Type` plus `Relation`, not as an overloaded edge.
- Frontend behavior is normally `presentational`; backend enforcement is normally `enforced`; persisted invariants may be `authoritative`. Upgrade grades only with evidence.
- Java/Spring role, route, transaction, listener, mapper, and data-role facts remain implementation observations until a mapping or rule explicitly interprets them.
- Keep root assembly, module declarations, runtime projection, and target presentation separate.

## Validate outcome

Confirm XML well-formedness, the closed grammar, unique IDs, resolvable module hrefs, reference-kind closure, FQN shape, evidence grade/confidence, predicate structure, evolution references, semantic-version consistency, lifecycle reachability, and absence of host-language code or `depa_*` dependencies. In `--generated` mode, also confirm direct evidence coverage and inferred evidence for hypotheses. Then review semantic consistency that the validator cannot prove: type and mixin compatibility, edge-property intent, rule intent, migration safety, evidence strength, and projection fidelity.

# Ontology XML DSL foundation axioms

These axioms define the stable ontology authoring model. Specifications may refine them but must not contradict them.

## O1. XML owns ontology meaning

Canonical XML resources own type, mixin, relation, rule, lifecycle, mapping, evidence, alias, migration, and generation-provenance declarations. Generated C#, Cozo `om_*` rows, RDF exports, diagrams, and Markdown catalogs are projections.

## O2. Observation and interpretation are distinct

CodeKnowledge `ck_*` facts and direct source inspection describe observed repositories: files, symbols, edges, entry points, processes, framework roles, and diagnostics. An ontology resource interprets selected observations as domain meaning. Never rename an observation into a business concept without an explicit mapping and evidence. `depa_*` judgments are outside this DSL's input, reference, and output boundaries.

## O3. One fact has one owner

Define each type, mixin, relation, rule, state machine, mapping, evidence item, alias, and migration once. Other modules reference its stable ID. Do not duplicate descriptions, paths, constraints, properties, or transition definitions across modules.

## O4. Identity survives layout

FQN identity is independent from directory and filename. Moving a module does not rename its ontology objects. A rename is an explicit schema evolution event, not a side effect of moving a file.

## O5. XML is declarative

XML may contain typed predicates, declarative constraints, existential requirements, state transitions, and effect declarations. It must not contain C#, JavaScript, callbacks, closures, arbitrary scripts, opaque query strings, or target-platform invocation instructions.

## O6. Text explains; structure decides

`Description` and `Statement` provide human meaning. Machine behavior comes from explicit attributes and child grammar. A natural-language sentence never substitutes for a missing predicate, relation endpoint, transition, or evidence reference.

## O7. Evidence is first class

Every interpreted semantic object is covered by one or more evidence items. Identity-bearing Type, Mixin, Relation, Rule, StateMachine, ImplementationMapping, Alias, and Migration objects carry direct `EvidenceRefs`; their child declarations may use direct refs or an unambiguous owning-object scope. A hypothesis still requires at least one `inferred` evidence item; `status="hypothesis"` is not an evidence waiver. Evidence records repository, revision, source position, resolver, grade, and confidence without being copied into semantic modules.

## O8. Truth grades remain ordered

Use these grades:

1. `authoritative`: persisted invariant, schema constraint, or designated fact source.
2. `enforced`: backend code rejects or prevents violations.
3. `contractual`: API, DTO, schema, or protocol declaration.
4. `presentational`: frontend visibility, validation, or interaction behavior.
5. `inferred`: naming, graph topology, heuristic, or model interpretation.

Lower-grade evidence may support discovery but cannot by itself establish a higher-grade rule.

## O9. Confidence is not authority

Confidence records certainty that evidence was interpreted correctly. Grade records authority of the source. A confidence of `1.0` on a frontend condition remains `presentational`.

## O10. Runtime projection is one way

Project validated XML into C# facade calls and then into Cozo `om_*` relations. Do not treat runtime rows as the editable source. A runtime callback may implement a declared binding, but its code is owned outside XML.

## O11. Open and closed world are explicit

Current Cozo OM validation behaves as an application object system, not unrestricted OWL inference. Rules that assume completeness, uniqueness, or absence must state that closed-world intent through their declared kind and predicate structure.

## O12. Semantic evolution is versioned

The root ontology declares a semantic schema version. A change to ontology meaning requires deliberate version treatment; backward-incompatible changes require a new semantic version and an explicit migration. Stable IDs must not silently change meaning.

## O13. Generation provenance is not semantic version

A generation snapshot records generator identity, source revisions, generation time, and normalized output digest. Re-running generation against relocated evidence may create a new generation snapshot without changing `Ontology@version`. Timestamps, commits, build numbers, and runtime schema counters must never be used as implicit semantic versions.

## O14. Framework inference stays bounded

Java/Spring and TypeScript framework derivations are implementation evidence. Resolver, confidence, and source location remain visible. Annotation or naming inference must not be presented as an authoritative business rule.

## O15. Edge ownership is explicit

A relation may own scalar properties of the edge itself. An association that has independent identity, participates in other relations, or owns a lifecycle is a `Type`, not a property-rich relation edge.

## O16. Validation is layered

Structural validation checks XML grammar and references. Semantic validation checks type compatibility, cardinality, state reachability, rule satisfiability, evidence sufficiency, and projection fidelity. Passing the first does not imply the second.

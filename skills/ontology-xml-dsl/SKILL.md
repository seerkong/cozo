---
name: ontology-xml-dsl
description: Author, review, validate, or evolve BO-first ontology XML that mechanically projects portable Cozo OM models to Bun and .NET. Use for BusinessObject, Association, BusinessProcess, DomainPolicy, EventContract, EvidenceCatalog, RuntimeBinding, or the canonical v5 ontology layout.
---

# BO-First Ontology XML DSL

XML is the canonical model. Generated Bun/.NET code, `om_*` rows, diagrams, and prose are projections.

## Canonical layout

Use PascalCase directories, XML filenames, elements, and FQN segments. Keep XML attributes conventional lower case (`id`, `ref`, `href`, `owner`). The v5 IT Asset example at `cozo-ontology/v5/` is the normative reading example.

```text
Ontology.xml
BusinessObjects/<Namespace>/<BusinessObject>/BusinessObject.xml
BusinessObjects/<Namespace>/<BusinessObject>/{ConstraintHandlers,Lifecycles,...}.xml
Associations/<Namespace>/
BusinessProcesses/<Namespace>/
DomainPolicies/<Namespace>/
EventContracts/<Namespace>/
EvidenceCatalog/<Namespace>/
RuntimeBindings/{Bun,DotNet}/<Namespace>/
```

`BusinessObject.xml` is the human entry point: purpose, identity, properties, and short references to its local detailed members. A plural collection contains singular members—`<Actions><Action ref="…" /></Actions>` and `<Evidences><Evidence ref="…" /></Evidences>`—never `*Refs` elements.

## Ownership and portability

- A `BusinessObject` owns only its local data and behaviour.
- `Association`, `BusinessProcess`, `DomainPolicy`, and `EventContract` own cross-BO meaning; do not hide it in one BO handler.
- `Portable` declarations use only the closed target-neutral predicate/effect vocabulary and must generate for every selected target.
- `ExtensionPoint` declares the contract but no host code. Every selected target needs a `RuntimeBinding`; missing binding is a generation error.
- Do not invent Actions, Mutations, Interceptors, ComputedFunctions, states, or .NET bindings absent from evidence.

## Evidence contract

`Evidence` is material, not a citation label. Keep source metadata as attributes and put the revision-pinned code excerpt, document excerpt, or read-only investigation result directly in the `Evidence` text node. Do not add wrapper nodes such as `Materials`, `Content`, `Findings`, `Document`, or `Code`. Preserve uncertainty and gaps explicitly.

## ExtensionPoint internal logic

An `Action`, `Mutation`, `Interceptor`, `ConstraintHandler`, or `ComputedFunction` with `portability="ExtensionPoint"` may contain one non-empty plain-text `<InternalLogic>` element. It explains arbitrary internal implementation for readers and target implementers; it is neither executable pseudocode nor a code-generation input. Keep inputs, outputs, failures, declared mutations, and RuntimeBindings structured. Keep real source code in `Evidence`.

## Workflow

1. Start from reviewed source or database material; keep observed material separate from the semantic interpretation.
2. Assemble BO summaries first, then local details and cross-BO resources using FQN references.
3. Mark callback-backed behaviour as `ExtensionPoint`; never copy JavaScript/C# into semantic declarations.
4. Validate structural closure, FQN kind closure, evidence material coverage, portable subset support, and target bindings before generation.
5. Generate source only from validated XML. Never hand-edit generated code as ontology source.

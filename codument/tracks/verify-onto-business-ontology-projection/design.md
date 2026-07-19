# Design: Independent delivery verification

The verifier reads only the completed `onto_*` generations and the generated XML bundle. It checks storage counts, artifact module structure, strict DSL validation, and evidence samples. It does not regenerate the input and does not infer or promote any additional business semantics.

The verification report is the delivery boundary: it distinguishes the canonical, evidence-backed ontology from the candidate audit queue, and it records the absence of canonical relationships, business rules, and lifecycles as a known next capability rather than treating zero counts as successful semantic coverage.

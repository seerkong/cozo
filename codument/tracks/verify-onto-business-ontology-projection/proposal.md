# Proposal: Verify business ontology projection

## Goal

Independently verify the fresh `onto_*` generations in the two existing project databases and the XML artifact delivered to `cozo-ontology/v0/ontology/it-asset-management`.

## Scope

- Confirm that the exported XML comes from `onto_*`, not `depa_*` or raw `ck_*` output.
- Inspect cardinalities and representative concepts, attributes, mappings, and evidence.
- State the boundary honestly: deterministic evidence rules only promote high-confidence business carriers; unresolved implementation, relationship, rule, and lifecycle semantics remain candidates rather than invented canonical ontology.

## Safety

The verification is read-only for both target databases and the final artifact directory.

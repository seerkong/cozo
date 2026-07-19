# Proposal: Java call resolution

## Goal

Extend the shared `CallResolver` with Java-aware binding and import/package scope so Java call sites become trustworthy `CALLS` and `ACCESSES` facts without changing the CodeKnowledge schema.

## Scope

- Java declared-type bindings for fields, parameters, and locals.
- `this`/`super`, constructor, static type, package-local, explicit import, wildcard import, and static import resolution.
- Java override/implementation and access-edge regression coverage.
- Existing C#/TS/JS resolution behavior remains unchanged.

## Out Of Scope

- Compiler-complete overload/type inference.
- Maven/Gradle classpath resolution and external dependency edges.
- Spring framework semantics, which belong to the next mission node.

# Decisions

- D3a=A: JSON is the canonical manifest format; YAML is an adapter over the same semantic model.
- D3b=A: missing callbacks may import as inactive/unresolved; execution fails closed; `requireReady` atomically rejects incomplete imports.
- D0: parity is observable semantics with idiomatic C# API shape, not copied JavaScript syntax.
- D4: preserve typed delegates, explicit runtime registry, async cancellation, and C# transaction boundaries.
- G5 owns optional Jint execution; G9 owns public validation facade parity.
- Binding identity is persisted in an additive relation; readiness is derived from binding identity plus an immutable registry snapshot and is never persisted as a stale ready flag.
- YAML support is isolated in `Cozo.DotNet.Om.Portability.Yaml`, pinned to YamlDotNet 18.1.0, and normalizes a bounded safe AST into the canonical catalog before Om.Core validation/import.
- Strict import uses a staged registry snapshot and runtime publication gate; readers observe only complete pre/post states, and compensation failures remain explicit.

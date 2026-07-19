# Design: Java call resolution

## Approach

Keep the registry and confidence tiers in `CallResolver`. Add a Java typed-binding pattern and a Java import scope assembled from package symbols plus `IMPORTS` edges. Exact and wildcard imports contribute matching types and members; static member imports contribute the imported member. Treat `super` like the existing inherited-member receiver tier.

## Confidence

- Exact receiver binding, `this`/`super`, constructor, or unique static type: 0.9.
- Unique package/import/name+arity fallback: 0.7.
- Remaining legal ambiguity: 0.5 with deterministic target and `ambiguous:<n>` evidence.
- No legal candidate: no edge plus existing unresolved diagnostics.

## Compatibility

All new paths are gated on `file.Language == "java"`. The shared registry, edge schema, counters, budgets, and C#/TS/JS branches remain intact.

# 方案设计：.NET OM 类型系统 parity audit and repair

## Implementation Plan

1. Add focused parity assertions to the .NET test program for:
   - mixin-contributed attributes;
   - inherited attribute override rejection;
   - type redefinition preserving parent when parent is omitted;
   - alias-stored property fallback;
   - `Validity` attribute writes.
2. Implement effective attribute merging with Bun precedence: mixins, far ancestors, near ancestors, self.
3. Enforce inherited attribute override safety in `DefineAttributeAsync`.
4. Add `Validity` normalization and use Cozo `validity(ts_us, is_assert)` when writing Validity-typed properties.
5. Add alias fallback when reading canonical properties and canonicalize property dictionaries before validation.
6. Expose public type-system inspection APIs needed by callers.

## Decisions

- Preserve .NET DEPA boundaries. Logic may build CozoScript but must execute it only through `ICozoOmStore`.
- Keep Cozo stored relations as the authoritative schema facts.
- Do not resolve relation `directed=false` drift in this track unless tests expose a concrete regression; leave it to the mission reconciliation item.

## Risks

- Cozo `validity(...)` values require expression-based query construction rather than ordinary parameter value insertion.
- Alias fallback can hide conflicting rows. Canonical rows should win when both canonical and alias values exist.

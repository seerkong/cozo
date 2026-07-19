# Decisions

## D1: Preserve The C# Transaction Boundary

Parent dispatch returns mutations to the overriding action. The existing outer action execution applies the final list and owns interceptor execution and commit/rollback. No nested transaction is introduced.

## D2: Keep Metadata And Delegates Separate

Cozo relations describe behavior definitions; `CozoOmRegistry` holds executable callbacks. Guardrails make their registration sequence consistent but do not serialize delegates.

## D3: Ancestor-To-Child Interceptor Order

Both before and after interceptors run root ancestor first, then descendants, while preserving registration sequence within each owner. This is the Bun reference behavior.

## D4: Public Surface Boundary

The only new public behavior surface in this track is the action-context parent-dispatch capability required for parity. General validation, traversal, and type inference facades remain in mission G9.

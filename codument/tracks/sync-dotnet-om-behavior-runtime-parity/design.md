# Design

## Existing Runtime

`ConstraintLogic.ExecuteActionAsync` opens one write transaction, resolves the effective action, runs inherited before interceptors, executes returned mutations, runs inherited after interceptors, and commits. Action and mutation lookup already search concrete type then ancestors. `OmActionContext.ActionOwnerType` records the owner selected by action resolution.

The design extends this runtime rather than introducing a second execution path.

## Parent Dispatch

`OmActionContext.CallParentActionAsync(actionName, parameters, cancellationToken)` delegates to behavior logic using:

- the entity's concrete `TypeName` for mutation resolution and context reads/writes;
- the current `ActionOwnerType` as the point above which parent lookup begins;
- the same transactional `Runtime` carried by the context;
- a new context whose `ActionOwnerType` is the parent registration owner.

The method invokes the parent handler and returns its mutation list. It does not run interceptors or execute mutations itself. This matches Bun's composition contract and avoids nested transactions or duplicate interceptor execution.

Errors are `InvalidOperationException` values whose stable diagnostic content identifies the requested action and current action owner. The message distinguishes a missing parent type from a parent chain that has no requested action. Existing type cycle prevention remains the hierarchy safety boundary.

## Interceptor Ordering

The owner chain is materialized root-first. For each owner, the registry returns that owner's registrations in `Seq` order. The flattened result retains owner grouping. A final global `OrderBy(Seq)` is forbidden because `Seq` is local to an owner/action/phase key.

Both `before` and `after` use ancestor-to-child order, matching the Bun reference. An exception still aborts the shared transaction.

## Guardrails And Atomicity

Behavior metadata methods validate owner type before writes. Constraint scopes are normalized and then checked against `conditional`, `cross-entity`, `computed-dep`, and `custom`. Interceptor phase normalization happens before either persistence or registry mutation.

Callback overloads reject null callbacks before persisting metadata. For interceptors, the runtime computes the next owner-local sequence, persists the validated metadata, and only then registers the callback with that same sequence. If persistence fails, registry state remains unchanged. If registration unexpectedly fails after persistence, the newly inserted metadata row is removed before the exception escapes. Registry helpers added solely for this coordination remain `internal` unless an existing public contract requires otherwise.

## Preserved C# Decisions

- Keep one transactional action/mutation/interceptor boundary.
- Keep persisted metadata separate from executable delegates.
- Keep typed async C# context methods and cancellation.
- Do not expose registry internals as new public API.

## Cross-Implementation Tests

C# scenarios are added to the existing executable OM test harness. Bun tests remain the reference for parent dispatch and inherited interceptors; missing mutation-inheritance and mixed parent/child sequence cases are added so both sides assert the same behavior.

## Risks

- Parent dispatch could accidentally re-run interceptors or open a nested transaction. Tests assert one interceptor pass and full rollback.
- Registry/persistence sequencing could drift under concurrent registration. The implementation must use one owner-local sequence source and avoid public mutable registry state.
- Tightened invalid-scope validation is behavior-changing for callers that persisted unsupported strings; this is intentional guardrail behavior, not a compatibility mode.

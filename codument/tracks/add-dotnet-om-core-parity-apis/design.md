# Design: .NET OM Core Parity APIs

## API Shape

Preferred additive API surface:

```csharp
public Task<EntityView?> GetEntityViewAsOfAsync(string entityId, string asOf, CancellationToken cancellationToken = default);

public Task<IReadOnlyList<FindByTypeEntry>> FindByTypeWithPropertiesAsync(
    string typeName,
    IReadOnlyDictionary<string, object?>? filter = null,
    FindByTypeOptions? options = null,
    CancellationToken cancellationToken = default);

public Task ValidatePropertyTypeAsync(string entityId, string attrName, object? value, CancellationToken cancellationToken = default);

public Task ValidateRelationAsync(string fromId, string relName, string toId, CancellationToken cancellationToken = default);

public Task<IReadOnlyList<string>> ValidateRequiredPropertiesAsync(string entityId, CancellationToken cancellationToken = default);

public Task FinalizeEntityAsync(string entityId, CancellationToken cancellationToken = default);
```

`FindByTypeEntry` should contain:

```csharp
public sealed record FindByTypeEntry(
    string Id,
    string TypeName,
    string Label,
    IReadOnlyDictionary<string, JsonElement> Properties);
```

## Historical Entity View Implementation

Implement `EntityLogic.GetEntityViewAsOfAsync` by reusing existing temporal primitives:

- normalize `asOf` using the same timestamp path as `GetPropertyAsOfAsync`
- load entity metadata from `om_entity`
- query historical properties with `*om_property{ ..., value @ $as_of }`
- canonicalize aliases with existing canonicalization helper
- evaluate inherited computed values using an as-of-aware computed context
- call `RelationLogic.GetNeighborsAsOfAsync` for outgoing edges

Avoid duplicating relation validation or type hierarchy logic.

## Computed Lookup

Add one shared resolution path for computed definitions:

- collect ancestors from root to parent
- merge parent computed registrations first
- apply current type registrations last
- resolve attr aliases against the concrete entity type

This shared lookup should be used by:

- `GetPropertyAsync`
- `GetPropertyAsOfAsync`
- `GetEntityViewAsync`
- `GetEntityViewAsOfAsync`
- constraint contexts that call `GetPropertyAsync`

## Constraint Runtime Model

C# currently has `Func<OmValidationContext, ValueTask<string?>>` validators. To model Bun `when/then`, add a typed registration shape rather than overloading string return delegates.

Conservative direction:

- keep `RegisterValidator` for existing `custom` constraints
- add `RegisterConstraint` for scoped constraints with `when` and `then`
- add context methods for `GetPropertyAsync`, `GetPropertyAsOfAsync`, and `GetNeighborsAsync`
- make validation collect effective constraints from ancestors plus current type
- let exact child constraint names override inherited constraints with the same name

The implementation can internally adapt old validators into the same execution pipeline.

## Mutation Rollback

`SetPropertyAsync` and action/mutation execution already validate after writes. Relation writes must also validate cross-entity constraints after edge insertion and preserve rollback behavior. If the current relation write path is not transactional, the implementation should wrap write-plus-validation in a write transaction before enabling cross-entity constraints.

## Tests

.NET tests should add cases for:

- historical entity view properties and outgoing edges
- historical computed property context
- filtered type search with canonicalized properties
- exact vs polymorphic filtered search
- public validation helper success/failure
- finalize entity success/failure
- inherited computed property
- child computed override
- conditional constraint
- cross-entity constraint rollback
- computed-dependent constraint
- inherited constraint on subtype

Bun tests should be checked and supplemented only where missing:

- `getEntityViewAsOf`
- `findByType` filter plus returned properties
- validation helper exports and behavior
- computed inheritance
- constraint scopes and inheritance

## Risks

- Returning rich properties from `findByType` could be expensive. Keeping the existing lightweight API and adding a rich API makes cost explicit.
- Callback semantics cannot be restored from persisted metadata. This remains an existing runtime limitation and should be documented in tests or README notes if touched.
- Historical computed values require context plumbing; a computed delegate that directly calls non-as-of methods will still observe NOW unless the context methods are as-of-aware.

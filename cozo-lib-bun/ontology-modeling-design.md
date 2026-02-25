# Ontology Modeling and Analysis Design

## 1. Goal

Build a practical ontology modeling layer on top of Cozo for Bun/JS usage, so users can:

- define object types, property types, and link types,
- ingest instance data with validation,
- run analysis/mining queries over modeled data.

This design aligns with a Foundry-like ontology workflow while keeping implementation lightweight and scriptable.

## 2. Core abstractions

### 2.1 ObjectType

Represents a business entity category, e.g. `User`, `Project`, `Task`.

Mapped relation:

- `om_type {name => description}`

### 2.2 PropertyType

Defines a property allowed for an `ObjectType`:

- name (`attr_name`)
- value type (`String` / `Number` / `Bool` / `Json`)
- required flag

Mapped relation:

- `om_attr_def {type_name, attr_name => value_type, required}`

### 2.3 LinkType

Defines relation semantics between object types:

- link name (`rel_name`)
- source type (`from_type`)
- target type (`to_type`)
- directionality (`directed`)

Mapped relation:

- `om_rel_def {rel_name => from_type, to_type, directed}`

### 2.4 Instance

Object instance and its data:

- base identity: `om_entity {id => type_name, label}`
- dynamic attributes (EAV): `om_property {entity_id, attr_name => value}`
- links/edges: `om_edge {from_id, rel_name, to_id => props}`

## 3. Validation and integrity

Validation is enforced in library write APIs (`cozo-om.js`):

- `setProperty` checks property existence and type compatibility,
- `linkEntities` checks link type definition and endpoint type compatibility,
- `finalizeEntity` enforces required properties before “publish-ready” usage,
- `ingestBatch` performs transactional ingestion (single write transaction) and optional required-property validation.

## 4. Analysis/mining capabilities (current)

### 4.1 Type-based filtering

- `findByType(typeName, filter)`
- use case: find all `User` with `role = engineer`.

### 4.2 Relationship traversal (path)

- `traverse(startId, relPath)`
- use case: `User --owns--> Project --contains--> Task`.

### 4.3 Neighborhood analysis

- `getNeighbors(entityId, relName?)`
- use case: incoming/outgoing dependency and impact inspection.

### 4.4 Numeric aggregation

- `aggregateByType(typeName, attrName, op)`
- supports `sum | avg | min | max | count`
- use case: total/average task estimate by type.

## 5. Suggested usage flow

1. Initialize schema (`initSchema`)
2. Define ontology (`defineType` / `defineAttribute` / `defineRelation`)
3. Ingest instances (`createEntity`, `setProperty`, `linkEntities`, or `ingestBatch`)
4. Validate integrity (`validateEntity`, `finalizeEntity`)
5. Run analysis (`traverse`, `findByType`, `getNeighbors`, `aggregateByType`)

## 6. MVP / V1 / V2 roadmap

### MVP (implemented)

- core ontology relations (`om_type`, `om_attr_def`, `om_rel_def`)
- instance relations (`om_entity`, `om_property`, `om_edge`)
- runtime validation (type/link/required)
- analysis APIs (`traverse`, `findByType`, `getNeighbors`, `aggregateByType`)

### V1

- stronger transactional scripts with Cozo query-chain assertions
- domain constraints: enum/range/pattern metadata and validation
- query template registry for reusable mining patterns

### V2

- temporal/versioned ontology evolution
- materialized relation views for expensive analyses
- graph algorithm integration (e.g. shortest path, centrality, clustering) as first-class APIs

## 7. Known limitations

- EAV model is flexible but can be slower for very wide/high-volume analytical scans.
- Current relation naming is global (`rel_name`) and may need namespacing for multi-domain ontologies.
- Deletion integrity checks (preventing dangling edges) are not yet implemented.

## 8. Practical note

The current `cozo-om` library is designed as a stable foundation for iterative ontology-driven analytics. It is suitable for prototyping and medium-scale modeling, and can be gradually hardened toward a production ontology service.

# Design: Spring semantic derivation

## Seam

`SpringSemanticDeriver` lives in Indexing and consumes Java `FileInput`, flattened symbols, and source text after syntax extraction/call resolution. It does not modify `JavaExtractor` or the parser schema.

## Vocabulary

- `SPRING_ROLE` -> `spring:role:controller|service|component|configuration|repository|mapper|listener|handler|dto|entity|vo`
- `SPRING_TRANSACTION` -> `spring:transaction`
- entry-point kinds: `http_route`, `kafka_listener`, `event_listener`

Annotation evidence uses confidence 0.99. Import-qualified mapper classification uses 0.95. Naming/package conventions use 0.70 and never become authoritative business facts.

## Annotation Forms

Support direct string, `value=`/`path=`, brace arrays, and `RequestMethod.*`; preserve transaction and listener arguments in metadata/evidence. Only controller-owned mapping methods become inbound HTTP routes, preventing Feign mappings from being misclassified.

## Integration

Merge derived edges/concepts/entry points into `RepositoryIndexer.BuildBatchAsync` before batch construction. Existing file-scoped edge deletion and symbol-scoped entry-point lifecycle provide incremental cleanup.

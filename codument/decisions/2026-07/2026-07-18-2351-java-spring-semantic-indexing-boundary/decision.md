# Decision: Java/Spring semantic indexing boundary (durable)

- Date: 2026-07-18
- Source: mission `add-dotnet-llm-wiki-java-spring-semantic-parsing` decisions D2 and D4
- Status: Durable project decision

## Decision

1. Java indexing does not require a complete Maven build or a fully resolvable enterprise classpath. Tree-sitter structural facts, lightweight call resolution, and annotation-driven Spring derivation remain independently usable; richer Java semantic analysis is an optional enhancement.
2. Java/Spring code-knowledge indexing and IT asset business-ontology extraction remain separate delivery boundaries. The indexing layer supplies auditable graph evidence; ontology projection consumes that evidence in a separate mission or track.

## Rationale

Enterprise repositories can depend on unavailable parent POMs and private artifacts. Keeping the structural baseline independent preserves useful indexing under partial dependency resolution. Separating parser infrastructure from business-ontology extraction also keeps their evidence sources, acceptance gates, and failure modes independently testable.

## Related decisions

The parser backend and CodeKnowledge schema continue to follow `decision://llm-wiki-code-graph-foundations`; this decision narrows the Java/Spring enhancement and downstream-consumer boundary rather than replacing that foundation.

## Source evidence

- Mission archive: `codument/missions/archived/2026-07-18-add-dotnet-llm-wiki-java-spring-semantic-parsing/`
- Completion evidence: `reports/mission-completion.md` within that archive
- Reference: `decision://java-spring-semantic-indexing-boundary`

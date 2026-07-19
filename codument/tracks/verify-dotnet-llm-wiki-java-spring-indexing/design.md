# Design: dogfood verification

1. Build Release `depa-wiki` and verify `parser_status` is sourced from the same native-first `ParserBackendSelector` used by indexing, including the Java ABI when the bundled grammar is available.
2. Index the target repository into `/tmp/depa-wiki-is-asset-new-java-spring.db` with documentation inference off.
3. Record files/symbols/edges/call/process counters and elapsed time.
4. Query overview/symbol context/trace plus direct persisted facts through a focused verifier when needed.
5. Sample known controller, route, transaction, Kafka listener, MyBatis mapper, and MapStruct mapper sources.
6. Run the full solution/smoke/OM regression and leave no server/test process running.

Mapper import disambiguation accepts both explicit imports and a single known wildcard namespace (`org.mapstruct.*` or `org.apache.ibatis.annotations.*`). If both known wildcard namespaces are present, the conservative ambiguous diagnostic remains in force.

## Dogfood correction

The first Release dogfood run proved that indexing loaded the bundled Java grammar while the public `parser_status` tool still queried the CLI-only parser. The status surface must delegate to `ParserBackendSelector.DescribeStatus()` so observability cannot contradict the parser path used by indexing. `parse_file` remains CLI-specific in this track because changing its parsing contract is outside the verification scope.

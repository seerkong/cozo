# depa-cozo

`depa-cozo` is the thin Cozo native binding for both Bun and Node.js. It exposes database lifecycle, CozoScript queries, transactions and backup/import/export primitives only. It intentionally does **not** contain an object model, ontology DSL or business behavior system.

> `depa-cozo@0.1.1` supports **macOS arm64 only**. Linux, Windows, and macOS x64 binaries will be released in later versions.

## Use

```js
const { CozoDb } = require('depa-cozo');

const db = new CozoDb('mem');
const result = await db.run('?[value] <- [[42]]');
db.close();
```

The published package contains the `darwin-arm64` N-API binary. npm restricts installation to macOS arm64, and the runtime loader rejects every other platform or architecture before attempting to load a binary.

## Source build

From a macOS arm64 checkout of this repository, run `npm run build-native` in this directory. It builds the existing `cozo-node` N-API artifact and copies it into `native/darwin-arm64/`. `npm run verify-native` verifies the host artifact before packing.

Later cross-platform release packaging is performed by `.github/workflows/release-depa-bindings.yml` after each target has a verified native artifact.
# Session boundaries

Await a transaction query before issuing its next query, committing, or aborting.
The wrapper rejects overlapping transaction queries because the native transaction
has a shared result channel. Commit and abort terminate the transaction. Closing
a database aborts idle open transactions; close rejects while queries are pending,
and repeated close calls are harmless. Native query errors are `Error` instances
and retain Cozo's structured fields, including `ok`, `code`, and `display`.

Use CozoScript's `:timeout` directive for native query budgets (seconds). A timed
out query rejects, and the database remains usable. JavaScript timeouts alone do
not cancel native queries. Callbacks and arbitrary rule evaluation policy belong
to consumers; this binding contains no domain or inference rules.

Version 0.1.1 includes explicit transaction terminal-state and overlap guards, close rollback, and structured native errors. Candidate package verification does not imply registry publication.

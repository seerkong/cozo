# depa-cozo

`depa-cozo` is the thin Cozo native binding for both Bun and Node.js. It exposes database lifecycle, CozoScript queries, transactions and backup/import/export primitives only. It intentionally does **not** contain an object model, ontology DSL or business behavior system.

The loader selects one prebuilt addon by the host's operating system and CPU. It does not keep an allowlist. A target is supported when its file is present, and missing when it is not.

## Use

```js
const { CozoDb } = require('depa-cozo');

const db = new CozoDb('mem');
const result = await db.run('?[value] <- [[42]]');
db.close();
```

At load time the package requires exactly one file:

```text
native/<platform>-<arch>/depa_cozo.node
```

`<platform>` and `<arch>` are Node's `process.platform` and `process.arch`. Windows is `win32`, not `windows`. The slots are independent: an Intel Mac loads `native/darwin-x64/depa_cozo.node` and never opens the arm64 file.

| Host | Artifact |
| --- | --- |
| macOS Apple silicon | `native/darwin-arm64/depa_cozo.node` |
| macOS Intel | `native/darwin-x64/depa_cozo.node` |
| Windows x64 | `native/win32-x64/depa_cozo.node` |
| Linux x64 | `native/linux-x64/depa_cozo.node` |

This checkout currently contains the Intel macOS artifact only. The arm64 slot is empty. Windows and Linux slots are named by the same rule; build them on that host and drop the file in. npm does not restrict `os` or `cpu`, so installing the package on a machine whose artifact is absent fails at load with the missing path, not at install time.

## Source build

From a checkout of this repository, run `npm run build-native` in this directory. It builds the existing `cozo-node` N-API artifact for the **host** (no cross-compilation) and copies it to `native/<platform>-<arch>/depa_cozo.node`. On this Intel Mac that is `native/darwin-x64/`. On Windows x64 it is `native/win32-x64/`. The RocksDB submodule must be checked out first (`git submodule update --init cozorocks/rocksdb`). `npm run verify-native` verifies the host artifact before packing.

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

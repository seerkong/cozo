#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PACKAGE_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
TEST_DIR="$(mktemp -d /tmp/depa-cozo-npm-consumer.XXXXXX)"
trap 'rm -rf "$TEST_DIR"' EXIT

(cd "$PACKAGE_DIR" && npm pack --pack-destination "$TEST_DIR" >/dev/null)
PACKAGE_TARBALL="$(find "$TEST_DIR" -maxdepth 1 -name 'depa-cozo-*.tgz' -print -quit)"
if [[ -z "$PACKAGE_TARBALL" ]]; then
  echo "npm pack did not produce a depa-cozo tarball." >&2
  exit 1
fi

(
  cd "$TEST_DIR"
  npm init --yes >/dev/null
  npm install --ignore-scripts --no-save "$PACKAGE_TARBALL" >/dev/null
  node -e '(async () => { const { CozoDb } = require("depa-cozo"); const db = new CozoDb("mem"); const r = await db.run("?[value] <- [[42]]"); db.close(); if (r.rows[0][0] !== 42) throw new Error(JSON.stringify(r)); })().catch(error => { console.error(error); process.exit(1); });'
  bun -e 'const { CozoDb } = require("depa-cozo"); const db = new CozoDb("mem"); const r = await db.run("?[value] <- [[42]]"); db.close(); if (r.rows[0][0] !== 42) throw new Error(JSON.stringify(r));'
)

echo "Verified Node.js and Bun loading from the packed depa-cozo tarball."

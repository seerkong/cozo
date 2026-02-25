#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"

cargo build --release -p cozo-node -F compact -F storage-rocksdb --manifest-path "$ROOT_DIR/Cargo.toml"

mkdir -p "$SCRIPT_DIR/native"

SOURCE=""
for candidate in \
  "$ROOT_DIR/target/release/libcozo_node.dylib" \
  "$ROOT_DIR/target/release/libcozo_node.so" \
  "$ROOT_DIR/target/release/cozo_node.dll"
do
  if [ -f "$candidate" ]; then
    SOURCE="$candidate"
    break
  fi
done

if [ -z "$SOURCE" ]; then
  echo "Cannot find built cozo-node shared library under target/release" >&2
  exit 1
fi

cp "$SOURCE" "$SCRIPT_DIR/native/cozo_bun.node"
echo "Native module copied to $SCRIPT_DIR/native/cozo_bun.node"

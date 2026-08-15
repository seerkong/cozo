#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

case "$(uname -s)" in
  Darwin) os="osx"; library="libcozo_c.dylib" ;;
  Linux) os="linux"; library="libcozo_c.so" ;;
  MINGW*|MSYS*|CYGWIN*) os="win"; library="cozo_c.dll" ;;
  *) echo "Unsupported operating system: $(uname -s)" >&2; exit 1 ;;
esac

case "$(uname -m)" in
  x86_64|amd64) arch="x64" ;;
  arm64|aarch64) arch="arm64" ;;
  *) echo "Unsupported CPU architecture: $(uname -m)" >&2; exit 1 ;;
esac

rid="$os-$arch"
cargo build --release -p cozo_c -F compact -F storage-rocksdb --manifest-path "$REPO_ROOT/Cargo.toml"

case "$os" in
  osx) source="$REPO_ROOT/target/release/libcozo_c.dylib" ;;
  linux) source="$REPO_ROOT/target/release/libcozo_c.so" ;;
  win) source="$REPO_ROOT/target/release/cozo_c.dll" ;;
esac

if [[ ! -f "$source" ]]; then
  echo "Could not find native output: $source" >&2
  exit 1
fi

mkdir -p "$SCRIPT_DIR/runtimes/$rid/native"
cp "$source" "$SCRIPT_DIR/runtimes/$rid/native/$library"
echo "Built Depa.Cozo native artifact: runtimes/$rid/native/$library"

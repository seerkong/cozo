#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

case "$(uname -s)" in
  Darwin) platform="darwin" ;;
  Linux) platform="linux" ;;
  MINGW*|MSYS*|CYGWIN*) platform="win32" ;;
  *) echo "Unsupported operating system: $(uname -s)" >&2; exit 1 ;;
esac

case "$(uname -m)" in
  x86_64|amd64) arch="x64" ;;
  arm64|aarch64) arch="arm64" ;;
  *) echo "Unsupported CPU architecture: $(uname -m)" >&2; exit 1 ;;
esac

target="$platform-$arch"
if [[ "$target" != "darwin-arm64" ]]; then
  echo "depa-cozo@0.1.0 supports only macOS arm64; refusing to build $target." >&2
  exit 1
fi

cargo build --release -p cozo-node -F compact -F storage-rocksdb --manifest-path "$REPO_ROOT/Cargo.toml"

for candidate in \
  "$REPO_ROOT/target/release/libcozo_node.dylib" \
  "$REPO_ROOT/target/release/libcozo_node.so" \
  "$REPO_ROOT/target/release/cozo_node.dll"; do
  if [[ -f "$candidate" ]]; then
    mkdir -p "$SCRIPT_DIR/native/$target"
    cp "$candidate" "$SCRIPT_DIR/native/$target/depa_cozo.node"
    echo "Built depa-cozo native artifact: native/$target/depa_cozo.node"
    exit 0
  fi
done

echo "Could not find the cozo-node native output." >&2
exit 1

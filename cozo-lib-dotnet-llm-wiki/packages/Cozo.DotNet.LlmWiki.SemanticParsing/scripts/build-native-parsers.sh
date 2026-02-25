#!/usr/bin/env bash
# Rebuild the tree-sitter native libraries bundled under runtimes/<rid>/native.
#
# Usage:
#   ./build-native-parsers.sh [rid]
#     rid: target runtime identifier, default osx-arm64 (the only one implemented so far;
#          other RIDs are reserved interface — see the case statement below).
#
# Environment:
#   TS_LOCAL_MIRROR: optional directory containing local clones named tree-sitter,
#                    tree-sitter-c-sharp, tree-sitter-typescript. Used as clone source
#                    when set (e.g. offline builds): TS_LOCAL_MIRROR=/tmp/spike-treesitter-pinvoke
#
# Pins (validated by spike /tmp/spike-treesitter-pinvoke, 2026-07-04):
#   tree-sitter runtime v0.27.0 -> supports grammar ABI [13, 15]
#   tree-sitter-c-sharp         -> generates ABI 15
#   tree-sitter-typescript      -> generates ABI 14
set -euo pipefail

TREE_SITTER_COMMIT=9fc2f486a8c1e1f5a4b1954cdcd240fcd09eb003   # v0.27.0
TREE_SITTER_CSHARP_COMMIT=af29416d729b7a6603101b513604392d8f675e3b
TREE_SITTER_TYPESCRIPT_COMMIT=75b3874edb2dc714fb1fd77a32013d0f8699989f

RID="${1:-osx-arm64}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
OUT_DIR="$SCRIPT_DIR/../runtimes/$RID/native"

case "$RID" in
  osx-arm64)
    LIB_EXT=dylib
    CFLAGS=(-shared -fPIC -O2 -arch arm64)
    ;;
  osx-x64|linux-x64|linux-arm64|win-x64)
    echo "RID '$RID' is a reserved interface: add its compiler flags/toolchain here before use." >&2
    exit 2
    ;;
  *)
    echo "Unknown RID '$RID'." >&2
    exit 2
    ;;
esac

WORK_DIR="$(mktemp -d /tmp/build-native-parsers.XXXXXX)"
trap 'rm -rf "$WORK_DIR"' EXIT

clone_pinned() {
  local name="$1" commit="$2" dest="$WORK_DIR/$1"
  if [[ -n "${TS_LOCAL_MIRROR:-}" && -d "$TS_LOCAL_MIRROR/$name/.git" ]]; then
    echo "==> $name: cloning from local mirror $TS_LOCAL_MIRROR/$name"
    git clone --quiet "$TS_LOCAL_MIRROR/$name" "$dest"
    git -C "$dest" checkout --quiet "$commit"
  else
    echo "==> $name: fetching pinned commit $commit from github.com/tree-sitter/$name"
    git init --quiet "$dest"
    git -C "$dest" remote add origin "https://github.com/tree-sitter/$name"
    git -C "$dest" fetch --quiet --depth 1 origin "$commit"
    git -C "$dest" checkout --quiet FETCH_HEAD
  fi
}

clone_pinned tree-sitter            "$TREE_SITTER_COMMIT"
clone_pinned tree-sitter-c-sharp    "$TREE_SITTER_CSHARP_COMMIT"
clone_pinned tree-sitter-typescript "$TREE_SITTER_TYPESCRIPT_COMMIT"

mkdir -p "$OUT_DIR"

echo "==> compiling libtree-sitter.$LIB_EXT"
cc "${CFLAGS[@]}" \
   -I "$WORK_DIR/tree-sitter/lib/include" -I "$WORK_DIR/tree-sitter/lib/src" \
   "$WORK_DIR/tree-sitter/lib/src/lib.c" \
   -o "$OUT_DIR/libtree-sitter.$LIB_EXT"

echo "==> compiling libtree-sitter-c-sharp.$LIB_EXT"
cc "${CFLAGS[@]}" \
   -I "$WORK_DIR/tree-sitter-c-sharp/src" \
   "$WORK_DIR/tree-sitter-c-sharp/src/parser.c" "$WORK_DIR/tree-sitter-c-sharp/src/scanner.c" \
   -o "$OUT_DIR/libtree-sitter-c-sharp.$LIB_EXT"

echo "==> compiling libtree-sitter-typescript.$LIB_EXT"
cc "${CFLAGS[@]}" \
   -I "$WORK_DIR/tree-sitter-typescript/typescript/src" \
   "$WORK_DIR/tree-sitter-typescript/typescript/src/parser.c" "$WORK_DIR/tree-sitter-typescript/typescript/src/scanner.c" \
   -o "$OUT_DIR/libtree-sitter-typescript.$LIB_EXT"

echo "==> done:"
ls -la "$OUT_DIR"

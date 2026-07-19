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
#                    tree-sitter-c-sharp, tree-sitter-typescript, tree-sitter-java.
#                    Used as archive source for the pinned commits
#                    when set (e.g. offline builds): TS_LOCAL_MIRROR=/tmp/spike-treesitter-pinvoke
#
# Pins (validated by spike /tmp/spike-treesitter-pinvoke, 2026-07-04):
#   tree-sitter runtime v0.27.0 -> supports grammar ABI [13, 15]
#   tree-sitter-c-sharp         -> generates ABI 15
#   tree-sitter-typescript      -> generates ABI 14
#   tree-sitter-java v0.23.5    -> generates ABI 14
set -euo pipefail

TREE_SITTER_COMMIT=9fc2f486a8c1e1f5a4b1954cdcd240fcd09eb003   # v0.27.0
TREE_SITTER_CSHARP_COMMIT=af29416d729b7a6603101b513604392d8f675e3b
TREE_SITTER_TYPESCRIPT_COMMIT=75b3874edb2dc714fb1fd77a32013d0f8699989f
TREE_SITTER_JAVA_COMMIT=94703d5a6bed02b98e438d7cad1136c01a60ba2c # v0.23.5

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
    echo "==> $name: exporting pinned commit $commit from local mirror $TS_LOCAL_MIRROR/$name"
    mkdir -p "$dest"
    git -C "$TS_LOCAL_MIRROR/$name" archive --format=tar "$commit" | tar -x -C "$dest"
  else
    echo "==> $name: fetching pinned commit $commit from github.com/tree-sitter/$name"
    local repo="$WORK_DIR/$name.git"
    git init --quiet "$repo"
    git -C "$repo" remote add origin "https://github.com/tree-sitter/$name"
    git -C "$repo" fetch --quiet --depth 1 origin "$commit"
    mkdir -p "$dest"
    git -C "$repo" archive --format=tar FETCH_HEAD | tar -x -C "$dest"
  fi
}

compile_grammar() {
  local name="$1" src_dir="$2" out_file="$3"
  local sources=("$src_dir/parser.c")
  if [[ -f "$src_dir/scanner.c" ]]; then
    sources+=("$src_dir/scanner.c")
  fi

  echo "==> compiling $out_file"
  cc "${CFLAGS[@]}" \
     -I "$src_dir" \
     "${sources[@]}" \
     -o "$OUT_DIR/$out_file"
}

clone_pinned tree-sitter            "$TREE_SITTER_COMMIT"
clone_pinned tree-sitter-c-sharp    "$TREE_SITTER_CSHARP_COMMIT"
clone_pinned tree-sitter-typescript "$TREE_SITTER_TYPESCRIPT_COMMIT"
clone_pinned tree-sitter-java       "$TREE_SITTER_JAVA_COMMIT"

mkdir -p "$OUT_DIR"

echo "==> compiling libtree-sitter.$LIB_EXT"
cc "${CFLAGS[@]}" \
   -I "$WORK_DIR/tree-sitter/lib/include" -I "$WORK_DIR/tree-sitter/lib/src" \
   "$WORK_DIR/tree-sitter/lib/src/lib.c" \
   -o "$OUT_DIR/libtree-sitter.$LIB_EXT"

compile_grammar tree-sitter-c-sharp "$WORK_DIR/tree-sitter-c-sharp/src" "libtree-sitter-c-sharp.$LIB_EXT"
compile_grammar tree-sitter-typescript "$WORK_DIR/tree-sitter-typescript/typescript/src" "libtree-sitter-typescript.$LIB_EXT"
compile_grammar tree-sitter-java "$WORK_DIR/tree-sitter-java/src" "libtree-sitter-java.$LIB_EXT"

echo "==> done:"
ls -la "$OUT_DIR"

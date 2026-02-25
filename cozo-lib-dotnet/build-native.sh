#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

detect_host_rid() {
    local os arch

    case "$(uname -s)" in
        Darwin) os="osx" ;;
        Linux) os="linux" ;;
        MINGW*|MSYS*|CYGWIN*) os="win" ;;
        *) echo "Unsupported OS: $(uname -s)" >&2; exit 1 ;;
    esac

    case "$(uname -m)" in
        x86_64|amd64) arch="x64" ;;
        arm64|aarch64) arch="arm64" ;;
        *) echo "Unsupported arch: $(uname -m)" >&2; exit 1 ;;
    esac

    echo "${os}-${arch}"
}

map_rid() {
    local rid="$1"
    case "$rid" in
        osx-arm64) echo "aarch64-apple-darwin libcozo_c.dylib" ;;
        osx-x64) echo "x86_64-apple-darwin libcozo_c.dylib" ;;
        linux-x64) echo "x86_64-unknown-linux-gnu libcozo_c.so" ;;
        linux-arm64) echo "aarch64-unknown-linux-gnu libcozo_c.so" ;;
        win-x64) echo "x86_64-pc-windows-msvc cozo_c.dll" ;;
        *) echo "Unsupported RID: $rid" >&2; exit 1 ;;
    esac
}

HOST_RID="$(detect_host_rid)"
RID="${1:-$HOST_RID}"

read -r TARGET_TRIPLE LIBNAME <<< "$(map_rid "$RID")"

BUILD_PATH=""
if [[ "$RID" == "$HOST_RID" ]]; then
    echo "Building cozo_c (release) for host RID=$RID ..."
    cargo build --release -p cozo_c -F compact -F storage-rocksdb --manifest-path "$REPO_ROOT/Cargo.toml"
    BUILD_PATH="$REPO_ROOT/target/release/$LIBNAME"
else
    echo "Building cozo_c (release) for RID=$RID (target=$TARGET_TRIPLE) ..."
    cargo build --release -p cozo_c -F compact -F storage-rocksdb --target "$TARGET_TRIPLE" --manifest-path "$REPO_ROOT/Cargo.toml"
    BUILD_PATH="$REPO_ROOT/target/$TARGET_TRIPLE/release/$LIBNAME"
fi

if [[ ! -f "$BUILD_PATH" ]]; then
    echo "ERROR: Expected native library not found at $BUILD_PATH" >&2
    exit 1
fi

DEST_DIR="$SCRIPT_DIR/runtimes/$RID/native"
mkdir -p "$DEST_DIR"
cp -v "$BUILD_PATH" "$DEST_DIR/$LIBNAME"

echo "Done: native library copied to $DEST_DIR/$LIBNAME"

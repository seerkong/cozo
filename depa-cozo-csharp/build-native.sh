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
# Host build only. The artifact lands in runtimes/<rid>/native/, which is the
# NuGet layout the loader and pack step already use.

# Git Bash puts /usr/bin ahead of MSVC, so coreutils `link` shadows the real
# linker. Import vcvars64 so the Windows host build uses the Visual Studio
# linker and Windows SDK headers.
setup_windows_msvc() {
  local vswhere="/c/Program Files (x86)/Microsoft Visual Studio/Installer/vswhere.exe"
  local install unix_install msvc_bin vcvars tmp
  if [[ ! -x "$vswhere" ]]; then
    echo "vswhere.exe not found; install Visual Studio with C++ tools." >&2
    exit 1
  fi
  install="$("$vswhere" -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath | tr -d '\r')"
  unix_install="$(cygpath -u "$install")"
  msvc_bin="$(ls -d "$unix_install"/VC/Tools/MSVC/*/bin/Hostx64/x64 2>/dev/null | tail -1)"
  vcvars="$unix_install/VC/Auxiliary/Build/vcvars64.bat"
  if [[ -z "$msvc_bin" || ! -f "$vcvars" ]]; then
    echo "MSVC x64 tools not found under $install" >&2
    exit 1
  fi
  export PATH="$msvc_bin:$PATH"
  if [[ -n "${INCLUDE:-}" && -n "${LIB:-}" ]]; then
    return 0
  fi
  tmp="$(mktemp /tmp/depa-vcvars.XXXXXX.bat)"
  printf '@echo off\r\nset "PATH=C:\\Program Files (x86)\\Microsoft Visual Studio\\Installer;%%PATH%%"\r\ncall "%s" >nul\r\nset INCLUDE\r\nset LIB\r\nset LIBPATH\r\n' "$(cygpath -w "$vcvars")" > "$tmp"
  while IFS= read -r line; do
    local key="${line%%=*}"
    local val="${line#*=}"
    case "$key" in
      INCLUDE|LIB|LIBPATH) export "$key=$val" ;;
    esac
  done < <(cmd.exe //C "$(cygpath -w "$tmp")" | tr -d '\r')
  rm -f "$tmp"
  if [[ -z "${INCLUDE:-}" || -z "${LIB:-}" ]]; then
    echo "Failed to import MSVC INCLUDE/LIB from vcvars64.bat" >&2
    exit 1
  fi
}

if [[ "$os" == "win" ]]; then
  setup_windows_msvc
fi

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

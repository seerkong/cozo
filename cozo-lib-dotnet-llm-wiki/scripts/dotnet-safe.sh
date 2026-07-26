#!/usr/bin/env bash
# Run finite .NET build/test commands without retaining MSBuild worker processes.
# Do not use this wrapper for long-lived `dotnet watch` or application servers.
set -euo pipefail

if [[ $# -eq 0 ]]; then
  echo "usage: scripts/dotnet-safe.sh <dotnet arguments...>" >&2
  exit 64
fi

if [[ "${1}" == "watch" ]]; then
  echo "dotnet-safe.sh is intentionally limited to finite commands; run dotnet watch directly." >&2
  exit 64
fi

if [[ -n "${DOTNET_HOST_PATH:-}" && -x "${DOTNET_HOST_PATH}" ]]; then
  dotnet_bin="${DOTNET_HOST_PATH}"
elif command -v dotnet >/dev/null 2>&1; then
  dotnet_bin="$(command -v dotnet)"
elif [[ -x "/usr/local/share/dotnet/dotnet" ]]; then
  dotnet_bin="/usr/local/share/dotnet/dotnet"
else
  echo "dotnet host was not found; set DOTNET_HOST_PATH." >&2
  exit 127
fi

# The 2026-07-19 recursive CLI-test incident also exposed many idle nodeReuse workers.
# This environment setting applies before MSBuild starts, unlike a project property.
export MSBUILDDISABLENODEREUSE=1

# The monolithic LLM Wiki test executable is expensive to compile with every Roslyn analyzer
# under memory pressure. The test project consumes this flag only for local wrapper-driven
# work; CI and direct dotnet invocations retain analyzers. Set it to 0 for a local full check.
export LLM_WIKI_FAST_LOCAL_TESTS="${LLM_WIKI_FAST_LOCAL_TESTS:-1}"

cleanup() {
  "${dotnet_bin}" build-server shutdown >/dev/null 2>&1 || true
}
trap cleanup EXIT INT TERM

args=("$@")
case "${args[0]}" in
  build|run|test|publish)
    if [[ " ${args[*]} " != *" --disable-build-servers "* ]]; then
      args+=("--disable-build-servers")
    fi
    ;;
esac

case "${args[0]}" in
  build|test)
    # A solution build otherwise starts many compiler processes at once on a cold workspace.
    if [[ " ${args[*]} " != *" -m:"* && " ${args[*]} " != *" -maxcpucount:"* && " ${args[*]} " != *" /m:"* ]]; then
      args+=("-m:1")
    fi
    ;;
esac

"${dotnet_bin}" "${args[@]}"

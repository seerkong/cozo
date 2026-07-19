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

cleanup() {
  "${dotnet_bin}" build-server shutdown >/dev/null 2>&1 || true
}
trap cleanup EXIT INT TERM

"${dotnet_bin}" "$@"

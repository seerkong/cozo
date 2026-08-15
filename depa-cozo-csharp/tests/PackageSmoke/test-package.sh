#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PACKAGE_DIR="$(cd "$SCRIPT_DIR/../.." && pwd)"
LOCAL_FEED="$SCRIPT_DIR/../local-packages"
VERSION="${DEPA_COZO_VERSION:-0.1.0}"
NUGET_CACHE="$(mktemp -d /tmp/depa-cozo-nuget-cache.XXXXXX)"

mkdir -p "$LOCAL_FEED"
dotnet pack "$PACKAGE_DIR/Depa.Cozo.csproj" --configuration Release --output "$LOCAL_FEED" -p:PackageVersion="$VERSION"
NUGET_PACKAGES="$NUGET_CACHE" dotnet restore "$SCRIPT_DIR/PackageSmoke.csproj" --configfile "$SCRIPT_DIR/NuGet.Config" --force --no-cache -p:DepaCozoVersion="$VERSION"
NUGET_PACKAGES="$NUGET_CACHE" dotnet run --project "$SCRIPT_DIR/PackageSmoke.csproj" --configuration Release --no-restore -p:DepaCozoVersion="$VERSION"

echo "Verified .NET Core loading from the packed Depa.Cozo NuGet package."

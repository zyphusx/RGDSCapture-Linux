#!/usr/bin/env bash
#
# Builds the RGDSCapture Flatpak.
#
# Two stages, deliberately: the .NET app is published on the host first, then
# flatpak-builder assembles the bundle from that output. Building .NET inside
# the sandbox would mean depending on a org.freedesktop.Sdk.Extension.dotnet
# matching this project's target framework, and restoring NuGet packages
# during a build that is supposed to be offline. Publishing self-contained
# sidesteps both: the runtime ships inside the bundle.
#
# Usage:
#   ./build.sh            build and install for the current user
#   ./build.sh --bundle   also write a single-file .flatpak for distribution
#
set -euo pipefail

APP_ID="io.github.zyphusx.RGDSCapture"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
RID="${RID:-linux-x64}"

need() {
    command -v "$1" >/dev/null 2>&1 || {
        echo "error: $1 is required but not installed." >&2
        case "$1" in
            flatpak-builder) echo "  sudo dnf install flatpak-builder" >&2 ;;
            dotnet)          echo "  see https://dotnet.microsoft.com/download" >&2 ;;
        esac
        exit 1
    }
}

need dotnet
need flatpak
need flatpak-builder

echo "==> Installing the Flatpak runtime and SDK"
flatpak install --user --noninteractive --or-update flathub \
    org.freedesktop.Platform//24.08 \
    org.freedesktop.Sdk//24.08

echo "==> Publishing RGDSCapture ($RID, self-contained)"
rm -rf "$ROOT/publish"
dotnet publish "$ROOT/RGDSCapture.csproj" \
    --configuration Release \
    --runtime "$RID" \
    --self-contained true \
    -p:PublishSingleFile=false \
    -p:DebugType=none \
    --output "$ROOT/publish"

echo "==> Building the Flatpak"
cd "$HERE"
rm -rf build-dir
flatpak-builder --user --install --force-clean \
    --state-dir "$ROOT/.flatpak-builder" \
    build-dir "$APP_ID.yml"

if [[ "${1:-}" == "--bundle" ]]; then
    echo "==> Writing $APP_ID.flatpak"
    rm -rf repo
    flatpak-builder --repo=repo --force-clean \
        --state-dir "$ROOT/.flatpak-builder" \
        build-dir "$APP_ID.yml"
    flatpak build-bundle repo "$ROOT/$APP_ID.flatpak" "$APP_ID"
    echo "    -> $ROOT/$APP_ID.flatpak"
fi

echo
echo "Done. Run it with:"
echo "    flatpak run $APP_ID"

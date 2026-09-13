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

# Avalonia's build task phones home. A Flatpak build is meant to be offline,
# so without this every build spends time on connections that cannot succeed.
# This is the environment variable the build task reads; the MSBuild property
# that looks like it should do the same is not referenced by the package.
export AVALONIA_TELEMETRY_OPTOUT=1

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
need curl
need sha256sum

# ── FFmpeg tarball checksum ───────────────────────────────────────────
# The manifest ships a placeholder sha256: the machine the Linux port was
# written on could not reach ffmpeg.org, and a guessed checksum fails the
# build looking like a corrupt download.
#
# The real value is resolved here and written to a GENERATED copy of the
# manifest, never to the tracked one. Writing it back into the tracked file
# is the obvious shortcut and it breaks `git pull`: the working tree is dirty
# from then on, so the next pull refuses to merge and quietly leaves the user
# building a stale manifest. The copy lives beside the original so that the
# relative source paths inside it still resolve.
MANIFEST_SRC="$HERE/$APP_ID.yml"
MANIFEST="$HERE/.$APP_ID.generated.yml"
PLACEHOLDER="0000000000000000000000000000000000000000000000000000000000000000"
SUMFILE="$ROOT/.flatpak-builder/ffmpeg.sha256"

FFMPEG_URL="$(grep -oP 'url: \K\S+ffmpeg-[0-9.]+\.tar\.xz' "$MANIFEST_SRC")"

if [[ -s "$SUMFILE" ]] && [[ "$(head -n1 "$SUMFILE" | cut -d" " -f2-)" == "$FFMPEG_URL" ]]; then
    SUM="$(head -n1 "$SUMFILE" | cut -d" " -f1)"
    echo "==> FFmpeg checksum (cached): $SUM"
else
    echo "==> Fetching the FFmpeg checksum (one time)"
    echo "    $FFMPEG_URL"
    TMPDIR_DL="$(mktemp -d)"
    curl --fail --location --progress-bar \
         --output "$TMPDIR_DL/$(basename "$FFMPEG_URL")" "$FFMPEG_URL"
    SUM="$(sha256sum "$TMPDIR_DL/$(basename "$FFMPEG_URL")" | cut -d" " -f1)"
    rm -rf "$TMPDIR_DL"
    mkdir -p "$(dirname "$SUMFILE")"
    echo "$SUM $FFMPEG_URL" > "$SUMFILE"
    echo "    -> $SUM"
fi

sed "s|sha256: $PLACEHOLDER|sha256: $SUM|" "$MANIFEST_SRC" > "$MANIFEST"

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
    build-dir "$MANIFEST"

if [[ "${1:-}" == "--bundle" ]]; then
    echo "==> Writing $APP_ID.flatpak"
    rm -rf repo
    flatpak-builder --repo=repo --force-clean \
        --state-dir "$ROOT/.flatpak-builder" \
        build-dir "$MANIFEST"
    flatpak build-bundle repo "$ROOT/$APP_ID.flatpak" "$APP_ID"
    echo "    -> $ROOT/$APP_ID.flatpak"
fi

echo
echo "Done. Run it with:"
echo "    flatpak run $APP_ID"

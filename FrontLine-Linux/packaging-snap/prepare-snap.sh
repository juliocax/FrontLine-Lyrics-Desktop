#!/usr/bin/env bash
# Prepare a Snapcraft project from the previously verified .deb artifact.
set -Eeuo pipefail
HERE="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
LINUX_DIR="$(realpath "$HERE/..")"
ROOT="$(realpath "$LINUX_DIR/..")"
ARCH="$(dpkg --print-architecture)"
DEB="${1:-$LINUX_DIR/dist-linux/frontline-lyrics_1.0.0-1_${ARCH}.deb}"
[[ -f "$DEB" ]] || { echo "No .deb found: $DEB" >&2; echo "Build it with: $LINUX_DIR/packaging-deb/build-deb.sh" >&2; exit 1; }
[[ "$ARCH" == amd64 ]] || { echo 'This prototype currently targets amd64 only' >&2; exit 1; }
command -v dpkg-deb >/dev/null || { echo 'dpkg-deb is required' >&2; exit 1; }
[[ -f "$HERE/snap/snapcraft.yaml" ]] || { echo 'snapcraft.yaml missing' >&2; exit 1; }
[[ -x "$HERE/frontline-lyrics-snap.sh" ]] || { echo 'Snap launcher missing or not executable (chmod +x)' >&2; exit 1; }
PAYLOAD="$HERE/payload"
rm -rf "$PAYLOAD"
mkdir -p "$PAYLOAD/bin" "$PAYLOAD/usr/share/applications" "$PAYLOAD/meta/gui" "$HERE/snap/gui"
dpkg-deb -x "$DEB" "$PAYLOAD"
[[ -x "$PAYLOAD/opt/frontline-lyrics/frontend/FrontLine-Linux" ]] || { echo 'Frontend not found' >&2; exit 1; }
[[ -x "$PAYLOAD/opt/frontline-lyrics/server/FrontlineServer" ]] || { echo 'Backend not found' >&2; exit 1; }
install -m 0755 "$HERE/frontline-lyrics-snap.sh" "$PAYLOAD/bin/frontline-lyrics-snap"
# Snapcraft desktop metadata: local executable command, not /usr/bin from host.
cat > "$PAYLOAD/usr/share/applications/frontline-lyrics.desktop" <<'DESKTOP'
[Desktop Entry]
Type=Application
Name=FrontLine Lyrics
Comment=Synchronized desktop lyrics
Exec=frontline-lyrics
Icon=${SNAP}/usr/share/icons/hicolor/256x256/apps/frontline-lyrics.png
Terminal=false
Categories=AudioVideo;Audio;
StartupNotify=true
DESKTOP
# Template expansion for Snap desktop is supported when imported into snap metadata;
# provide GUI fallback icon for Snapcraft's app icon discovery.
if [[ -f "$PAYLOAD/usr/share/icons/hicolor/256x256/apps/frontline-lyrics.png" ]]; then
  cp "$PAYLOAD/usr/share/icons/hicolor/256x256/apps/frontline-lyrics.png" "$HERE/snap/gui/frontline-lyrics.png"
fi
[[ -s "$PAYLOAD/bin/frontline-lyrics-snap" ]] || { echo "Snap launcher missing" >&2; exit 1; }
[[ -f "$PAYLOAD/usr/share/applications/frontline-lyrics.desktop" ]] || { echo "Desktop entry missing" >&2; exit 1; }
printf '\nReady: %s\n' "$HERE"
printf 'Now run: cd "%s" && snapcraft pack\n' "$HERE"
printf 'Use: sudo snap install ./frontline-lyrics_1.0.0_amd64.snap --dangerous\n'

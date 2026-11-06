#!/usr/bin/env bash
set -Eeuo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(realpath "${1:-$SCRIPT_DIR/../..}")"
APP="$ROOT/FrontLine-Linux"
SERVER="$ROOT/FrontlineServer"
OUT="$APP/dist-linux"
STAGE="$OUT/.frontline-deb-stage"
VERSION="${FRONTLINE_PACKAGE_VERSION:-1.0.0-1}"
APP_VERSION="${VERSION%-*}"
PACKAGE="frontline-lyrics"

fail() { printf '\nERROR: %s\n' "$*" >&2; exit 1; }
for command_name in dotnet dpkg dpkg-deb realpath; do
  command -v "$command_name" >/dev/null || fail "Missing tool: $command_name"
done
[[ -f "$APP/FrontLine-Linux.csproj" ]] || fail "Missing $APP/FrontLine-Linux.csproj"
[[ -f "$SERVER/FrontlineServer.py" ]] || fail "Missing $SERVER/FrontlineServer.py"
[[ -f "$APP/assets/logo.png" ]] || fail "Missing $APP/assets/logo.png"

if [[ -n "${FRONTLINE_PYTHON:-}" ]]; then
  PY="$FRONTLINE_PYTHON"
elif [[ -x "$ROOT/.venv/bin/python" ]]; then
  PY="$ROOT/.venv/bin/python"
elif [[ -x "$SERVER/.venv/bin/python" ]]; then
  PY="$SERVER/.venv/bin/python"
else
  fail "No Python venv found at ROOT/.venv or FrontlineServer/.venv. Set FRONTLINE_PYTHON."
fi
"$PY" -m PyInstaller --version >/dev/null || fail "PyInstaller missing: $PY -m pip install pyinstaller"

ARCH="$(dpkg --print-architecture)"
case "$ARCH" in
  amd64) RID=linux-x64 ;;
  arm64) RID=linux-arm64 ;;
  *) fail "Unsupported architecture: $ARCH" ;;
esac

mkdir -p "$OUT/py-spec"
printf '[1/5] Publishing Avalonia %s (%s)...\n' "$APP_VERSION" "$RID"
dotnet publish "$APP/FrontLine-Linux.csproj" -c Release -r "$RID" \
  --self-contained true -p:UseAppHost=true -p:Version="$APP_VERSION" \
  -o "$OUT/frontend"
[[ -x "$OUT/frontend/FrontLine-Linux" ]] || fail "Missing published Avalonia executable"

printf '[2/5] Bundling Python backend with PyInstaller...\n'
(
  cd "$SERVER"
  "$PY" -m PyInstaller --noconfirm --clean --onedir \
    --name FrontlineServer \
    --distpath "$OUT/py-dist" \
    --workpath "$OUT/py-work" \
    --specpath "$OUT/py-spec" \
    --paths "$SERVER" \
    --collect-all shazamio \
    --collect-all deep_translator \
    --collect-all translators \
    --collect-all anyascii \
    FrontlineServer.py
)
[[ -x "$OUT/py-dist/FrontlineServer/FrontlineServer" ]] || fail "Missing bundled backend"

printf '[3/5] Staging Debian package...\n'
rm -rf "$STAGE"
mkdir -p "$STAGE/DEBIAN" "$STAGE/opt/frontline-lyrics/frontend" \
  "$STAGE/opt/frontline-lyrics/server" "$STAGE/usr/bin" \
  "$STAGE/usr/share/applications" \
  "$STAGE/usr/share/icons/hicolor/256x256/apps"
cp -a "$OUT/frontend/." "$STAGE/opt/frontline-lyrics/frontend/"
cp -a "$OUT/py-dist/FrontlineServer/." "$STAGE/opt/frontline-lyrics/server/"
cp "$APP/assets/logo.png" "$STAGE/usr/share/icons/hicolor/256x256/apps/frontline-lyrics.png"
install -m 0755 "$SCRIPT_DIR/frontline-lyrics-launcher.sh" "$STAGE/usr/bin/frontline-lyrics"
install -m 0644 "$SCRIPT_DIR/frontline-lyrics.desktop" "$STAGE/usr/share/applications/frontline-lyrics.desktop"
cat > "$STAGE/DEBIAN/control" <<CONTROL
Package: $PACKAGE
Version: $VERSION
Section: sound
Priority: optional
Architecture: $ARCH
Maintainer: FrontLine Lyrics <https://github.com/juliocax/FrontLine-Lyrics-Desktop>
Depends: libc6, libstdc++6, libgcc-s1, playerctl
Description: FrontLine Lyrics for Linux ($APP_VERSION)
 Synchronized desktop lyrics with local Python recognition and translation engine.
 Includes self-contained Avalonia frontend and bundled Python backend.
CONTROL

printf '[4/5] Checking package layout...\n'
[[ -f "$STAGE/opt/frontline-lyrics/frontend/FrontLine-Linux" ]] || fail 'Frontend missing in package'
[[ -f "$STAGE/opt/frontline-lyrics/server/FrontlineServer" ]] || fail 'Server missing in package'
[[ -f "$STAGE/usr/share/applications/frontline-lyrics.desktop" ]] || fail 'Desktop entry missing'

printf '[5/5] Building .deb...\n'
DEB="$OUT/${PACKAGE}_${VERSION}_${ARCH}.deb"
dpkg-deb --root-owner-group --build "$STAGE" "$DEB"
dpkg-deb --info "$DEB"
printf '\nDONE: %s\n' "$DEB"
printf 'INSTALL: sudo apt install "%s"\n' "$DEB"
printf 'RUN: frontline-lyrics\n'

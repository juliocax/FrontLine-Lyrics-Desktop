#!/usr/bin/env bash
# Build .deb -> payload -> .snap from anywhere in the repository.
set -Eeuo pipefail
HERE="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(realpath "$HERE/../..")"
LINUX="$ROOT/FrontLine-Linux"
ARCH="$(dpkg --print-architecture)"
VERSION="${1:-1.0.0}"
[[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+([.-][0-9A-Za-z.-]+)?$ ]] || { echo "Invalid version: $VERSION" >&2; exit 2; }
[[ "$ARCH" == amd64 ]] || { echo 'Only amd64 is configured in snapcraft.yaml' >&2; exit 2; }
for tool in snapcraft dpkg-deb realpath; do command -v "$tool" >/dev/null || { echo "Missing: $tool" >&2; exit 2; }; done
DEB="$LINUX/dist-linux/frontline-lyrics_${VERSION}-1_${ARCH}.deb"
SNAP="$HERE/frontline-lyrics_${VERSION}_${ARCH}.snap"
echo "==> Building .deb: $VERSION"
FRONTLINE_PACKAGE_VERSION="${VERSION}-1" "$LINUX/packaging-deb/build-deb.sh"
[[ -s "$DEB" ]] || { echo "Expected .deb missing: $DEB" >&2; exit 1; }
echo '==> Preparing Snap payload'
"$HERE/prepare-snap.sh" "$DEB"
[[ -x "$HERE/payload/bin/frontline-lyrics-snap" ]] || { echo 'Snap payload launcher missing' >&2; exit 1; }
# Update only the version field; preserve the rest of snapcraft.yaml.
# grade remains devel unless the maintainer deliberately changes it.
python3 - "$HERE/snap/snapcraft.yaml" "$VERSION" <<'PYVERSION'
import pathlib, re, sys
p=pathlib.Path(sys.argv[1]); s=p.read_text(); v=sys.argv[2]
s2,n=re.subn(r"(?m)^version:\s*[^\n]+$", "version: '"+v+"'", s, count=1)
if n!=1: raise SystemExit('snapcraft.yaml must have a version field')
p.write_text(s2)
PYVERSION
rm -f -- "$SNAP"
cd "$HERE"
echo '==> Cleaning Snapcraft part'
snapcraft clean packaged-app
echo '==> Packing Snap'
snapcraft pack
[[ -s "$SNAP" ]] || { echo "Snap not found: $SNAP" >&2; exit 1; }
echo "DONE: $SNAP"
ls -lh "$SNAP"

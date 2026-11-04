#!/usr/bin/env bash
# Subsequent versions: build, verify and publish to edge.
set -Eeuo pipefail
HERE="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
[[ $# -eq 1 ]] || { echo "Usage: $0 1.0.1" >&2; exit 2; }
VERSION="$1"
SNAP="$HERE/frontline-lyrics_${VERSION}_amd64.snap"
"$HERE/build-snap.sh" "$VERSION"
echo "==> Publishing UPDATE $VERSION to EDGE"
read -r -p 'Did you test this build? Type PUBLISH to upload: ' answer
[[ "$answer" == PUBLISH ]] || { echo 'Upload cancelled'; exit 0; }
snapcraft upload --release=edge "$SNAP"
echo 'Update uploaded to edge subject to Store review. Test before promoting to stable.'

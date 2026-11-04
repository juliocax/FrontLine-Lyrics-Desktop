#!/usr/bin/env bash
# First time only: login, register, build and publish to edge.
set -Eeuo pipefail
HERE="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
VERSION="${1:-1.0.0}"
SNAP="$HERE/frontline-lyrics_${VERSION}_amd64.snap"
command -v snapcraft >/dev/null || { echo 'Install snapcraft first' >&2; exit 2; }
echo '==> Log in to your Ubuntu One / Snap Store account'
snapcraft login
echo '==> Register snap name (only once)'
read -r -p 'Is frontline-lyrics already registered to your account? [y/N] ' answer
if [[ "$answer" != [yY] && "$answer" != [yY][eE][sS] ]]; then snapcraft register frontline-lyrics; fi
"$HERE/build-snap.sh" "$VERSION"
echo '==> Publishing FIRST revision to EDGE (not stable)'
read -r -p 'Did you test the SNAP and want to upload to edge now? Type PUBLISH: ' answer
[[ "$answer" == PUBLISH ]] || { echo 'Upload cancelled'; exit 0; }
snapcraft upload --release=edge "$SNAP"
echo 'Check review status in Snap Store dashboard. Stable requires a separately tested promotion.'

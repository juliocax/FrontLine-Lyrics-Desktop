#!/usr/bin/env bash
set -Eeuo pipefail
APP_DIR="$SNAP/opt/frontline-lyrics"
SERVER="$APP_DIR/server/FrontlineServer"
FRONTEND="$APP_DIR/frontend/FrontLine-Linux"
STATE_DIR="${SNAP_USER_COMMON:-${XDG_STATE_HOME:-$HOME/.local/state}/frontline-lyrics}/logs"
mkdir -p "$STATE_DIR"
exec >>"$STATE_DIR/launcher.log" 2>&1
printf '[%s] Starting FrontLine Lyrics snap\n' "$(date -Is)"
[[ -x "$SERVER" && -x "$FRONTEND" ]] || { echo 'Missing bundled binaries'; exit 1; }
exec 9>"$STATE_DIR/launch.lock"
if ! flock -n 9; then echo 'An instance is already running'; exit 0; fi
port_open() { (echo >/dev/tcp/127.0.0.1/8765) >/dev/null 2>&1; }
if port_open; then echo 'Port 8765 is occupied by another process'; exit 1; fi
SERVER_PID=''
cleanup() {
    trap - EXIT INT TERM
    if [[ -n "$SERVER_PID" ]]; then
        kill "$SERVER_PID" 2>/dev/null || true
        wait "$SERVER_PID" 2>/dev/null || true
    fi
}
trap cleanup EXIT INT TERM
(
    cd "$APP_DIR/server"
    exec "$SERVER"
) >>"$STATE_DIR/server-stdout.log" 2>&1 &
SERVER_PID=$!
READY=0
for ((i=0; i<100; i++)); do
    kill -0 "$SERVER_PID" 2>/dev/null || { echo 'Backend exited unexpectedly'; exit 1; }
    if port_open; then READY=1; break; fi
    sleep .2
done
[[ "$READY" == 1 ]] || { echo 'Backend did not open port 8765'; exit 1; }
cd "$SNAP_USER_DATA"
"$FRONTEND"

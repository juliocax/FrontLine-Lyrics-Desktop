#!/usr/bin/env bash
set -Eeuo pipefail

APP_DIR=/opt/frontline-lyrics
SERVER="$APP_DIR/server/FrontlineServer"
FRONTEND="$APP_DIR/frontend/FrontLine-Linux"
STATE_DIR="${XDG_STATE_HOME:-$HOME/.local/state}/frontline-lyrics"
RUNTIME_DIR="${XDG_RUNTIME_DIR:-$STATE_DIR}"
mkdir -p "$STATE_DIR" "$RUNTIME_DIR"
LOG="$STATE_DIR/launcher.log"
SERVER_LOG="$STATE_DIR/server-stdout.log"
exec >>"$LOG" 2>&1

echo "[$(date -Is)] Launching FrontLine Lyrics"

[[ -x "$SERVER" ]] || { echo 'Bundled Python server not executable'; exit 1; }
[[ -x "$FRONTEND" ]] || { echo 'Avalonia executable not found'; exit 1; }
command -v flock >/dev/null || { echo 'flock not found (util-linux)'; exit 1; }

exec 9>"$RUNTIME_DIR/frontline-lyrics.lock"
if ! flock -n 9; then
  echo 'An instance of FrontLine Lyrics is already running.'
  exit 0
fi

port_open() { (echo >/dev/tcp/127.0.0.1/8765) >/dev/null 2>&1; }
if port_open; then
  echo 'Port 8765 is already occupied. Stop the development server before starting the installed app.'
  exit 1
fi

SERVER_PID=''
cleanup() {
  trap - EXIT INT TERM
  if [[ -n "$SERVER_PID" ]]; then
    echo "Stopping bundled Python server (PID $SERVER_PID)"
    kill "$SERVER_PID" 2>/dev/null || true
    wait "$SERVER_PID" 2>/dev/null || true
  fi
}
trap cleanup EXIT INT TERM

(
  cd "$APP_DIR/server"
  exec "$SERVER"
) >>"$SERVER_LOG" 2>&1 &
SERVER_PID=$!

echo "Started Python backend (PID $SERVER_PID)"
READY=0
for ((attempt=0; attempt<100; attempt++)); do
  if ! kill -0 "$SERVER_PID" 2>/dev/null; then
    echo 'Backend exited unexpectedly; inspect server-stdout.log.'
    exit 1
  fi
  if port_open; then READY=1; break; fi
  sleep 0.2
done
if [[ "$READY" -ne 1 ]]; then
  echo 'Backend did not listen on 127.0.0.1:8765 within 20 seconds.'
  exit 1
fi

cd "$HOME"
"$FRONTEND"

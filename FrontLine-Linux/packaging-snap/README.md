# FrontLine Lyrics 1.0.0 — Snap packaging

This directory is **inside** `FrontLine-Linux/`. The Snap package currently uses a `.deb` as an intermediate build artifact; the `.deb` is not distributed in the Snap Store.

## From the repository root

```bash
cd ~/Documentos/FrontLine-Lyrics-Desktop
source .venv/bin/activate
chmod +x FrontLine-Linux/packaging-deb/*.sh FrontLine-Linux/packaging-snap/*.sh
./FrontLine-Linux/packaging-deb/build-deb.sh
./FrontLine-Linux/packaging-snap/prepare-snap.sh
cd FrontLine-Linux/packaging-snap
snapcraft clean packaged-app
snapcraft pack
ls -lh *.snap
```

If the `.deb` has **already** been built, start at `prepare-snap.sh`. If it is located somewhere else, pass the file's absolute path as the first argument.

## Test local snap

```bash
sudo snap install ./frontline-lyrics_1.0.0_amd64.snap --dangerous
sudo snap connect frontline-lyrics:audio-record
snap run frontline-lyrics
snap connections frontline-lyrics
```

If already installed, `sudo snap remove frontline-lyrics` first, or try `sudo snap install --dangerous ...` with a fresh revision; beware that removing a snap may affect user data. Check the actual status before changing the system.

## Logs and diagnostics

```bash
tail -n 80 ~/snap/frontline-lyrics/current/FrontLineLyrics/logs/python_crash.log
tail -n 80 ~/snap/frontline-lyrics/common/logs/launcher.log
snap run --shell frontline-lyrics
# Inside shell:
command -v pactl parecord playerctl
exit
```

## Publish only after verification

```bash
snapcraft login
snapcraft register frontline-lyrics # first publication only
snapcraft upload --release=edge ./frontline-lyrics_1.0.0_amd64.snap
```

The current `grade: devel` is suitable for Edge testing; for Stable, change to `grade: stable`, rebuild, review interfaces/permissions and ensure the store approves distribution. Note that MPRIS through D-Bus inside strict confinement still requires functional testing, and `audio-record` must be connected.

## What changed in this consolidated kit

- `prepare-snap.sh`: correct relative path to `FrontLine-Linux/dist-linux`, creates `snap/gui` automatically and checks expected files.
- `snapcraft.yaml`: `core26` + `platforms` and staged `pulseaudio-utils` (for `parecord` and `pactl`).
- `frontline-lyrics-snap.sh`: adds `$SNAP/usr/bin` to `PATH` so the PyInstaller backend can discover staged utilities.
- Source and scripts are safe to commit; **never commit** `payload/`, `.snap` binaries or local telemetry credentials.

## Gitignore

```gitignore
/FrontLine-Linux/packaging-snap/payload/
/FrontLine-Linux/packaging-snap/parts/
/FrontLine-Linux/packaging-snap/prime/
/FrontLine-Linux/packaging-snap/stage/
/FrontLine-Linux/packaging-snap/*.snap
```

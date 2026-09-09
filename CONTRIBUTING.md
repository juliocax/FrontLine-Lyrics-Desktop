# Contributing

Thank you for considering a contribution, this project is maintained in my spare time, so any help is welcome.

## Project structure

- **`Frontline/`** — the WPF (C#/.NET) overlay interface. It launches the Python server as a child process and communicates with it via a local WebSocket.
- **`FrontlineServer/`** — the Python backend (the recognition/synchronization engine). The code resides in `FrontlineServer/engine/`, organized by responsibility (audio capture, Shazam-based recognition, SMTC confidence policy,
lyrics fetching, translation, WebSocket server, background processes).
- **`FrontlineServer/tests/`** — the `pytest` test suite for the Python component.

If you are unsure where a change belongs: anything related to *what* is displayed and the visual appearance belongs to the WPF project. Anything related to audio identification, lyrics fetching, or translation belongs to the Python engine.

## Initial setup

**C# / WPF component**
1. Install Visual Studio with the **.NET desktop development** workload.
2. Open `Frontline.sln`, set `Frontline` as the startup project, and run it.

**Python component**
```
cd FrontlineServer
pip install -r requirements.txt
pip install -r requirements-dev.txt   # required only to run tests
```

## Running tests

```
cd FrontlineServer
pytest
```

All external calls (Shazam, LRCLIB, Deezer, translation backends) are mocked in the test suite, allowing for fully offline execution. The `tests/` structure mirrors that of `engine/` on a file-by-file basis; therefore, changes to `engine/lyrics.py` should be reflected in `tests/test_lyrics.py`, and so on. ## Choosing an issue

- Comment on the issue before starting (e.g., "I'd like to take on this task") so that someone else doesn't pick it up at the same time.

## Creating a pull request

1. Fork the repository and create a branch from `main`.
2. Keep the PR focused; one issue or feature per PR is easier to review than a set of unrelated changes.
3. If you have modified Python code, run `pytest` locally before opening the PR (see above).
4. Provide details about the changes in the PR.
5. Please be patient regarding review turnaround times; this project is developed in spare time, but all PRs receive a response.

## Questions?

Open a [Discussion](../../discussions) or comment on the corresponding issue.

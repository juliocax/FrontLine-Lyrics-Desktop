"""Local request/response endpoint for the Avalonia add-on manager."""
import asyncio
import json
import logging
import os
from pathlib import Path
import signal
import sys
import tempfile

from websockets.exceptions import ConnectionClosed

from engine import japanese_pack, offline_translation
from engine.addon_paths import addons_directory

_lock = asyncio.Lock()


def _status():
    current = offline_translation.installed()
    models = [
        {"source": a, "target": b, "sourceName": model["from_name"],
         "targetName": model["to_name"], "installed": True}
        for (a, b), model in current.items()
    ]
    return {"models": models, "dictionary": bool(japanese_pack.installed_dictionary()),
            "source": offline_translation.source_code(None, current)}


async def _run_worker(args):
    root = addons_directory()
    root.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(dir=root) as staging:
        result = Path(staging) / "result.json"
        error = Path(staging) / "error.txt"
        env = {**os.environ, "FRONTLINE_ADDONS_DIR": str(root),
               "FRONTLINE_ADDON_RESULT_FILE": str(result),
               "FRONTLINE_ADDON_ERROR_FILE": str(error), "FRONTLINE_ADDON_STAGE_DIR": staging}
        command = [sys.executable]
        if not getattr(sys, "frozen", False):
            command.append(str(Path(__file__).resolve().parents[1] / "FrontlineServer.py"))

        process = await asyncio.create_subprocess_exec(*command, *args, env=env,
                                                       start_new_session=True)
        try:
            code = await process.wait()
        finally:
            if process.returncode is None:
                try:
                    os.killpg(process.pid, signal.SIGKILL)
                except ProcessLookupError:
                    pass
                await process.wait()

        if code:
            detail = next((p.read_text(encoding="utf-8")[:500] for p in (error, result) if p.is_file()),
                          f"Add-on worker exited with code {code}")
            raise RuntimeError(detail)
        return offline_translation.read_json(result) if result.is_file() else None


async def _operation(request):
    operation = request.get("operation")
    codes = request.get("codes", [])
    if not isinstance(codes, list) or any(
        not isinstance(code, str) or not offline_translation.CODE.fullmatch(code) for code in codes
    ):
        raise ValueError("Invalid language code")
    result = None
    async with _lock:
        if operation in {"list", "install", "remove"} and len(codes) == (0 if operation == "list" else 2):
            result = await _run_worker(["--offline-addons", operation, *codes])
        elif operation == "dictionary_install" and not codes:
            await _run_worker(["--install-japanese-pack"])
        elif operation == "dictionary_remove" and not codes:
            await asyncio.to_thread(japanese_pack.remove)
        elif operation == "source" and len(codes) <= 1:
            models = offline_translation.installed()
            path = offline_translation.directory() / "source.txt"
            if codes:
                if not any(source == codes[0] for source, _ in models):
                    raise ValueError("No installed model for that source")
                with tempfile.NamedTemporaryFile("w", dir=path.parent, delete=False, encoding="utf-8") as tmp:
                    tmp.write(codes[0])
                Path(tmp.name).replace(path)
            else:
                path.unlink(missing_ok=True)
        elif operation != "status" or codes:
            raise ValueError("Unknown add-on operation")
        return {**_status(), "catalog": result}


async def handle(websocket):
    work = disconnect = None
    try:
        request = json.loads(await asyncio.wait_for(websocket.recv(), timeout=5))
        work = asyncio.create_task(_operation(request))
        disconnect = asyncio.create_task(websocket.wait_closed())
        await asyncio.wait((work, disconnect), return_when=asyncio.FIRST_COMPLETED)
        if work.done():
            await websocket.send(json.dumps(work.result()))
    except Exception as error:
        logging.warning("Linux add-on request failed: %s", error)
        if disconnect is None or not disconnect.done():
            try:
                await websocket.send(json.dumps({"error": str(error)[:500]}))
            except ConnectionClosed:
                pass
    finally:
        tasks = [task for task in (work, disconnect) if task is not None]
        for task in tasks:
            if not task.done():
                task.cancel()
        await asyncio.gather(*tasks, return_exceptions=True)

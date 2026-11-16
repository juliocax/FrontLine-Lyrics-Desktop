"""Manage optional Argos models and translate timestamped lyrics in batches."""
import json
import logging
import os
import re
import shutil
import subprocess
import sys
import tempfile
import time
import urllib.request
import zipfile
from pathlib import Path
from urllib.parse import urlsplit
from uuid import uuid4

import requests

from engine.addon_paths import addons_directory

INDEX = "https://raw.githubusercontent.com/argosopentech/argospm-index/main/index.json"
CODE = re.compile(r"[a-z][a-z0-9-]{1,19}")
MARKER = "metadata.json"


def directory():
    return addons_directory() / "translation"


def read_json(path):
    return json.loads(path.read_text(encoding="utf-8"))


def pair(item):
    return item["from_code"], item["to_code"]


def slug(codes):
    if len(codes) != 2 or not all(CODE.fullmatch(code) for code in codes):
        raise ValueError("Invalid model language codes")
    return "_".join(codes)


def installed():
    models = {}
    for path in directory().glob(f"*/{MARKER}"):
        try:
            item = read_json(path)
            if path.parent.name == slug(pair(item)) and (path.parent / "model" / "model.bin").is_file():
                models[pair(item)] = item
        except (OSError, ValueError, KeyError, TypeError):
            logging.warning("Skipping unreadable translation model: %s", path.parent.name)
    return models


def route(models, source, target):
    if source == target:
        return []
    direct = (source, target)
    first, second = (source, "en"), ("en", target)
    if direct in models:
        return [direct]
    if first in models and second in models:
        return [first, second]
    return []


def catalog():
    root = directory()
    root.mkdir(parents=True, exist_ok=True)
    cache = root / "catalog.json"
    try:
        with urllib.request.urlopen(INDEX, timeout=8) as response:
            raw = response.read(2_000_000)
        rows = json.loads(raw)
        if not isinstance(rows, list):
            raise ValueError("Invalid Argos catalog")
        # Keep the previous usable copy until the new one is fully written.
        with tempfile.TemporaryDirectory(dir=root) as temp:
            path = Path(temp) / "catalog.json"
            path.write_bytes(raw)
            path.replace(cache)
    except (OSError, ValueError):
        rows = read_json(cache) if cache.exists() else []
        if not rows and not installed():
            raise
    models = {}
    for item in rows:
        if item.get("type", "translate") != "translate" or not item.get("from_code") or not item.get("to_code"):
            continue
        links = [link for link in item.get("links", []) if link.startswith("https://")]
        if links:
            models[pair(item)] = {**item, "links": links}
    for codes, item in installed().items():
        models.setdefault(codes, item)
    return models


def download_model(item, archive):
    codes = pair(item)
    failures = []
    headers = {
        "User-Agent": "FrontLineLyrics/1.7.0",
        "Accept-Encoding": "identity",
    }
    for link in item.get("links", []):
        host = urlsplit(link).hostname or "Argos"
        for attempt in range(3):
            try:
                with requests.get(link, headers=headers, stream=True, timeout=(12, 45)) as response:
                    if urlsplit(response.url).scheme != "https":
                        raise ValueError("Argos download redirected away from HTTPS")
                    response.raise_for_status()
                    expected = response.headers.get("Content-Length")
                    total = 0
                    with archive.open("wb") as output:
                        for chunk in response.iter_content(chunk_size=1024 * 1024):
                            total += len(chunk)
                            if total > 1_000_000_000:
                                raise ValueError("Argos archive exceeds size limit")
                            output.write(chunk)
                    if expected and total != int(expected):
                        raise requests.exceptions.ChunkedEncodingError(
                            f"Incomplete response ({total}/{expected} bytes)")
                    if not zipfile.is_zipfile(archive):
                        raise ValueError("Server did not return an Argos model archive")
                return
            except requests.exceptions.RequestException as error:
                status = error.response.status_code if error.response is not None else None
                failures.append(f"{host}: HTTP {status}" if status else f"{host}: {type(error).__name__}: {error}")
                transient = (
                    status in {408, 429, 500, 502, 503, 504}
                    or isinstance(error, (requests.exceptions.ConnectionError, requests.exceptions.Timeout,
                                          requests.exceptions.ChunkedEncodingError))
                ) and not isinstance(error, requests.exceptions.SSLError)
                if not transient or attempt == 2:
                    break
                time.sleep(2 ** attempt)
            except ValueError as error:
                failures.append(f"{host}: {error}")
                break
    detail = failures[-1] if failures else "No HTTPS download link in the catalog"
    raise RuntimeError(f"Argos {codes[0]} → {codes[1]}: {detail}")


def install(item):
    codes = pair(item)
    root = directory()
    dest = root / slug(codes)
    if codes in installed():
        return
    stage_dir = os.environ.get("FRONTLINE_ADDON_STAGE_DIR") or root
    with tempfile.TemporaryDirectory(dir=stage_dir) as temp:
        stage = Path(temp)
        archive = stage / "model.zip"
        download_model(item, archive)

        unpack = stage / "unpacked"
        with zipfile.ZipFile(archive) as model:
            if sum(info.file_size for info in model.infolist()) > 2_000_000_000:
                raise ValueError("Argos model exceeds extracted size limit")
            model.extractall(unpack)
        marker, = unpack.glob(f"*/{MARKER}")
        metadata = read_json(marker)
        if (pair(metadata) != codes or not (marker.parent / "model" / "model.bin").is_file()
                or metadata.get("package_version") != item.get("package_version")
                or not any((marker.parent / name).is_file()
                           for name in ("sentencepiece.model", "bpe.model"))):
            raise ValueError("Argos model does not match catalog entry")
        if dest.exists():
            shutil.rmtree(dest)
        try:
            marker.parent.rename(dest)
        except OSError:
            if codes not in installed():
                raise


def run_model(codes, lines):
    import ctranslate2
    from argostranslate.tokenizer import BPETokenizer, SentencePieceTokenizer

    path = directory() / slug(codes)
    sentencepiece = path / "sentencepiece.model"
    tokenizer = (SentencePieceTokenizer(sentencepiece) if sentencepiece.is_file()
                 else BPETokenizer(path / "bpe.model", *codes))
    encoded = [(index, tokenizer.encode(line)) for index, line in enumerate(lines)
               if any(char.isalnum() for char in line)]
    encoded = [(index, tokens) for index, tokens in encoded if tokens]
    if not encoded:
        return lines
    device_types = ctranslate2.get_supported_compute_types("cpu")
    translator = ctranslate2.Translator(
        str(path / "model"), device="cpu",
        compute_type="int8" if "int8" in device_types else "float32", intra_threads=2,
    )
    prefix = read_json(path / MARKER).get("target_prefix", "")
    result = translator.translate_batch(
        [tokens for _, tokens in encoded],
        target_prefix=[[prefix]] * len(encoded) if prefix else None,
        beam_size=4, max_batch_size=8, replace_unknowns=True, length_penalty=0.2,
        max_decoding_length=192,
    )
    if len(result) != len(encoded):
        raise RuntimeError("Argos returned an unexpected number of results")
    texts = list(lines)
    empty = 0
    for (index, _), item in zip(encoded, result):
        text = tokenizer.decode(item.hypotheses[0]).strip() if item.hypotheses else ""
        text = text.removeprefix(prefix).strip() if prefix else text
        if text:
            texts[index] = text
        else:
            empty += 1
    if empty == len(encoded):
        raise RuntimeError("Argos returned no translations for the input batch")
    return texts


def target_code(code):
    return {"pt": "pb", "pt-pt": "pt", "zh-cn": "zh", "zh-tw": "zt"}.get(code, code)


def source_code(code, models):
    try:
        selected = (directory() / "source.txt").read_text(encoding="utf-8").strip()
        if any(source == selected for source, _ in models):
            return selected
    except (OSError, UnicodeError):
        pass
    return {"zh-cn": "zh", "zh-tw": "zt"}.get(code, code)


def translate(lines, source, steps):
    if not lines:
        return None
    root = directory()
    command = [sys.executable]
    if not getattr(sys, "frozen", False):
        command.append(str(Path(__file__).resolve().parents[1] / "FrontlineServer.py"))
    try:
        with tempfile.TemporaryDirectory(dir=root) as temp:
            data = Path(temp) / "translation.json"
            result = lines
            error_file = Path(temp) / "worker-error.txt"
            for codes in steps:
                data.write_text(json.dumps({"model": codes, "lines": result}), encoding="utf-8")
                process = subprocess.run(
                    [*command, "--offline-translate", str(data), str(error_file)], timeout=60,
                )
                if process.returncode:
                    detail = (error_file.read_text(encoding="utf-8").strip()
                              if error_file.is_file() else "worker terminated without a Python traceback")
                    logging.warning("Argos %s → %s failed (exit %#x):\n%s", *codes,
                                    process.returncode, detail)
                    return None
                result = read_json(data)
                if not isinstance(result, list) or len(result) != len(lines):
                    raise ValueError("Invalid translation response")
            logging.info("Argos offline: %s", " → ".join([source, *(end for _, end in steps)]))
            return dict(zip(lines, result))
    except (OSError, ValueError, subprocess.SubprocessError):
        logging.warning("Argos offline failed; using online translation", exc_info=True)
        return None


def command(action, args):
    if action == "--offline-translate":
        path = Path(args[0])
        request = read_json(path)
        lines = run_model(request["model"], request["lines"])
        path.write_text(json.dumps(lines), encoding="utf-8")
        return

    action, *codes = args
    if action == "install":
        try:
            available = catalog()
        except (OSError, ValueError) as error:
            raise RuntimeError(f"Argos catalog: {error}") from error
        steps = route(available, *codes)
        if not steps:
            raise ValueError("No Argos route available")
        for step in steps:
            install(available[step])
    elif action == "remove":
        model_path = directory() / slug(codes)
        if model_path.exists():
            removed = model_path.with_name(f".removed-{uuid4().hex}")
            model_path.rename(removed)
            shutil.rmtree(removed)
    elif action != "list":
        raise ValueError("Unknown add-on operation")

    current = installed()
    try:
        available = catalog() if action == "list" else read_json(directory() / "catalog.json")
    except (OSError, ValueError):
        if action == "list" and not current:
            raise
        available = list(current.values())
    if isinstance(available, list):
        available = {pair(item): item for item in available
                     if item.get("from_code") and item.get("to_code") and item.get("type", "translate") == "translate"
                     and (pair(item) in current or any(link.startswith("https://") for link in item.get("links", [])))}
    available.update({codes: item for codes, item in current.items() if codes not in available})
    result = [{"source": a, "target": b, "sourceName": item["from_name"],
               "targetName": item["to_name"], "installed": (a, b) in current}
              for (a, b), item in available.items()]
    Path(os.environ["FRONTLINE_ADDON_RESULT_FILE"]).write_text(json.dumps(result), encoding="utf-8")

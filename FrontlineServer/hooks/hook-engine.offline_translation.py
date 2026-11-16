"""Collect Argos runtime files for Linux PyInstaller packaging."""
import sys

from PyInstaller.utils.hooks import collect_all, collect_data_files, copy_metadata

if sys.platform.startswith("linux"):
    datas, binaries, hiddenimports = collect_all("ctranslate2")
    for name in ("sacremoses", "certifi"):
        datas += collect_data_files(name)
    for name in ("ctranslate2", "sentencepiece", "sacremoses", "argostranslate"):
        datas += copy_metadata(name)

"""Collect romanization data without bundling the optional UniDic dictionary."""
import sys

from PyInstaller.utils.hooks import collect_all, collect_data_files

if sys.platform.startswith("linux"):
    datas, binaries = [], []
    hiddenimports = ["pythainlp.tokenize.newmm", "pythainlp.transliterate.royin"]
    for name in ("opencc", "pypinyin", "koroman", "cutlet", "fugashi"):
        package_data, package_binaries, imports = collect_all(name)
        datas += package_data
        binaries += package_binaries
        hiddenimports += imports
    datas += collect_data_files("langdetect")
    datas += collect_data_files("pythainlp", includes=["corpus/words_th.txt"])
    excludedimports = ["unidic_lite"]

"""Install only the data files used by fugashi and Cutlet."""
import hashlib
import os
from pathlib import Path
import shutil
import tarfile
import tempfile
from urllib.request import urlopen
from urllib.error import URLError

URL = ("https://files.pythonhosted.org/packages/55/2b/"
       "8cf7514cb57d028abcef625afa847d60ff1ffbf0049c36b78faa7c35046f/"
       "unidic-lite-1.0.8.tar.gz")
SHA256 = "db9d4572d9fdd4d00a97949d4b0741ec480ee05a7e7e2e32f547500dae27b245"
REQUIRED = {"mecabrc", "sys.dic", "unk.dic", "matrix.bin", "char.bin", "dicrc"}


def dictionary_path():
    root = os.environ.get("FRONTLINE_ADDONS_DIR")
    return Path(root) / "ja-unidic-1.0.8" if root else None


def installed_dictionary():
    path = dictionary_path()
    if path and all((path / name).is_file() for name in REQUIRED):
        return path
    return None


def create_converter(path):
    from cutlet import Cutlet

    directory = path.as_posix()
    return Cutlet(ensure_ascii=False, use_foreign_spelling=False,
                  mecab_args=f'-r "{directory}/mecabrc" -d "{directory}"')


def validate_dictionary(path):
    reading = create_converter(path).romaji("過ぎる日々にあくびが出る")
    if "hibi" not in reading.lower():
        raise ValueError(f"Cutlet / UniDic smoke test: {reading}")


def install(validate):
    destination = dictionary_path()
    if destination is None:
        raise RuntimeError("FRONTLINE_ADDONS_DIR missing")
    if installed_dictionary():
        return
    destination.parent.mkdir(parents=True, exist_ok=True)
    stage_dir = os.environ.get("FRONTLINE_ADDON_STAGE_DIR") or destination.parent
    with tempfile.TemporaryDirectory(dir=stage_dir) as temporary:
        root = Path(temporary)
        archive = root / "unidic.tar.gz"
        staged = root / "dictionary"
        staged.mkdir()
        digest = hashlib.sha256()
        size = 0
        try:
            with urlopen(URL, timeout=30) as response, archive.open("wb") as output:
                while chunk := response.read(1024 * 1024):
                    size += len(chunk)
                    if size > 50_000_000:
                        raise ValueError("Unexpected download size")
                    digest.update(chunk)
                    output.write(chunk)
        except (URLError, TimeoutError) as error:
            raise RuntimeError(f"UniDic download: {error}") from error
        if digest.hexdigest() != SHA256:
            raise ValueError("UniDic checksum mismatch")
        with tarfile.open(archive, "r:gz") as bundle:
            for member in bundle:
                path = Path(member.name)
                if not member.isfile():
                    continue
                if path.parts[:-1] != ("unidic-lite-1.0.8", "unidic_lite", "dicdir"):
                    if path.parts[:-1] != ("unidic-lite-1.0.8",) or not path.name.startswith("LICENSE"):
                        continue
                if ":" in path.name or "\\" in path.name:
                    raise ValueError("Invalid dictionary filename")
                with bundle.extractfile(member) as source, (staged / path.name).open("wb") as target:
                    shutil.copyfileobj(source, target)
        if not REQUIRED.issubset({file.name for file in staged.iterdir()}):
            raise ValueError("Incomplete UniDic dictionary")
        # The validator exits before activation, releasing native file mappings on Windows.
        validate(staged)
        if destination.exists():
            shutil.rmtree(destination)
        staged.rename(destination)

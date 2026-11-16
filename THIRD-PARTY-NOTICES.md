# Third-party notices — FrontLine Lyrics

See [full license and NOTICE texts](THIRD-PARTY-LICENSES.md) for the notices reproduced here.

| Component | Platform | Purpose | License / upstream terms |
| --- | --- | --- | --- |
| shazamio | Both | Audio recognition | MIT · [ShazamIO](https://github.com/shazamio/ShazamIO) |
| deep-translator | Both | MyMemory client | MIT · [PyPI](https://pypi.org/project/deep-translator/) |
| langdetect | Both | Lyric language detection | Apache-2.0 per the shipped notices. Package metadata also lists MIT. · [License and NOTICE](THIRD-PARTY-LICENSES.md#langdetect-109) |
| anyascii | Both | Transliteration fallback | ISC · [License](THIRD-PARTY-LICENSES.md#anyascii-033) |
| requests | Both | HTTP client | Apache-2.0 · [Requests](https://github.com/psf/requests) |
| certifi | Both | HTTPS certificate bundle | MPL-2.0 · [certifi](https://github.com/certifi/python-certifi) |
| websockets | Both | Local frontend connection | BSD-3-Clause · [websockets](https://github.com/python-websockets/websockets) |
| pyaudiowpatch | Windows | WASAPI audio capture | Apache-2.0 declared by the distribution. See the [PyAudio MIT notice](https://github.com/s0d3s/PyAudioWPatch/blob/master/LICENSE.txt) and [PortAudio terms](https://github.com/PortAudio/portaudio/blob/master/LICENSE.txt). |
| PyWinRT runtime and Windows namespace packages | Windows | Windows media-session bindings | MIT · [PyWinRT](https://github.com/pywinrt/pywinrt) |
| audioop-lts | Both | Audio operations on Python 3.13+ | PSF-2.0 · [PyPI](https://pypi.org/project/audioop-lts/) |
| PyInstaller | Both | Backend build tool and bootloader | GPL-2.0-or-later with the PyInstaller exception, and Apache-2.0 for designated files · [License terms](https://pyinstaller.org/en/stable/license.html) |
| translators | Both | Python requirements entry | GPL-3.0 as declared upstream · [PyPI](https://pypi.org/project/translators/) |
| cutlet, fugashi | Both | Japanese romanization and tokenization | MIT · [cutlet](THIRD-PARTY-LICENSES.md#cutlet-052) · [fugashi and MeCab BSD-3-Clause notice](THIRD-PARTY-LICENSES.md#fugashi-152) |
| jaconv, mojimoji | Both | Japanese text conversion | [jaconv, MIT](THIRD-PARTY-LICENSES.md#jaconv-050) · [mojimoji, Apache-2.0](THIRD-PARTY-LICENSES.md#mojimoji-0013) |
| koroman | Both | Korean romanization | MIT · [License](THIRD-PARTY-LICENSES.md#koroman-1016) |
| opencc-python-reimplemented | Both | Chinese script conversion | Apache-2.0 · [License and NOTICE](THIRD-PARTY-LICENSES.md#opencc-python-reimplemented-017) |
| pypinyin | Both | Mandarin romanization | MIT · [License](THIRD-PARTY-LICENSES.md#pypinyin-0550) |
| pythainlp | Both | Thai text processing and romanization | Apache-2.0 · [License](THIRD-PARTY-LICENSES.md#pythainlp-540). The selected word list uses [CC0-1.0](https://creativecommons.org/publicdomain/zero/1.0/) · [Corpus notice](https://github.com/PyThaiNLP/pythainlp/blob/v5.4.0/pythainlp/corpus/corpus_license.md) |
| CTranslate2 | Both | On-device model inference | MIT · [Source](https://github.com/OpenNMT/CTranslate2) |
| SentencePiece | Both | Model tokenization | Apache-2.0 · [Source](https://github.com/google/sentencepiece) |
| Sacremoses | Both | Tokenization for compatible models | MIT · [Source](https://github.com/hplt-project/sacremoses) |
| Argos Translate tokenizer | Both | Separately installed tokenizer code | MIT · [Argos Translate 1.11.0](https://pypi.org/project/argostranslate/1.11.0/) |
| UniDic Lite / UniDic | Both | Optional Japanese dictionary | MIT for UniDic Lite and BSD-3-Clause notices for UniDic · [License texts](THIRD-PARTY-LICENSES.md#unidic-lite-108--unidic-212) |
| Argos model packages | Both | Optional translation models | See each model's supplied terms. Runtime licenses do not cover model weights or training datasets. · [Package index](https://github.com/argosopentech/argospm-index) |
| dbus-next | Linux | D-Bus integration for MPRIS | MIT · [License](https://github.com/altdesktop/python-dbus-next/blob/master/LICENSE) |
| playerctl | Linux | MPRIS player controls | LGPL-3.0 as listed upstream · [Source](https://github.com/altdesktop/playerctl) |
| pulseaudio-utils, libpulse0 | Linux | Audio output discovery and capture | See [PulseAudio terms](https://github.com/pulseaudio/pulseaudio/blob/master/LICENSE) and the notices supplied with the packaged binaries. |
| Avalonia | Linux | Desktop frontend | MIT · [License](https://github.com/AvaloniaUI/Avalonia/blob/main/licence.md) |
| .NET runtime and frontend dependencies | Linux | Self-contained frontend distribution | See the notices for the exact runtime and NuGet packages included in the build. |
| Noto Core and Noto CJK | Linux | Fonts staged in the Snap and embedded CJK fallback | SIL OFL 1.1 · [Noto Core](https://github.com/notofonts/noto-fonts) · [Noto CJK](https://github.com/notofonts/noto-cjk/blob/main/Sans/LICENSE) |
| libx11-6, libxcb1, libxrender1, libfontconfig1, libfreetype6, libgl1, libdbus-1-3, libicu78, libice6, libsm6 | Linux (Snap) | Native packages listed in `snapcraft.yaml` | See the copyright and license files of each staged package. |
| CAMeL Tools / CALIMA, Phonikud, uroman, YAMNet | Outside the build | Laboratory components | Inventory the exact runtimes, models, datasets, and notices if adopted. |
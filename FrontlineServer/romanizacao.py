import argparse
from pathlib import Path

SAMPLES = {
    "ja": [("過ぎる日々にあくびが出る", "Sugiru hibi ni akubi ga deru"),
           ("明日の今頃には", "Ashita no imagoro ni wa")],
    "ko": [("파란", "paran"), ("들어", "deureo"), ("완벽한지", "wanbyeokanji")],
    "zh": [("稻香", "dao xiang"), ("銀行行長", "yin hang hang zhang")],
    "th": [("เพื่อนเล่น ไม่เล่นเพื่อน", "Compare as palavras com o áudio")],
    "ar": [("تملي معاك", "Tamally maak")],
    "he": [("מי ישים יד ויבנה את ביתך", "Mi yasim yad veyivneh et beitech")],
}


def get_converter(args):
    if args.language == "ja":
        from cutlet import Cutlet

        options = {}
        if args.dictionary:
            path = args.dictionary.resolve()
            for name in ("mecabrc", "sys.dic"):
                if not (path / name).is_file():
                    raise ValueError(f"Falta {name} em {path}")
            options["mecab_args"] = f'-r "{path.as_posix()}/mecabrc" -d "{path.as_posix()}"'
        converter = Cutlet(use_foreign_spelling=False, ensure_ascii=False, **options)
        return converter.romaji
    if args.language == "ko":
        from koroman import romanize

        return romanize
    if args.language == "zh":
        from pypinyin import lazy_pinyin

        if args.expanded:
            from pypinyin_dict.phrase_pinyin_data import large_pinyin

            large_pinyin.load()
        return lambda line: " ".join(lazy_pinyin(line))
    if args.language == "th":
        from pythainlp.tokenize import word_tokenize
        from pythainlp.transliterate import romanize

        return lambda line: " ".join(
            romanize(word, engine="royin") for word in word_tokenize(line, keep_whitespace=False)
        )

    from uroman import Uroman

    universal = Uroman()
    if args.language == "he" and args.model:
        from phonikud_onnx import Phonikud
        from phonikud import phonemize

        model = Phonikud(str(args.model))

        def hebrew(line):
            vocalized = model.add_diacritics(line)
            romanized = universal.romanize_string(vocalized, lcode="heb")
            return f"{romanized} | vocalizado: {vocalized} | fonemas: {phonemize(vocalized)}"

        return hebrew
    if args.language == "ar" and args.egyptian:
        from camel_tools.disambig.mle import MLEDisambiguator
        from camel_tools.tokenizers.word import simple_word_tokenize

        model = MLEDisambiguator.pretrained("calima-egy-r13")

        def arabic(line):
            words = model.disambiguate(simple_word_tokenize(line))
            vocalized = " ".join(
                word.analyses[0].analysis.get("diac", word.word) if word.analyses else word.word
                for word in words
            )
            return f"{universal.romanize_string(vocalized, lcode='ara')} | vocalizado: {vocalized}"

        return arabic
    return lambda line: universal.romanize_string(line, lcode={"ar": "ara", "he": "heb"}[args.language])


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("language", choices=SAMPLES)
    parser.add_argument("--input", type=Path, help="Arquivo UTF-8, uma linha por verso")
    parser.add_argument("--dictionary", type=Path, help="Pasta externa do UniDic")
    parser.add_argument("--expanded", action="store_true", help="Dicionário ampliado de mandarim")
    parser.add_argument("--model", type=Path, help="Modelo ONNX do Phonikud")
    parser.add_argument("--egyptian", action="store_true", help="Usar desambiguador egípcio CAMeL")
    args = parser.parse_args()
    from anyascii import anyascii

    convert = get_converter(args)
    samples = SAMPLES[args.language]
    if args.input:
        samples = [(line, "") for line in args.input.read_text(encoding="utf-8-sig").splitlines() if line.strip()]
    for original, expected in samples:
        print(f"Original : {original}\nAnyAscii : {anyascii(original).capitalize()}\nCandidato: {convert(original)}")
        if expected:
            print(f"Referência: {expected}")
        print()


if __name__ == "__main__":
    main()

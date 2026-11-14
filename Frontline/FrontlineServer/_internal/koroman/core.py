import re

HANGUL_BASE = 0xAC00
HANGUL_END = 0xD7A3

CHO = [
    "ᄀ", "ᄁ", "ᄂ", "ᄃ", "ᄄ", "ᄅ", "ᄆ", "ᄇ", "ᄈ", "ᄉ",
    "ᄊ", "ᄋ", "ᄌ", "ᄍ", "ᄎ", "ᄏ", "ᄐ", "ᄑ", "ᄒ"
]

JUNG = [
    "ᅡ", "ᅢ", "ᅣ", "ᅤ", "ᅥ", "ᅦ", "ᅧ", "ᅨ", "ᅩ", "ᅪ", "ᅫ",
    "ᅬ", "ᅭ", "ᅮ", "ᅯ", "ᅰ", "ᅱ", "ᅲ", "ᅳ", "ᅴ", "ᅵ"
]

JONG = [
    "", "ᆨ", "ᆩ", "ᆪ", "ᆫ", "ᆬ", "ᆭ", "ᆮ", "ᆯ", "ᆰ", "ᆱ", "ᆲ",
    "ᆳ", "ᆴ", "ᆵ", "ᆶ", "ᆷ", "ᆸ", "ᆹ", "ᆺ", "ᆻ", "ᆼ", "ᆽ", "ᆾ",
    "ᆿ", "ᇀ", "ᇁ", "ᇂ"
]

_CASING_ALIASES = {
    'lowercase': 'lowercase', 'lower': 'lowercase', 'l': 'lowercase', 'lc': 'lowercase', '0': 'lowercase',
    'uppercase': 'uppercase', 'upper': 'uppercase', 'u': 'uppercase', 'uc': 'uppercase', '1': 'uppercase',
    'capitalize-line': 'capitalize-line', 'cap-line': 'capitalize-line', 'cline': 'capitalize-line', 'cl': 'capitalize-line', '2': 'capitalize-line',
    'capitalize-word': 'capitalize-word', 'cap-word': 'capitalize-word', 'cword': 'capitalize-word', 'cw': 'capitalize-word', '3': 'capitalize-word',
}


def _normalize_casing_option(opt):
    """Normalize casing_option input to one of the canonical names.

    Accepts full strings, short aliases, and integers (case-insensitive).
    Falls back to 'lowercase' for None or unknown values.
    """
    if opt is None:
        return 'lowercase'
    return _CASING_ALIASES.get(str(opt).lower(), 'lowercase')


# ---------------------------------------------------------------------------
# 자모 → 로마자 매핑 테이블
#
# ROMAN_MAP_STD     : 표준 발음 기반 매핑 (ROMANIZE_RULE 3·4). use_pronunciation_rules=True 일 때 사용.
#                     받침 ㄷ/ㅈ/ㅊ/ㅌ/ㅎ → 'ㄷ' 대표음(t).
# ROMAN_MAP_LITERAL : 직역/학술적 표기에 가까운 매핑 (ROMANIZE_RULE 5 일부 반영).
#                     use_pronunciation_rules=False 일 때 사용. 음운변화 가정 없이 글자 그대로.
#                     - 종성 ㄷ → 'd' (예: 굳이 → gudi), 종성 ㅎ → 'h' (예: 좋다 → johda)
# ---------------------------------------------------------------------------
ROMAN_MAP_STD = {
    "ᄀ": "g", "ᄁ": "kk", "ᄂ": "n", "ᄃ": "d", "ᄄ": "tt",
    "ᄅ": "r", "ᄆ": "m", "ᄇ": "b", "ᄈ": "pp", "ᄉ": "s", "ᄊ": "ss",
    "ᄋ": "", "ᄌ": "j", "ᄍ": "jj", "ᄎ": "ch", "ᄏ": "k",
    "ᄐ": "t", "ᄑ": "p", "ᄒ": "h",

    "ᅡ": "a", "ᅢ": "ae", "ᅣ": "ya", "ᅤ": "yae", "ᅥ": "eo", "ᅦ": "e",
    "ᅧ": "yeo", "ᅨ": "ye", "ᅩ": "o", "ᅪ": "wa", "ᅫ": "wae",
    "ᅬ": "oe", "ᅭ": "yo", "ᅮ": "u", "ᅯ": "wo", "ᅰ": "we",
    "ᅱ": "wi", "ᅲ": "yu", "ᅳ": "eu", "ᅴ": "ui", "ᅵ": "i",

    "ᆨ": "k", "ᆩ": "k", "ᆪ": "k", "ᆫ": "n", "ᆬ": "n", "ᆭ": "n", "ᆮ": "t",
    "ᆯ": "l", "ᆰ": "k", "ᆱ": "m", "ᆲ": "p", "ᆳ": "t", "ᆴ": "t", "ᆵ": "p", "ᆶ": "l",
    "ᆷ": "m", "ᆸ": "p", "ᆹ": "p", "ᆺ": "t", "ᆻ": "t", "ᆼ": "ng",
    "ᆽ": "t", "ᆾ": "t", "ᆿ": "k", "ᇀ": "t", "ᇁ": "p", "ᇂ": "t",
}

ROMAN_MAP_LITERAL = {**ROMAN_MAP_STD, "ᆮ": "d", "ᇂ": "h"}

# 하위 호환용 별칭 (기존 import koroman.core.ROMAN_MAP)
ROMAN_MAP = ROMAN_MAP_STD


# 표준발음법 29항 ㄴ첨가 — 형태소 없이: 뒤 음절 트리거 '잎'만 (오탐 방지)
_N_INSERTION_TRIGGERS = frozenset(["잎"])


def _hangul_syllable_indices(ch):
    n = ord(ch) - HANGUL_BASE
    return n // (21 * 28), (n % (21 * 28)) // 28, n % 28


def _compose_hangul_syllable(cho, jung, jong=0):
    return chr(HANGUL_BASE + (cho * 21 + jung) * 28 + jong)


def apply_n_insertion_triggers(text):
    """합성어 ㄴ첨가(29항): 받침 + 잎 → […닙] (꽃잎·깻잎 등)."""
    def _repl(match):
        word = match.group(0)
        if len(word) < 2 or word[-1] not in _N_INSERTION_TRIGGERS:
            return word
        prev, last = word[-2], word[-1]
        cho, jung, jong = _hangul_syllable_indices(prev)
        if jong == 0:
            return word
        l_cho, l_jung, l_jong = _hangul_syllable_indices(last)
        if l_cho != 11:  # ᄋ
            return word
        return word[:-1] + _compose_hangul_syllable(2, l_jung, l_jong)

    return re.sub(r"[\uAC00-\uD7A3]+", _repl, text)


def apply_pronunciation_rules(jamo_str):
    rules = [
        # ==============================
        # 1. 무효화 처리
        # ==============================
        (r"\u11a7", ""),  # 'ᆧ'(U+11A7) → 제거 (사용되지 않는 종성)

        # ==============================
        # 2. 비음화 (ㄴ, ㅁ, ㅇ)
        # ==============================
        (r"[\u11b8\u11c1\u11b9\u11b2\u11b5](?=[\u1102\u1106])", "\u11b7"),
        # 종성 'ᆸ(ㅂ)' 'ᇁ(ㅍ)' 'ᆹ(ㅂㅅ)' 'ᆲ(ㄹㅂ)' 'ᆵ(ㄹㅍ)' + 다음 초성 'ᄂ(ㄴ)' or 'ᄆ(ㅁ)' → 'ᆷ'

        (r"[\u11ae\u11c0\u11bd\u11be\u11ba\u11bb\u11c2](?=[\u1102\u1106])", "\u11ab"),
        # 종성 'ᆮ(ㄷ)' 'ᇀ(ㅌ)' 'ᆽ(ㅈ)' 'ᆾ(ㅊ)' 'ᆺ(ㅅ)' 'ᆻ(ㅆ)' 'ᇂ(ㅎ)' + 다음 초성 'ᄂ(ㄴ)' or 'ᄆ(ㅁ)' → 'ᆫ'

        (r"[\u11a8\u11a9\u11bf\u11aa\u11b0](?=[\u1102\u1106])", "\u11bc"),
        # 종성 'ᆨ(ㄱ)' 'ᆩ(ㄲ)' 'ᆿ(ㅋ)' 'ᆪ(ㄱㅅ)' 'ᆰ(ㄹㄱ)' + 다음 초성 'ᄂ'/'ᄆ' → 'ᆼ'

        # ==============================
        # 3. 연음/연철
        # ==============================
        (r"\u11a8\u110b(?=[\u1163\u1164\u1167\u1168\u116d\u1172])", "\u11bc\u1102"),
        # 'ᆨ' + 'ᄋ' + 중성 'ㅑㅒㅕㅖㅛㅠ' → 'ᆼᄂ' (연음화)

        (r"\u11af\u110b(?=[\u1163\u1164\u1167\u1168\u116d\u1172])", "\u11af\u1105"),
        # 'ᆯ' + 'ᄋ' + 중성 위와 같음 → 'ᆯᄅ'

        (r"[\u11a8\u11bc]\u1105", "\u11bc\u1102"),
        # 'ᆨ(ㄱ)', 'ᆼ(ㅇ)' + 'ᄅ(ㄹ)' → 'ᆼᄂ'

        (r"\u11ab\u1105(?=\u1169)", "\u11ab\u1102"),
        # 'ᆫ(ㄴ)' + 'ᄅ' + 중성 'ㅗ' → 'ᆫᄂ'

        (r"\u11af\u1102|\u11ab\u1105", "\u11af\u1105"),
        # 'ᆯ(ㄹ)' + 'ᄂ(ㄴ)', 'ᆫ(ㄴ)' + 'ᄅ(ㄹ)' → 'ᆯᄅ'

        (r"[\u11b7\u11b8]\u1105", "\u11b7\u1102"),
        # 'ᆷ(ㅁ)', 'ᆸ(ㅂ)' + 'ᄅ' → 'ᆷᄂ'

        (r"\u11b0\u1105", "\u11a8\u1105"),
        # 'ᆰ(ㄹㄱ)' + 'ᄅ' → 'ᆨᄅ'

        # ==============================
        # 4. 격음화 / 자음군 분해
        # ==============================
        (r"\u11a8\u110f", "\u11a8-\u110f"),  # 'ᆨ' + 'ᄏ' → 'ᆨ-ᄏ'
        (r"\u11b8\u1111", "\u11b8-\u1111"),  # 'ᆸ' + 'ᄑ' → 'ᆸ-ᄑ'
        (r"\u11ae\u1110", "\u11ae-\u1110"),  # 'ᆮ' + 'ᄐ' → 'ᆮ-ᄐ'

        # ==============================
        # 5. 복합 종성(자음군) + 'ᄋ' 모음 연음 처리 (표준발음법 14항)
        #    뒤 자음만 다음 음절 초성으로 옮겨감. 분해보다 먼저 적용해야
        #    이중종성 dedup 규칙으로 자음이 잘못 사라지지 않는다.
        #    예: 닭이[달기] → ᆰᄋ → ᆯᄀ → "dalgi"
        #        잃어[이러] → ᆶᄋ → ᆯᄋ → 후속 연음으로 ᄅ → "ireo"
        #    (된소리는 표기에 반영하지 않음 — 넋이[넉씨] → "neoksi")
        # ==============================
        (r"\u11aa\u110b", "\u11a8\u1109"),  # ᆪ + 'ᄋ' → ᆨ + 'ᄉ' (넋이 → 넉시)
        (r"\u11ac\u110b", "\u11ab\u110c"),  # ᆬ + 'ᄋ' → ᆫ + 'ᄌ' (앉아 → 안자)
        (r"\u11ad\u110b", "\u11ab\u110b"),  # ᆭ + 'ᄋ' → ᆫ + 'ᄋ' (ㅎ만 탈락; 일관성)
        (r"\u11b0\u110b", "\u11af\u1100"),  # ᆰ + 'ᄋ' → ᆯ + 'ᄀ' (닭이 → 달기)
        (r"\u11b1\u110b", "\u11af\u1106"),  # ᆱ + 'ᄋ' → ᆯ + 'ᄆ' (삶이 → 살미)
        (r"\u11b2\u110b", "\u11af\u1107"),  # ᆲ + 'ᄋ' → ᆯ + 'ᄇ' (밟아 → 발바)
        (r"\u11b3\u110b", "\u11af\u1109"),  # ᆳ + 'ᄋ' → ᆯ + 'ᄉ' (곬이 → 골시)
        (r"\u11b4\u110b", "\u11af\u1110"),  # ᆴ + 'ᄋ' → ᆯ + 'ᄐ' (핥아 → 할타)
        (r"\u11b5\u110b", "\u11af\u1111"),  # ᆵ + 'ᄋ' → ᆯ + 'ᄑ' (읊어 → 을퍼)
        (r"\u11b6\u110b", "\u11af\u110b"),  # ᆶ + 'ᄋ' → ᆯ + 'ᄋ' (잃어 → 이러; ㅎ만 탈락)
        (r"\u11b9\u110b", "\u11b8\u1109"),  # ᆹ + 'ᄋ' → ᆸ + 'ᄉ' (값이 → 갑시)

        # ==============================
        # 5b. 밟- 어간 예외 (표준발음법 제10항 예외)
        #    'ㄼ' 받침은 일반적으로 [ㄹ]로 발음되지만, '밟-' 어간은 자음 앞에서 [ㅂ]로 발음.
        #    예: 밟다[밥따] → bapda, 밟지[밥찌] → bapji, 밟고[밥꼬] → bapgo, 밟는[밤는] → bamneun (비음화 우선)
        #    위 5번 규칙으로 'ᆲ+ᄋ' (밟아) 케이스는 이미 처리됨.
        # ==============================
        (r"\u1107\u1161\u11b2(?=[\u1100-\u110A\u110C-\u1112])", "\u1107\u1161\u11b8"),

        # ==============================
        # 6. 복합 종성 분해 (자음군 단순화)
        #    분해 순서: 받침 위치에서 [표준 발음법상 남는 자음]을 첫 자모로 둠.
        #    이후 이중종성 dedup 규칙으로 두 번째 자모가 떨어져 나감.
        # ==============================
        (r"\u11aa", "\u11a8\u11ba"),  # 'ᆪ(ㄱㅅ)' → [ㄱ] 우세
        (r"\u11ac", "\u11ab\u11bd"),  # 'ᆬ(ㄴㅈ)' → [ㄴ] 우세
        (r"\u11ad", "\u11ab\u11c2"),  # 'ᆭ(ㄴㅎ)' → [ㄴ] 우세
        (r"\u11b0", "\u11a8\u11af"),  # 'ᆰ(ㄹㄱ)' → [ㄱ] 우세 (예: 흙→heuk)
        (r"\u11b1", "\u11b7\u11af"),  # 'ᆱ(ㄹㅁ)' → [ㅁ] 우세 (예: 삶→sam)
        (r"\u11b2", "\u11af\u11b8"),  # 'ᆲ(ㄹㅂ)' → [ㄹ] 우세 (밟- 예외 [ㅂ]는 형태소 정보 필요)
        (r"\u11b3", "\u11af\u11ba"),  # 'ᆳ(ㄹㅅ)' → [ㄹ] 우세
        (r"\u11b4", "\u11af\u11c0"),  # 'ᆴ(ㄹㅌ)' → [ㄹ] 우세
        (r"\u11b5", "\u11c1\u11af"),  # 'ᆵ(ㄹㅍ)' → [ㅂ] 우세 (예: 읊→eup)
        (r"\u11b6", "\u11af\u11c2"),  # 'ᆶ(ㄹㅎ)' → [ㄹ] 우세 (모음 앞은 위 5단계에서 선처리)
        (r"\u11b9", "\u11b8\u11ba"),  # 'ᆹ(ㅂㅅ)' → [ㅂ] 우세

        # ==============================
        # 7. 구개음화 (ㄷ/ㅌ + ᄋㅣ → 지/치) + ㄷ+ㅎ+ㅣ → 치 (격음화+구개음화 통합)
        # ==============================
        (r"\u11ae\u110b\u1175", "\u110c\u1175"),  # 'ᆮ' + 'ᄋ' + 'ᅵ' → '지' (해돋이 → 해도지)
        (r"\u11c0\u110b\u1175", "\u110e\u1175"),  # 'ᇀ' + 'ᄋ' + 'ᅵ' → '치' (같이 → 가치)
        (r"\u11ae\u1112\u1175", "\u110e\u1175"),  # 'ᆮ' + 'ᄒ' + 'ᅵ' → '치' (굳히다 → 구치다, 닫히다 → 다치다)

        # ==============================
        # 8. 연음 / 이음자 제거 (받침 + ᄋ)
        # ==============================
        (r"\u11a8\u110b", "\u1100"),  # 'ᆨ' + 'ᄋ' → 'ᄀ'
        (r"\u11a9\u110b", "\u1101"),  # 'ᆩ' + 'ᄋ' → 'ᄁ'
        (r"\u11ae\u110b", "\u1103"),  # 'ᆮ' + 'ᄋ' → 'ᄃ'
        (r"\u11af\u110b", "\u1105"),  # 'ᆯ' + 'ᄋ' → 'ᄅ'
        (r"\u11b8\u110b", "\u1107"),  # 'ᆸ' + 'ᄋ' → 'ᄇ'
        (r"\u11ba\u110b", "\u1109"),  # 'ᆺ' + 'ᄋ' → 'ᄉ'
        (r"\u11bb\u110b", "\u110a"),  # 'ᆻ' + 'ᄋ' → 'ᄊ'
        (r"\u11bd\u110b", "\u110c"),  # 'ᆽ' + 'ᄋ' → 'ᄌ'
        (r"\u11be\u110b", "\u110e"),  # 'ᆾ' + 'ᄋ' → 'ᄎ'
        (r"\u11c2\u110b", ""),        # 'ᇂ' + 'ᄋ' → 제거

        # ==============================
        # 9. 거센소리되기 (Aspiration) — ROMANIZE_RULE 4 기본 정책
        #    체언 예외(묵호→Mukho, 집현전→Jiphyeonjeon)는 형태소 분석 없이는 분리 불가하므로
        #    일관되게 단일 격음(k/t/p/ch)으로 처리. 표준 발음 결과 기준.
        # ==============================
        (r"\u11c2\u1100|\u11a8\u1112", "\u110f"),  # 'ᇂ'+'ᄀ' or 'ᆨ'+'ᄒ' → 'ᄏ' (좋고→joko)
        (r"\u11c2\u1103|\u11ae\u1112", "\u1110"),  # 'ᇂ'+'ᄃ' or 'ᆮ'+'ᄒ' → 'ᄐ' (놓다→nota)
        (r"\u11c2\u110c|\u11bd\u1112", "\u110e"),  # 'ᇂ'+'ᄌ' or 'ᆽ'+'ᄒ' → 'ᄎ' (낳지→nachi, 맞히다→machida)
        (r"\u11c2\u1107", "\u1107"),               # 'ᇂ'+'ᄇ' → 'ᄇ'
        (r"\u11b8\u1112", "\u1111"),               # 'ᆸ'+'ᄒ' → 'ᄑ' (잡혀→japyeo)

        # ==============================
        # 10. 특수 처리 및 최종 정리
        # ==============================
        (r"\u11af\u1105", "ll"),                          # 'ᆯ' + 'ᄅ' → ll
        (r"\u11c2(?!\s|$)", ""),                          # 'ᇂ' (종성) 단독 → 제거
        (r"([\u11a8-\u11c2])([\u11a8-\u11c2])", r"\1"),   # 이중 종성 제거 (자음군 단순화 마무리)
    ]

    for pattern, repl in rules:
        jamo_str = re.sub(pattern, repl, jamo_str)
    return jamo_str

def split_hangul_to_jamos(text):
    result = ""
    for char in text:
        code = ord(char)
        if code < HANGUL_BASE or code > HANGUL_END:
            result += char
            continue
        index = code - HANGUL_BASE
        cho = CHO[index // (21 * 28)]
        jung = JUNG[(index % (21 * 28)) // 28]
        jong = JONG[index % 28]
        result += cho + jung + jong
    return result

def capitalize_words(text):
    result = []
    capitalize_next = True
    for char in text:
        if char.isspace():
            capitalize_next = True
            result.append(char)
        else:
            if capitalize_next:
                result.append(char.upper())
                capitalize_next = False
            else:
                result.append(char.lower())
    return "".join(result)

def capitalize_lines(text):
    result = []
    capitalize_next = True
    for char in text:
        if char == "\n":
            capitalize_next = True
            result.append(char)
        else:
            if capitalize_next:
                result.append(char.upper())
                capitalize_next = False
            else:
                result.append(char.lower())
    return "".join(result)


# ---------------------------------------------------------------------------
# 음절 재합성 + 붙임표(use_hyphen) 처리
# 음운규칙 적용 후의 자모열을 (초/중/종) 음절 단위로 재그룹화하여
# 모호한 음절 경계에 '-'를 삽입한다.
#
# 모호 케이스 (표준 로마자 표기법: "발음상 혼동의 우려가 있을 때 붙임표"):
#   1. 앞 음절 종성 ᆼ(ng) + 뒤 음절 초성 ᄋ(모음시작)   예) 중앙 → jung-ang
#   2. 앞 음절 종성 ᆫ(n)  + 뒤 음절 초성 ᄀ(g)          예) 반구대 → ban-gudae
#   3. 앞 음절 종성 없음   + 뒤 음절 초성 ᄋ(모음시작)   예) 해운대 → hae-undae, 세운 → se-un
# ---------------------------------------------------------------------------
_CHO_LO, _CHO_HI = 0x1100, 0x1112
_JUNG_LO, _JUNG_HI = 0x1161, 0x1175
_JONG_LO, _JONG_HI = 0x11A8, 0x11C2


def _regroup_jamos_to_syllables(jamo_str):
    """Return list of dicts: {'type': 'syllable', 'cho','jung','jong'} or {'type':'other','char'}."""
    groups = []
    i = 0
    n = len(jamo_str)
    while i < n:
        ch = jamo_str[i]
        code = ord(ch)
        if _CHO_LO <= code <= _CHO_HI and i + 1 < n and _JUNG_LO <= ord(jamo_str[i + 1]) <= _JUNG_HI:
            cho = ch
            jung = jamo_str[i + 1]
            jong = ''
            step = 2
            if i + 2 < n and _JONG_LO <= ord(jamo_str[i + 2]) <= _JONG_HI:
                jong = jamo_str[i + 2]
                step = 3
            groups.append({'type': 'syllable', 'cho': cho, 'jung': jung, 'jong': jong})
            i += step
            continue
        groups.append({'type': 'other', 'char': ch})
        i += 1
    return groups


def _romanize_group(g, mapping):
    if g['type'] == 'syllable':
        return mapping.get(g['cho'], '') + mapping.get(g['jung'], '') + (mapping.get(g['jong'], '') if g['jong'] else '')
    return mapping.get(g['char'], g['char'])


def _needs_hyphen(prev, cur):
    if prev is None or cur is None or prev['type'] != 'syllable' or cur['type'] != 'syllable':
        return False
    prev_jong = prev['jong']
    cur_cho = cur['cho']
    # 규칙1: ᆼ + ᄋ (모음 시작)
    if prev_jong == '\u11BC' and cur_cho == '\u110B':
        return True
    # 규칙2: ᆫ + ᄀ
    if prev_jong == '\u11AB' and cur_cho == '\u1100':
        return True
    # 규칙3: 받침 없음 + ᄋ (모음 시작)
    if not prev_jong and cur_cho == '\u110B':
        return True
    return False


def apply_roman_mapping_with_hyphen(jamo_str, mapping=None):
    if mapping is None:
        mapping = ROMAN_MAP_STD
    groups = _regroup_jamos_to_syllables(jamo_str)
    out = []
    for i, cur in enumerate(groups):
        if i > 0 and _needs_hyphen(groups[i - 1], cur):
            # 이미 끝이 '-'(예: 'ᆨ-ᄏ' 규칙에 의해 삽입)이면 중복 삽입 방지
            if not (out and out[-1].endswith('-')):
                out.append('-')
        out.append(_romanize_group(cur, mapping))
    return ''.join(out)


# ---------------------------------------------------------------------------
# 사용자 사전(custom_dictionary) 처리
# 매칭되는 한국어 단어를 사용자가 지정한 로마자 표기로 최우선 변환한다.
# 사전 값은 casing 옵션의 영향을 받지 않으며(사용자 의도 보존), 음운규칙/매핑도 거치지 않는다.
#
# 동작 방식:
#   1) 한국어 텍스트에서 사전 키를 길이 내림차순으로 찾아 placeholder(\x01N\x01)로 치환
#   2) 평소대로 romanize 파이프라인 수행 (placeholder는 비한글이라 그대로 보존됨)
#   3) casing 후처리까지 끝낸 결과에서 placeholder를 사전 값으로 복원
# ---------------------------------------------------------------------------
_DICT_MARK = "\x01"
_DICT_PATTERN = re.compile(r"\x01(\d+)\x01")

# 모듈 내부 영구 사전 저장소.
# set/add/remove/clear 함수로 관리하고, romanize 호출 시 use_custom_dictionary 옵션(기본 True)에 따라 자동 사용됨.
_CUSTOM_DICTIONARY_STORE = {}


def set_custom_dictionary(dictionary):
    """내부 사용자 사전을 통째로 교체한다. None/빈 dict이면 전체 비움."""
    _CUSTOM_DICTIONARY_STORE.clear()
    if isinstance(dictionary, dict):
        for k, v in dictionary.items():
            if isinstance(k, str) and k:
                _CUSTOM_DICTIONARY_STORE[k] = str(v)


def add_custom_dictionary(dict_or_key, value=None):
    """
    내부 사용자 사전에 항목을 추가/병합한다.
      - add_custom_dictionary({"김철수": "Kim Chul-soo"})
      - add_custom_dictionary("김철수", "Kim Chul-soo")
    """
    if isinstance(dict_or_key, str):
        if dict_or_key:
            _CUSTOM_DICTIONARY_STORE[dict_or_key] = str(value)
    elif isinstance(dict_or_key, dict):
        for k, v in dict_or_key.items():
            if isinstance(k, str) and k:
                _CUSTOM_DICTIONARY_STORE[k] = str(v)


def remove_custom_dictionary_entry(key):
    """내부 사용자 사전에서 키를 제거한다. 제거되었으면 True."""
    if isinstance(key, str) and key in _CUSTOM_DICTIONARY_STORE:
        del _CUSTOM_DICTIONARY_STORE[key]
        return True
    return False


def clear_custom_dictionary():
    """내부 사용자 사전을 모두 비운다."""
    _CUSTOM_DICTIONARY_STORE.clear()


def get_custom_dictionary():
    """현재 내부 사용자 사전의 스냅샷(얕은 복사)을 반환한다."""
    return dict(_CUSTOM_DICTIONARY_STORE)


def _resolve_effective_dictionary(use_custom_dictionary, custom_dictionary):
    """
    내부 사전 + 옵션 사전을 병합해 실효 사전을 만든다.
      - use_custom_dictionary=False: 내부 사전 무시
      - custom_dictionary 옵션: 항상 병합되며 동일 키는 옵션 사전이 우선
    """
    out = {}
    if use_custom_dictionary and _CUSTOM_DICTIONARY_STORE:
        out.update(_CUSTOM_DICTIONARY_STORE)
    if isinstance(custom_dictionary, dict):
        out.update(custom_dictionary)
    return out if out else None


def apply_custom_dictionary(text, dictionary):
    """Replace dictionary keys in text with placeholders. Returns (text, values)."""
    if not dictionary:
        return text, []
    keys = sorted([k for k in dictionary.keys() if k], key=len, reverse=True)
    if not keys:
        return text, []
    values = []
    for key in keys:
        value = dictionary[key]
        pos = 0
        while True:
            idx = text.find(key, pos)
            if idx == -1:
                break
            placeholder = f"{_DICT_MARK}{len(values)}{_DICT_MARK}"
            values.append(value)
            text = text[:idx] + placeholder + text[idx + len(key):]
            pos = idx + len(placeholder)
    return text, values


def restore_custom_dictionary(text, values):
    if not values:
        return text
    def _sub(m):
        i = int(m.group(1))
        return values[i] if 0 <= i < len(values) else ""
    return _DICT_PATTERN.sub(_sub, text)


def romanize(text, **options):
    """
    Convert Korean text to Romanized form.

    Args:
        text (str): Korean text to romanize
        **options: Optional parameters:
            - use_pronunciation_rules (bool): Whether to apply pronunciation rules (default: True)
            - casing_option (str|int): Casing option (default: "lowercase").
              Accepts full names, short aliases, or integers:
                * lowercase       : "lowercase" | "lower" | "l" | "lc" | 0
                * uppercase       : "uppercase" | "upper" | "u" | "uc" | 1
                * capitalize-line : "capitalize-line" | "cap-line" | "cline" | "cl" | 2
                * capitalize-word : "capitalize-word" | "cap-word" | "cword" | "cw" | 3
            - use_hyphen (bool): Insert '-' at ambiguous syllable boundaries (default: False).
              See `apply_roman_mapping_with_hyphen` for the rules:
                * 중앙 → jung-ang, 반구대 → ban-gudae,
                * 세운 → se-un, 해운대 → hae-undae
            - use_custom_dictionary (bool): Whether to apply the module-level
              persistent custom dictionary (managed via set_custom_dictionary /
              add_custom_dictionary / etc.). Default True.
            - custom_dictionary (dict[str, str]): One-shot user dictionary.
              Matching Korean words are replaced with the given Romanized form (highest priority).
              Merged with the persistent store; entries here override the store on key collision.
                * Values are preserved as-is (casing option NOT applied to dictionary values)
                * Pronunciation rules NOT applied to dictionary values
                * Longer keys matched first to avoid partial matches
              Example: {"김철수": "Kim Chul-soo", "서울": "Seoul"}

    Returns:
        str: Romanized text
    """
    use_pronunciation_rules = options.get('use_pronunciation_rules', True)
    casing_option = _normalize_casing_option(options.get('casing_option', "lowercase"))
    use_hyphen = options.get('use_hyphen', False)
    use_custom_dictionary = options.get('use_custom_dictionary', True)
    custom_dictionary = options.get('custom_dictionary', None)

    effective_dict = _resolve_effective_dictionary(use_custom_dictionary, custom_dictionary)
    text, dict_values = apply_custom_dictionary(text, effective_dict)

    if use_pronunciation_rules:
        text = apply_n_insertion_triggers(text)

    jamo_str = split_hangul_to_jamos(text)
    if use_pronunciation_rules:
        jamo_str = apply_pronunciation_rules(jamo_str)

    mapping = ROMAN_MAP_STD if use_pronunciation_rules else ROMAN_MAP_LITERAL
    if use_hyphen:
        result = apply_roman_mapping_with_hyphen(jamo_str, mapping)
    else:
        result = "".join(mapping.get(c, c) for c in jamo_str)

    if casing_option == "uppercase":
        result = result.upper()
    elif casing_option == "capitalize-word":
        result = capitalize_words(result)
    elif casing_option == "capitalize-line":
        result = capitalize_lines(result)
    else:
        result = result.lower()

    return restore_custom_dictionary(result, dict_values)

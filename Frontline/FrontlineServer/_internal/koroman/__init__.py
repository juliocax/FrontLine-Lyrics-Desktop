from .core import (
    romanize,
    _normalize_casing_option as normalize_casing_option,
    set_custom_dictionary,
    add_custom_dictionary,
    remove_custom_dictionary_entry,
    clear_custom_dictionary,
    get_custom_dictionary,
)

__version__ = '1.0.16'
__all__ = [
    'romanize',
    'normalize_casing_option',
    'set_custom_dictionary',
    'add_custom_dictionary',
    'remove_custom_dictionary_entry',
    'clear_custom_dictionary',
    'get_custom_dictionary',
]

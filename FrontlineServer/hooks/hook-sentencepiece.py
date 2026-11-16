from PyInstaller.utils.hooks import collect_delvewheel_libs_directory, collect_dynamic_libs

# Windows wheels load their vendored DLLs from a sibling sentencepiece.libs directory.
datas, binaries = collect_delvewheel_libs_directory(
    "sentencepiece", binaries=collect_dynamic_libs("sentencepiece"),
)

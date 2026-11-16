$ErrorActionPreference = 'Stop'

Push-Location -LiteralPath $PSScriptRoot
try {
    $repositoryRoot = Split-Path -Parent $PSScriptRoot
    $wpfFolder = Join-Path $repositoryRoot 'Frontline'
    if (-not (Test-Path -LiteralPath $wpfFolder -PathType Container)) {
        throw "WPF project directory not found: $wpfFolder"
    }

    $python = Join-Path $PSScriptRoot 'env\Scripts\python.exe'
    if (-not (Test-Path -LiteralPath $python -PathType Leaf)) {
        & python -m venv .venv
        if ($LASTEXITCODE -ne 0) { throw 'Could not create the Python virtual environment.' }
    }

    & $python -m pip install -r requirements.txt
    if ($LASTEXITCODE -ne 0) { throw 'Could not install requirements.txt.' }

    & $python -m pip install --no-deps argostranslate==1.11.0
    if ($LASTEXITCODE -ne 0) { throw 'Could not install the Argos tokenizer.' }

    $thaiPackage = & $python -c "import pythainlp; print(pythainlp.__path__[0])"
    if ($LASTEXITCODE -ne 0) { throw 'Could not locate the PyThaiNLP package.' }
    $thaiWords = Join-Path $thaiPackage 'corpus\words_th.txt'
    if (-not (Test-Path -LiteralPath $thaiWords -PathType Leaf)) {
        throw "PyThaiNLP word list not found: $thaiWords"
    }

    $buildArgs = @(
        '--noconfirm'
        '--clean'
        '--onedir'
        '--windowed'

        '--collect-all', 'anyascii'
        '--collect-all', 'winrt'
        '--collect-all', 'opencc'
        '--collect-all', 'pypinyin'
        '--collect-all', 'koroman'
        '--collect-all', 'cutlet'
        '--collect-all', 'fugashi'
        '--collect-data', 'langdetect'
        '--collect-data', 'certifi'
        '--collect-all', 'ctranslate2'
        '--additional-hooks-dir', 'hooks'
        '--collect-data', 'sacremoses'
        '--copy-metadata', 'ctranslate2'
        '--copy-metadata', 'sentencepiece'
        '--copy-metadata', 'sacremoses'
        '--copy-metadata', 'argostranslate'

        '--hidden-import', 'pythainlp.tokenize.newmm'
        '--hidden-import', 'pythainlp.transliterate.royin'
        '--add-data', "${thaiWords}:pythainlp/corpus"
        '--exclude-module', 'unidic_lite'
        '--exclude-module', 'torch'
        '--exclude-module', 'stanza'
        '--exclude-module', 'spacy'

        '--hidden-import', 'winrt.windows.media.control'
        '--hidden-import', 'winrt.windows.storage.streams'
        '--hidden-import', 'crash_guard'
        '--hidden-import', 'media_session'
        '--hidden-import', 'music_manager'
        '--hidden-import', 'audio_capture'
        '--hidden-import', 'recognition'
        '--hidden-import', 'lyrics'
        '--hidden-import', 'cover_art'
        '--hidden-import', 'translation'
        '--hidden-import', 'smtc_policy'
        '--hidden-import', 'tuning'
        '--hidden-import', 'task_utils'
        '--hidden-import', 'workers'
        '--hidden-import', 'ws_server'

        '--name', 'FrontlineServer'
        '--distpath', 'dist'
        '--workpath', 'build'
        '--specpath', 'build'
        'FrontlineServer.py'
    )

    & $python -m PyInstaller @buildArgs
    if ($LASTEXITCODE -ne 0) { throw 'PyInstaller failed to build FrontlineServer.' }

    $buildOutput = Join-Path $PSScriptRoot 'dist\FrontlineServer'
    $serverExe = Join-Path $buildOutput 'FrontlineServer.exe'
    $internalFolder = Join-Path $buildOutput '_internal'
    if (-not (Test-Path -LiteralPath $serverExe -PathType Leaf) -or
        -not (Test-Path -LiteralPath $internalFolder -PathType Container)) {
        throw "Incomplete PyInstaller output in $buildOutput."
    }

    $destination = Join-Path $wpfFolder 'FrontlineServer'
    $operationId = [Guid]::NewGuid().ToString('N')
    $staging = Join-Path $wpfFolder ".frontline-server-staging-$operationId"
    $backup = Join-Path $wpfFolder ".frontline-server-backup-$operationId"

    try {
        New-Item -Path $staging -ItemType Directory | Out-Null
        Copy-Item -LiteralPath $serverExe -Destination $staging
        Copy-Item -LiteralPath $internalFolder -Destination $staging -Recurse -Force

        if (Test-Path -LiteralPath $destination -PathType Container) {
            # Keep any files in the destination outside the two generated items.
            Get-ChildItem -LiteralPath $destination -Force |
                Where-Object { $_.Name -notin 'FrontlineServer.exe', '_internal' } |
                ForEach-Object {
                    Copy-Item -LiteralPath $_.FullName -Destination $staging -Recurse -Force
                }

            [System.IO.Directory]::Move($destination, $backup)
        }

        try {
            [System.IO.Directory]::Move($staging, $destination)
        }
        catch {
            if (Test-Path -LiteralPath $backup -PathType Container) {
                try {
                    [System.IO.Directory]::Move($backup, $destination)
                }
                catch {
                    throw "Could not restore the previous backend. Its backup is at $backup. $($_.Exception.Message)"
                }
            }
            throw
        }
    }
    finally {
        if (Test-Path -LiteralPath $staging -PathType Container) {
            Remove-Item -LiteralPath $staging -Recurse -Force
        }
    }

    if (Test-Path -LiteralPath $backup -PathType Container) {
        Remove-Item -LiteralPath $backup -Recurse -Force
    }
    Remove-Item -LiteralPath (Join-Path $PSScriptRoot 'build'), (Join-Path $PSScriptRoot 'dist') -Recurse -Force

    Write-Host "Build complete. Backend installed in $destination"
}
finally {
    Pop-Location
}

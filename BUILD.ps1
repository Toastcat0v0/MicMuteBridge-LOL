# Windows PowerShell 5.1, local .NET Framework compilation. No network required.
param([string]$CompileTo)

$ErrorActionPreference = 'Stop'
$path = $PSScriptRoot

if ($CompileTo) {
    try {
        # Windows PowerShell 5.1 rejects Add-Type with CompilerParameters and OutputAssembly together.
        $framework = [System.Runtime.InteropServices.RuntimeEnvironment]::GetRuntimeDirectory()
        $csc = Join-Path $framework 'csc.exe'
        if (-not [System.IO.File]::Exists($csc)) {
            throw ('The .NET Framework C# compiler was not found: ' + $csc)
        }
        $sounds = Join-Path $path 'Sounds'
        $soundNames = foreach ($preset in @('classic', 'soft', 'pixel', 'chime', 'bubble', 'digital')) {
            foreach ($state in @('mute', 'unmute')) {
                $name = $preset + '_' + $state + '.wav'
                $sound = Join-Path $sounds $name
                if (-not [System.IO.File]::Exists($sound)) {
                    throw ('Missing Sounds\' + $name + '. Extract the full ZIP before running START.cmd.')
                }
                $name
            }
        }

        $compilerArguments = @(
            '/nologo'
            '/noconfig'
            '/target:winexe'
            '/win32icon:app.ico'
            ('/out:' + $CompileTo)
            '/reference:System.dll'
            '/reference:System.Core.dll'
            '/reference:System.Windows.Forms.dll'
            '/reference:System.Drawing.dll'
            'MicMuteBridge.cs'
        )
        foreach ($name in $soundNames) {
            $compilerArguments += ('/resource:Sounds\' + $name + ',' + $name)
        }
        Push-Location -LiteralPath $path
        try {
            & $csc @compilerArguments
            if ($LASTEXITCODE -ne 0) { throw ('The C# compiler exited with code ' + $LASTEXITCODE + '.') }
        } finally {
            Pop-Location
        }

        $assembly = [System.Reflection.Assembly]::Load([System.IO.File]::ReadAllBytes($CompileTo))
        $resources = $assembly.GetManifestResourceNames()
        foreach ($name in $soundNames) {
            if ($resources -cnotcontains $name) { throw ('Compiled EXE is missing sound resource ' + $name) }
        }
        exit 0
    } catch {
        Write-Output ($_.ToString())
        exit 1
    }
}

$exe = Join-Path $path 'MicMuteBridge.exe'
$stage = Join-Path $path ('.build-' + [Guid]::NewGuid().ToString('N'))
$candidate = Join-Path $stage 'MicMuteBridge.exe'
$previous = Join-Path $stage 'MicMuteBridge.previous.exe'
$keepStage = $false
$originalTemp = $env:TEMP
$originalTmp = $env:TMP

try {
    [void][System.IO.Directory]::CreateDirectory($stage)
    $env:TEMP = $stage
    $env:TMP = $stage
    Write-Host 'Compiling Windows tray app from local source...' -ForegroundColor Cyan
    # Let the compiler process exit before moving its output.
    $compiler = Join-Path $PSHOME 'powershell.exe'
    $compilerOutput = & $compiler -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $PSCommandPath -CompileTo $candidate
    if ($LASTEXITCODE -ne 0) {
        throw ('Compilation failed.' + [Environment]::NewLine + ($compilerOutput -join [Environment]::NewLine))
    }
    if (-not [System.IO.File]::Exists($candidate)) { throw 'Compilation did not produce MicMuteBridge.exe' }
    [void][System.Reflection.AssemblyName]::GetAssemblyName($candidate)

    if ([System.IO.File]::Exists($exe)) {
        [System.IO.File]::Replace($candidate, $exe, $previous)
    } else {
        [System.IO.File]::Move($candidate, $exe)
    }
    Write-Host 'Build successful: MicMuteBridge.exe' -ForegroundColor Green
} catch {
    $failure = $_.Exception.ToString()
    if ([System.IO.File]::Exists($previous)) {
        try {
            if (-not [System.IO.File]::Exists($exe)) { [System.IO.File]::Move($previous, $exe) }
        } catch {
            $failure += [Environment]::NewLine + 'Restore failed: ' + $_.Exception.Message
        }
        $keepStage = [System.IO.File]::Exists($previous)
        if ($keepStage) {
            $failure += [Environment]::NewLine + 'Previous EXE: ' + $previous
        }
    }
    $errors = Join-Path $path 'Errors'
    $log = Join-Path $errors 'BUILD_ERROR.log'
    $detail = 'Build failure ' + (Get-Date).ToString('s') + [Environment]::NewLine + $failure
    try {
        [void][System.IO.Directory]::CreateDirectory($errors)
        [System.IO.File]::WriteAllText($log, $detail, [System.Text.Encoding]::UTF8)
        Write-Host 'Build failed. Details saved to Errors\BUILD_ERROR.log.' -ForegroundColor Red
    } catch {
        Write-Host ('Could not write BUILD_ERROR.log: ' + $_.Exception.Message) -ForegroundColor Red
    }
    Write-Host $failure -ForegroundColor Red
    exit 1
} finally {
    $env:TEMP = $originalTemp
    $env:TMP = $originalTmp
    if (-not $keepStage -and [System.IO.Directory]::Exists($stage)) {
        Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
    }
}

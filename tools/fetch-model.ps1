<#
.SYNOPSIS
    Downloads EmbeddingGemma-300m (ONNX, q8) for Valafind.

.DESCRIPTION
    Default target is the per-user models folder Valafind looks in:
      Windows: %LOCALAPPDATA%\Valafind\models\embeddinggemma-300m
      macOS:   ~/Library/Application Support/Valafind/models/embeddinggemma-300m
      Linux:   ${XDG_DATA_HOME:-~/.local/share}/valafind/models/embeddinggemma-300m
    VALAFIND_DATA_DIR overrides the data folder, as it does for the app.

    This one-time download is the ONLY network access involved; the Valafind app itself never downloads anything.

.PARAMETER Target
    Directory to download into (optional).

.PARAMETER Force
    Re-download files that are already present.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\fetch-model.ps1
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)][string]$Target,
    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # the progress bar makes Invoke-WebRequest very slow on Windows PowerShell

$BaseUrl = 'https://huggingface.co/onnx-community/embeddinggemma-300m-ONNX/resolve/main'
$Files = @(
    'tokenizer.model',
    'tokenizer.json',
    'tokenizer_config.json',
    'special_tokens_map.json',
    'config.json',
    'onnx/model_quantized.onnx',
    'onnx/model_quantized.onnx_data'
)

if (-not $Target) {
    if ($env:VALAFIND_DATA_DIR) {
        $data = $env:VALAFIND_DATA_DIR
    }
    elseif ((Get-Variable IsMacOS -ErrorAction SilentlyContinue) -and $IsMacOS) {
        $data = Join-Path $HOME 'Library/Application Support/Valafind'
    }
    elseif ((Get-Variable IsLinux -ErrorAction SilentlyContinue) -and $IsLinux) {
        $xdg = if ($env:XDG_DATA_HOME) { $env:XDG_DATA_HOME } else { Join-Path $HOME '.local/share' }
        $data = Join-Path $xdg 'valafind'
    }
    else {
        $data = Join-Path $env:LOCALAPPDATA 'Valafind'
    }
    $Target = Join-Path (Join-Path $data 'models') 'embeddinggemma-300m'
}

Write-Host @'
EmbeddingGemma is provided under and subject to the Gemma Terms of Use:
  https://ai.google.dev/gemma/terms
By downloading the model you agree to those terms.

This one-time download is the only network access Valafind needs.
The Valafind app itself never downloads anything and runs fully offline.

'@

# Older Windows PowerShell defaults may not enable TLS 1.2.
try { [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12 } catch { }

Write-Host "Target: $Target"
New-Item -ItemType Directory -Force -Path (Join-Path $Target 'onnx') | Out-Null

foreach ($f in $Files) {
    $dest = Join-Path $Target ($f -replace '/', [IO.Path]::DirectorySeparatorChar)
    if (-not $Force -and (Test-Path -LiteralPath $dest) -and ((Get-Item -LiteralPath $dest).Length -gt 0)) {
        Write-Host "  [skip] $f (already present)"
        continue
    }
    Write-Host "  [get]  $f"
    $tmp = "$dest.part"
    if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp -Force }
    $ok = $false
    for ($attempt = 1; $attempt -le 3 -and -not $ok; $attempt++) {
        try {
            Invoke-WebRequest -Uri "$BaseUrl/$($f)?download=true" -OutFile $tmp -UseBasicParsing
            $ok = $true
        }
        catch {
            if ($attempt -eq 3) {
                if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp -Force }
                throw "Failed to download $f : $($_.Exception.Message)"
            }
            Start-Sleep -Seconds 2
        }
    }
    Move-Item -LiteralPath $tmp -Destination $dest -Force
}

Write-Host ''
Write-Host "Done. Valafind will find the model automatically in: $Target"

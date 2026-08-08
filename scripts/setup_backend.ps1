# Setup the Windows Python backend venv for Meeting Transcriber.
# Run from PowerShell (Windows side). Creates backend\.venv and installs deps.
$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$backendDir = Join-Path $root "backend"
$venvDir = Join-Path $backendDir ".venv"

Write-Host "Creating venv at $venvDir ..."
python -m venv $venvDir

$python = Join-Path $venvDir "Scripts\python.exe"
Write-Host "Upgrading pip ..."
& $python -m pip install --upgrade pip

# Install CUDA-enabled torch FIRST so Whisper can use the GPU.
# cu128 supports Blackwell (sm_120), e.g. RTX 5090. Installing before the
# requirements.txt install lets pip resolve the 'torch' dependency to this
# CUDA build instead of the CPU-only PyPI default. --force-reinstall replaces
# an already-present CPU torch build. Whisper reads audio via ffmpeg/numpy, so
# torchaudio is NOT required and is intentionally omitted (its version must
# exactly match torch's).
$torchIndex = "https://download.pytorch.org/whl/cu128"
Write-Host "Installing CUDA torch (cu128) ..."
& $python -m pip install --force-reinstall torch --index-url $torchIndex

Write-Host "Installing remaining dependencies ..."
& $python -m pip install -r (Join-Path $backendDir "requirements.txt")

Write-Host ""
Write-Host "Backend ready. venv python: $python"
Write-Host "Verify GPU with: & $python -c 'import torch; print(torch.cuda.is_available())"

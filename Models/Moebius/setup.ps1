param([switch]$ReuseInstalledTorch)
$ErrorActionPreference = 'Stop'
$taskRoot = $PSScriptRoot
$taskVenv = Join-Path $taskRoot '.venv'
if (-not (Test-Path -LiteralPath (Join-Path $taskVenv 'Scripts\python.exe'))) {
    if ($ReuseInstalledTorch) { python -m venv --system-site-packages $taskVenv }
    else { python -m venv $taskVenv }
    if ($LASTEXITCODE -ne 0) { throw 'Could not create virtual environment' }
}
$taskPython = Join-Path $taskVenv 'Scripts\python.exe'
& $taskPython -c 'import torch; assert torch.cuda.is_available(), "Install a CUDA PyTorch build into this environment before continuing"'
if ($LASTEXITCODE -ne 0) { throw 'A working CUDA PyTorch installation is required. See README.md.' }
& $taskPython -m pip install -r (Join-Path $taskRoot 'requirements.txt')
if ($LASTEXITCODE -ne 0) { throw 'Dependency installation failed' }
& $taskPython (Join-Path $taskRoot 'moebius.py') prepare
if ($LASTEXITCODE -ne 0) { throw 'Model preparation failed' }
& $taskPython (Join-Path $taskRoot 'moebius.py') doctor

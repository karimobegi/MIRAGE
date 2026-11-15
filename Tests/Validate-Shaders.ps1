[CmdletBinding()]
param([string]$RepositoryRoot = '')
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) { $RepositoryRoot = Split-Path -Parent $PSScriptRoot }
if (-not ('MirageD3DCompiler' -as [type])) {
    Add-Type -Path (Join-Path $PSScriptRoot 'D3DCompiler.cs')
}

function Find-DxcCompiler {
    $cmd = Get-Command dxc -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $sdk = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin\*\x64\dxc.exe' -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | Select-Object -First 1
    if ($sdk) { return $sdk.FullName }
    return $null
}

function Expand-ShaderSource([string]$Path, [string[]]$IncludeStack = @()) {
    $resolved = (Resolve-Path -LiteralPath $Path).Path
    if ($IncludeStack -contains $resolved) { throw "Recursive shader include: $resolved" }
    $directory = Split-Path -Parent $resolved
    $source = Get-Content -LiteralPath $resolved -Raw
    $stack = @($IncludeStack) + $resolved
    $source = [regex]::Replace($source, '(?m)^\s*#include\s+"([^"]+)"\s*$', {
        param($match)
        $includePath = Join-Path $directory $match.Groups[1].Value
        Expand-ShaderSource $includePath $stack
    })
    # Unity consumes kernel pragmas; D3DCompiler and DXC would otherwise warn about them.
    return [regex]::Replace($source, '(?m)^\s*#pragma\s+kernel\s+.*$', '')
}

$dxcExe = Find-DxcCompiler
$dxcTempDir = Join-Path ([System.IO.Path]::GetTempPath()) ('mirage-dxc-' + [guid]::NewGuid().ToString('N'))
if ($dxcExe) { New-Item -ItemType Directory -Path $dxcTempDir -Force | Out-Null }

$shaderRoot = Join-Path $RepositoryRoot 'MIRAGE Unity/Assets/Resources'
$kernelCount = 0
$dxcCount = 0
$failures = @()
try {
    foreach ($file in Get-ChildItem -LiteralPath $shaderRoot -Recurse -File -Filter '*.compute') {
        $original = Get-Content -LiteralPath $file.FullName -Raw
        $kernels = [regex]::Matches($original, '(?m)^\s*#pragma\s+kernel\s+(\w+)')
        $expanded = Expand-ShaderSource $file.FullName
        $expandedPath = if ($dxcExe) { Join-Path $dxcTempDir ($file.BaseName + '.hlsl') } else { $null }
        if ($expandedPath) { [System.IO.File]::WriteAllText($expandedPath, $expanded) }

        foreach ($kernel in $kernels) {
            $entry = $kernel.Groups[1].Value
            try {
                $diagnostics = [MirageD3DCompiler]::Compile($expanded, $file.FullName, $entry)
                $kernelCount++
                $dxcTag = ''
                if ($dxcExe) {
                    foreach ($profile in @('cs_6_0', 'cs_6_6')) {
                        $dxilOut = Join-Path $dxcTempDir "$($file.BaseName)_$($entry)_$profile.dxil"
                        $dxcOutput = & $dxcExe -T $profile -E $entry $expandedPath -Fo $dxilOut 2>&1
                        if ($LASTEXITCODE -ne 0) {
                            throw "DXC ($profile) $($file.Name)::$entry`n$($dxcOutput -join "`n")"
                        }
                    }
                    $dxcCount++
                    $dxcTag = ' [FXC cs_5_0 + DXC cs_6_0/cs_6_6]'
                }
                Write-Output "PASS $($file.Name) :: $entry$dxcTag"
                if ($diagnostics) { Write-Output $diagnostics }
            } catch {
                $failures += $_.Exception.Message
                Write-Output "FAIL $($file.Name) :: $entry"
            }
        }
    }
} finally {
    if ($dxcExe) { Remove-Item -LiteralPath $dxcTempDir -Recurse -Force -ErrorAction SilentlyContinue }
}
if ($failures.Count -gt 0) { throw ($failures -join "`n`n") }
if ($dxcExe) {
    Write-Output "PASS: $kernelCount actual compute shader kernels compiled cleanly under D3D FXC (cs_5_0) and DXC HLSL 2021 (cs_6_0/cs_6_6 for Unity 6.6/6.7). This does not execute GPU kernels or verify Unity resource bindings."
} else {
    Write-Output "PASS: $kernelCount actual compute shader kernels compiled for D3D cs_5_0 (DXC not found). This does not execute GPU kernels or verify Unity resource bindings."
}

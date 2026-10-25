[CmdletBinding()]
param([string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference = 'Stop'
if (-not ('MirageD3DCompiler' -as [type])) {
    Add-Type -Path (Join-Path $PSScriptRoot 'D3DCompiler.cs')
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
    # Unity consumes kernel pragmas; D3DCompiler would otherwise warn about them.
    return [regex]::Replace($source, '(?m)^\s*#pragma\s+kernel\s+.*$', '')
}

$shaderRoot = Join-Path $RepositoryRoot 'MIRAGE Unity/Assets/Resources'
$kernelCount = 0
$failures = @()
foreach ($file in Get-ChildItem -LiteralPath $shaderRoot -Recurse -File -Filter '*.compute') {
    $original = Get-Content -LiteralPath $file.FullName -Raw
    $kernels = [regex]::Matches($original, '(?m)^\s*#pragma\s+kernel\s+(\w+)')
    $expanded = Expand-ShaderSource $file.FullName
    foreach ($kernel in $kernels) {
        $entry = $kernel.Groups[1].Value
        try {
            $diagnostics = [MirageD3DCompiler]::Compile($expanded, $file.FullName, $entry)
            $kernelCount++
            Write-Output "PASS $($file.Name) :: $entry"
            if ($diagnostics) { Write-Output $diagnostics }
        } catch {
            $failures += $_.Exception.Message
            Write-Output "FAIL $($file.Name) :: $entry"
        }
    }
}
if ($failures.Count -gt 0) { throw ($failures -join "`n`n") }
Write-Output "PASS: $kernelCount actual compute shader kernels compiled for D3D cs_5_0. This does not execute GPU kernels or verify Unity resource bindings."

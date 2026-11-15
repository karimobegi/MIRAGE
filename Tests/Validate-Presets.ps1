[CmdletBinding()]
param([string]$RepositoryRoot = '')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Invoke-RoslynCheck.ps1')
$RepositoryRoot = Resolve-MirageRepositoryRoot $RepositoryRoot $PSScriptRoot
$taskStore = Join-Path $RepositoryRoot 'MIRAGE Unity/Assets/Resources/UI/Scripts/Presets/MiragePresetStore.cs'
Invoke-MirageRoslynHarness -SourceFiles @($taskStore, (Join-Path $PSScriptRoot 'PresetStoreChecks.cs')) -EntryExpression '[PresetStoreChecks]::Run()' -CompilerOptions @('/nowarn:1701') -IncludeNewtonsoftJson
Write-Output 'LIMIT: this executes the real storage/validation code. Run Unity UI checks for restored cards, controls and rendered effects.'

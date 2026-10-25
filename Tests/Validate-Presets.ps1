[CmdletBinding()]
param([string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference = 'Stop'
$taskStore = Join-Path $RepositoryRoot 'MIRAGE Unity/Assets/Resources/UI/Scripts/Presets/MiragePresetStore.cs'
$taskJsonAssembly = [Newtonsoft.Json.JsonConvert].Assembly.Location
$taskFrameworkReferences = Get-ChildItem -LiteralPath (Join-Path $PSHOME 'ref') -Filter '*.dll' | ForEach-Object FullName
Add-Type -Path @($taskStore, (Join-Path $PSScriptRoot 'PresetStoreChecks.cs')) -ReferencedAssemblies @($taskFrameworkReferences + $taskJsonAssembly) -CompilerOptions '/nowarn:1701'
[PresetStoreChecks]::Run()
Write-Output 'LIMIT: this executes the real storage/validation code. Run Unity UI checks for restored cards, controls and rendered effects.'

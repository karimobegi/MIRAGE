[CmdletBinding()]
param([string]$RepositoryRoot = '')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Invoke-RoslynCheck.ps1')
$RepositoryRoot = Resolve-MirageRepositoryRoot $RepositoryRoot $PSScriptRoot
$taskResources = Join-Path $RepositoryRoot 'MIRAGE Unity/Assets/Resources'
$taskSources = @(
    (Join-Path $PSScriptRoot 'PresetUiStubs.cs'),
    (Join-Path $taskResources 'PostProcessing/IEffectHandler.cs'),
    (Join-Path $taskResources 'UI/Scripts/Presets/MiragePresetStore.cs'),
    (Join-Path $taskResources 'UI/Scripts/PipelineUIHandler.cs'),
    (Join-Path $taskResources 'UI/Scripts/ClassPanel.cs'),
    (Join-Path $taskResources 'UI/Scripts/EffectPanel.cs'),
    (Join-Path $taskResources 'UI/Scripts/ToggleClass.cs'),
    (Join-Path $taskResources 'UI/Scripts/UIToggleHandler.cs'),
    (Join-Path $taskResources 'UI/Scripts/OpacitySlider.cs'),
    (Join-Path $taskResources 'UI/Scripts/GaussianBlurSlider.cs'),
    (Join-Path $taskResources 'UI/Scripts/KuwaharaSlider.cs'),
    (Join-Path $PSScriptRoot 'PresetUiChecks.cs')
)
Invoke-MirageRoslynHarness -SourceFiles $taskSources -EntryExpression '[PresetUiChecks]::Run()' -CompilerOptions @('/nowarn:1701') -IncludeNewtonsoftJson
Write-Output 'LIMIT: production preset/UI code is exercised against managed Unity seams. Native prefab layout, UI events and rendering still require Unity Play-mode validation.'

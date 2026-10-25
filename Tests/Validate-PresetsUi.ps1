[CmdletBinding()]
param([string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference = 'Stop'
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
$taskJsonAssembly = [Newtonsoft.Json.JsonConvert].Assembly.Location
$taskFrameworkReferences = Get-ChildItem -LiteralPath (Join-Path $PSHOME 'ref') -Filter '*.dll' | ForEach-Object FullName
Add-Type -Path $taskSources -ReferencedAssemblies @($taskFrameworkReferences + $taskJsonAssembly) -CompilerOptions '/nowarn:1701'
[PresetUiChecks]::Run()
Write-Output 'LIMIT: production preset/UI code is exercised against managed Unity seams. Native prefab layout, UI events and rendering still require Unity Play-mode validation.'

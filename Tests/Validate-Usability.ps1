[CmdletBinding()]
param([string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference = 'Stop'
$taskRuntime = Join-Path $RepositoryRoot 'MIRAGE Unity/Assets/Resources'
$taskSources = @(
    (Join-Path $PSScriptRoot 'RuntimeStubs.cs'),
    (Join-Path $PSScriptRoot 'UsabilityStubs.cs'),
    (Join-Path $PSScriptRoot 'RuntimeChecks.cs'),
    (Join-Path $PSScriptRoot 'UsabilityChecks.cs')
)
foreach ($taskSource in @(
    'Models/ModelRunner.cs','Models/SegmentationRunner.cs','Models/DepthEstimationRunner.cs','Models/InpaintingRunner.cs',
    'Models/DepthAnthing/DepthAnythingRunner.cs','Models/YOLO/YOLORunner.cs','Models/Pipeline.cs','Scripts/CameraInput.cs',
    'PostProcessing/CPUPostProcessor.cs','PostProcessing/PostProcessor.cs','PostProcessing/ImagePostProcessor.cs',
    'PostProcessing/ImageCopyPostProcessor.cs','PostProcessing/GaussianBlurImagePostProcessor.cs','PostProcessing/ObjectInfoPostProcessor.cs',
    'Models/MI-GAN/InpaintingMaskBuilder.cs','Models/MI-GAN/InpaintingCropUtility.cs','Models/MI-GAN/MIGANRunner.cs',
    'Models/MI-GAN/Editor/InpaintingGpuValidation.cs','Models/Moebius/MoebiusRunner.cs','Models/Moebius/Editor/MoebiusSetupMenu.cs',
    'Scripts/Diagnostics/SessionLogWriter.cs','Scripts/Diagnostics/MirageDiagnostics.cs','Scripts/Diagnostics/MirageDiagnosticsRuntime.cs',
    'Scripts/Usability/MirageSetupReport.cs','Scripts/Usability/MirageOperatorPanel.cs','Scripts/Usability/VideoFilePicker.cs',
    'Scripts/StudyManager.cs',
    'Editor/MirageDesktopPreparation.cs'
)) { $taskSources += Join-Path $taskRuntime $taskSource }
$taskReferences = @(Get-ChildItem -LiteralPath (Join-Path $PSHOME 'ref') -Filter '*.dll' | ForEach-Object { $_.FullName })
$taskReferences += [Newtonsoft.Json.Linq.JObject].Assembly.Location
Add-Type -Path $taskSources -ReferencedAssemblies ($taskReferences | Select-Object -Unique) -IgnoreWarnings -CompilerOptions '/define:UNITY_EDITOR,UNITY_EDITOR_WIN,MIRAGE_DIAGNOSTICS_REAL','/nowarn:1701'
[MirageRuntimeChecks]::Run()
$taskOutput = Join-Path $PSScriptRoot ('.cache/usability-' + [guid]::NewGuid().ToString('N'))
[MirageUsabilityChecks]::Run($taskOutput)
Write-Output 'LIMIT: actual C# and filesystem code are exercised with explicit Unity seams. Native Unity GUI, file-dialog interaction, device events, rendering, and player builds remain unverified.'

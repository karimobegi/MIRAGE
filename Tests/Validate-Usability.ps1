[CmdletBinding()]
param([string]$RepositoryRoot = '')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Invoke-RoslynCheck.ps1')
$RepositoryRoot = Resolve-MirageRepositoryRoot $RepositoryRoot $PSScriptRoot
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
$taskOutput = Join-Path $PSScriptRoot ('.cache/usability-' + [guid]::NewGuid().ToString('N'))
Invoke-MirageRoslynHarness -SourceFiles $taskSources -EntryExpression '[MirageRuntimeChecks]::Run() + [System.Environment]::NewLine + [MirageUsabilityChecks]::Run($args[0])' -EntryArguments @($taskOutput) -CompilerOptions @('/define:UNITY_EDITOR,UNITY_EDITOR_WIN,MIRAGE_DIAGNOSTICS_REAL','/nowarn:1701') -IncludeNewtonsoftJson
Write-Output 'LIMIT: actual C# and filesystem code are exercised with explicit Unity seams. Native Unity GUI, file-dialog interaction, device events, rendering, and player builds remain unverified.'

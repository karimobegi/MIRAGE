[CmdletBinding()]
param([string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference = 'Stop'
$runtimeRoot = Join-Path $RepositoryRoot 'MIRAGE Unity/Assets/Resources'
$sources = @(
    (Join-Path $PSScriptRoot 'RuntimeStubs.cs'),
    (Join-Path $runtimeRoot 'Models/ModelRunner.cs'),
    (Join-Path $runtimeRoot 'Models/SegmentationRunner.cs'),
    (Join-Path $runtimeRoot 'Models/DepthEstimationRunner.cs'),
    (Join-Path $runtimeRoot 'Models/InpaintingRunner.cs'),
    (Join-Path $runtimeRoot 'Models/DepthAnthing/DepthAnythingRunner.cs'),
    (Join-Path $runtimeRoot 'Models/YOLO/YOLORunner.cs'),
    (Join-Path $runtimeRoot 'Scripts/CameraInput.cs'),
    (Join-Path $runtimeRoot 'Models/Pipeline.cs'),
    (Join-Path $runtimeRoot 'PostProcessing/CPUPostProcessor.cs'),
    (Join-Path $runtimeRoot 'PostProcessing/PostProcessor.cs'),
    (Join-Path $runtimeRoot 'PostProcessing/ImagePostProcessor.cs'),
    (Join-Path $runtimeRoot 'PostProcessing/ImageCopyPostProcessor.cs'),
    (Join-Path $runtimeRoot 'PostProcessing/GaussianBlurImagePostProcessor.cs'),
    (Join-Path $runtimeRoot 'PostProcessing/ObjectInfoPostProcessor.cs'),
    (Join-Path $runtimeRoot 'Models/MI-GAN/InpaintingMaskBuilder.cs'),
    (Join-Path $runtimeRoot 'Models/MI-GAN/InpaintingCropUtility.cs'),
    (Join-Path $runtimeRoot 'Models/MI-GAN/MIGANRunner.cs'),
    (Join-Path $runtimeRoot 'Models/MI-GAN/Editor/InpaintingGpuValidation.cs'),
    (Join-Path $runtimeRoot 'Models/Moebius/MoebiusRunner.cs'),
    (Join-Path $runtimeRoot 'Models/Moebius/Editor/MoebiusSetupMenu.cs'),
    (Join-Path $PSScriptRoot 'RuntimeChecks.cs')
)
Add-Type -Path $sources -IgnoreWarnings -CompilerOptions '/define:UNITY_EDITOR'
[MirageRuntimeChecks]::Run()
Write-Output 'LIMIT: Unity rendering, inference operations, native event order and real API compatibility are stubbed. Run a Unity import/build and recorded-video GPU validation before making performance/visual-quality claims.'

[CmdletBinding()]
param([string]$RepositoryRoot = '')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Invoke-RoslynCheck.ps1')
$RepositoryRoot = Resolve-MirageRepositoryRoot $RepositoryRoot $PSScriptRoot
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
    (Join-Path $runtimeRoot 'PostProcessing/IconPostProcessor.cs'),
    (Join-Path $runtimeRoot 'PostProcessing/BoundsPostProcessor.cs'),
    (Join-Path $runtimeRoot 'Models/MI-GAN/InpaintingMaskBuilder.cs'),
    (Join-Path $runtimeRoot 'Models/MI-GAN/InpaintingCropUtility.cs'),
    (Join-Path $runtimeRoot 'Models/MI-GAN/MIGANRunner.cs'),
    (Join-Path $runtimeRoot 'Models/MI-GAN/Editor/InpaintingGpuValidation.cs'),
    (Join-Path $runtimeRoot 'Models/Moebius/MoebiusRunner.cs'),
    (Join-Path $runtimeRoot 'Models/Moebius/Editor/MoebiusSetupMenu.cs'),
    (Join-Path $PSScriptRoot 'RuntimeChecks.cs')
)
Invoke-MirageRoslynHarness -SourceFiles $sources -EntryExpression '[MirageRuntimeChecks]::Run()' -CompilerOptions @('/define:UNITY_EDITOR')

$lapjvSource = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'MIRAGE Unity/Assets/ByteTrack-CSharp/lapjv.cs') -Raw
$lapjvHarness = $lapjvSource + @'

public static class MirageLapjvCheck
{
    public static unsafe string Run()
    {
        int n = 3;
        double* r0 = stackalloc double[3] { 10.0, 1.0, 9.0 };
        double* r1 = stackalloc double[3] { 8.0, 12.0, 2.0 };
        double* r2 = stackalloc double[3] { 3.0, 11.0, 10.0 };
        double** cost = stackalloc double*[3] { r0, r1, r2 };
        int* x = stackalloc int[3];
        int* y = stackalloc int[3];
        int rc = ByteTrackCSharp.lapjv.lapjv_internal(n, cost, x, y);
        if (rc != 0 || x[0] != 1 || x[1] != 2 || x[2] != 0)
            throw new System.Exception("Managed ByteTrack lapjv assignment failed.");
        return "PASS: managed ByteTrack lapjv linear assignment verified without native CRT P/Invoke (CoreCLR-safe).";
    }
}
'@
Invoke-MirageRoslynHarness -InlineSource $lapjvHarness -EntryExpression '[MirageLapjvCheck]::Run()' -CompilerOptions @('/unsafe')
Write-Output 'LIMIT: Unity rendering, inference operations, native event order and real API compatibility are stubbed. Run a Unity import/build and recorded-video GPU validation before making performance/visual-quality claims.'

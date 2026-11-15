function Resolve-MirageRepositoryRoot([string]$ProvidedRoot, [string]$ScriptDir) {
    if (-not [string]::IsNullOrWhiteSpace($ProvidedRoot)) { return $ProvidedRoot }
    return (Split-Path -Parent $ScriptDir)
}

function Invoke-MirageRoslynHarness {
    param(
        [string[]]$SourceFiles = @(),
        [string]$InlineSource = '',
        [string]$EntryExpression,
        [string[]]$EntryArguments = @(),
        [string[]]$CompilerOptions = @(),
        [switch]$IncludeNewtonsoftJson
    )

    if ($PSVersionTable.PSVersion.Major -ge 6) {
        $refs = @(Get-ChildItem -LiteralPath (Join-Path $PSHOME 'ref') -Filter '*.dll' | ForEach-Object { $_.FullName })
        if ($IncludeNewtonsoftJson) {
            $refs += [Newtonsoft.Json.JsonConvert].Assembly.Location
        }
        $refs = @($refs | Select-Object -Unique)
        if ($InlineSource) {
            Add-Type -TypeDefinition $InlineSource -ReferencedAssemblies $refs -IgnoreWarnings -CompilerOptions $CompilerOptions
        } else {
            Add-Type -Path $SourceFiles -ReferencedAssemblies $refs -IgnoreWarnings -CompilerOptions $CompilerOptions
        }
        $args = $EntryArguments
        return (Invoke-Expression $EntryExpression)
    }

    # Windows PowerShell 5.1 fallback: perform strict Roslyn C# compilation verification
    # (including UNITY_6000_4_OR_NEWER for Unity 6.6/6.7) using Unity's bundled Roslyn compiler.
    $unityEditors = @(Get-ChildItem 'C:\Program Files\Unity\Hub\Editor' -Directory -ErrorAction SilentlyContinue | Sort-Object Name -Descending)
    $cscDll = $null
    $dotnetExe = $null
    $netCoreDir = $null
    $jsonDll = $null

    foreach ($editor in $unityEditors) {
        $candidateCsc = Join-Path $editor.FullName 'Editor\Data\DotNetSdkRoslyn\csc.dll'
        $candidateDotnet = Join-Path $editor.FullName 'Editor\Data\NetCoreRuntime\dotnet.exe'
        $sharedRoot = Join-Path $editor.FullName 'Editor\Data\NetCoreRuntime\shared\Microsoft.NETCore.App'
        $candidateJson = Join-Path $editor.FullName 'Editor\Data\Managed\Newtonsoft.Json.dll'
        if ((Test-Path $candidateCsc) -and (Test-Path $candidateDotnet) -and (Test-Path $sharedRoot)) {
            $versionDir = Get-ChildItem -LiteralPath $sharedRoot -Directory | Sort-Object Name -Descending | Select-Object -First 1
            if ($versionDir) {
                $cscDll = $candidateCsc
                $dotnetExe = $candidateDotnet
                $netCoreDir = $versionDir.FullName
                if (Test-Path $candidateJson) { $jsonDll = $candidateJson }
                break
            }
        }
    }

    if (-not $cscDll) {
        throw 'PowerShell 7 (pwsh) or a Unity 6 Editor with DotNetSdkRoslyn is required to validate C# 9+ MIRAGE sources.'
    }

    $workDir = Join-Path ([System.IO.Path]::GetTempPath()) ('mirage-roslyn-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $workDir -Force | Out-Null
    try {
        $allSources = @($SourceFiles)
        if ($InlineSource) {
            $inlinePath = Join-Path $workDir 'InlineCheck.cs'
            [System.IO.File]::WriteAllText($inlinePath, $InlineSource)
            $allSources += $inlinePath
        }

        $csExpr = $EntryExpression -replace '\[([A-Za-z0-9_.]+)\]::', '$1.'
        $csExpr = $csExpr.Replace('$args', 'args')
        $entryPath = Join-Path $workDir 'ProgramEntry.cs'
        $entryCode = @"
public static class MirageRoslynRunnerEntry
{
    public static int Main(string[] args)
    {
        object result = $csExpr;
        if (result != null) System.Console.WriteLine(result);
        return 0;
    }
}
"@
        [System.IO.File]::WriteAllText($entryPath, $entryCode)
        $allSources += $entryPath

        $coreRefs = Get-ChildItem -LiteralPath $netCoreDir -Filter '*.dll' | Where-Object {
            $_.Name -match '^(System\.|mscorlib\.dll$|netstandard\.dll$|Microsoft\.CSharp\.dll$)' -and
            $_.Name -notmatch 'Native'
        }

        foreach ($unityStreamDefines in @('', 'UNITY_6000_4_OR_NEWER')) {
            $outDll = Join-Path $workDir ("MirageCheck_" + $(if ($unityStreamDefines) { 'u66' } else { 'base' }) + ".dll")
            $rspPath = Join-Path $workDir 'compile.rsp'
            $rspLines = @(
                '/nologo',
                '/target:exe',
                '/langversion:latest',
                '/nowarn:0169,0414,0618,0649,1701,1702',
                "/out:`"$outDll`""
            )
            foreach ($opt in $CompilerOptions) {
                if (-not [string]::IsNullOrWhiteSpace($opt)) { $rspLines += $opt }
            }
            if ($unityStreamDefines) {
                $rspLines += "/define:$unityStreamDefines"
            }
            foreach ($ref in $coreRefs) {
                $rspLines += "/r:`"$($ref.FullName)`""
            }
            if ($IncludeNewtonsoftJson) {
                if (-not $jsonDll) { throw 'Newtonsoft.Json.dll was not found in the Unity Editor Managed folder.' }
                $rspLines += "/r:`"$jsonDll`""
            }
            foreach ($src in $allSources) {
                $rspLines += "`"$src`""
            }
            [System.IO.File]::WriteAllLines($rspPath, $rspLines)

            $compileOut = & $dotnetExe $cscDll "@$rspPath" 2>&1
            if ($LASTEXITCODE -ne 0) {
                throw ("Roslyn compilation failed ($unityStreamDefines):`n" + ($compileOut -join "`n"))
            }
        }

        return "PASS (Roslyn C# compile check across baseline + UNITY_6000_4_OR_NEWER [Unity 6.6/6.7]): $($SourceFiles.Count) source file(s) compiled cleanly. Install PowerShell 7 (pwsh) to execute managed test assemblies on hosts with Smart App Control."
    } finally {
        Remove-Item -LiteralPath $workDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

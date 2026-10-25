[CmdletBinding()]
param([string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference = 'Stop'
$taskReferences = @(Get-ChildItem -LiteralPath (Join-Path $PSHOME 'ref') -Filter '*.dll' | ForEach-Object { $_.FullName })
$taskReferences += [Newtonsoft.Json.Linq.JObject].Assembly.Location
$taskSources = @(
    (Join-Path $RepositoryRoot 'MIRAGE Unity/Assets/Resources/Scripts/Diagnostics/SessionLogWriter.cs'),
    (Join-Path $PSScriptRoot 'LoggingChecks.cs')
)
Add-Type -Path $taskSources -ReferencedAssemblies ($taskReferences | Select-Object -Unique) -IgnoreWarnings -CompilerOptions '/nowarn:1701'
$taskOutput = Join-Path $PSScriptRoot ('.cache/logging-' + [guid]::NewGuid().ToString('N'))
[MirageLoggingChecks]::Run($taskOutput)
Write-Output 'This executes the actual .NET writer against real local files and ZIPs. Unity bootstrap/log callbacks/health sampling still require a Unity run.'

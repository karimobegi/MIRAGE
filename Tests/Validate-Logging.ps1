[CmdletBinding()]
param([string]$RepositoryRoot = '')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Invoke-RoslynCheck.ps1')
$RepositoryRoot = Resolve-MirageRepositoryRoot $RepositoryRoot $PSScriptRoot
$taskSources = @(
    (Join-Path $RepositoryRoot 'MIRAGE Unity/Assets/Resources/Scripts/Diagnostics/SessionLogWriter.cs'),
    (Join-Path $PSScriptRoot 'LoggingChecks.cs')
)
$taskOutput = Join-Path $PSScriptRoot ('.cache/logging-' + [guid]::NewGuid().ToString('N'))
Invoke-MirageRoslynHarness -SourceFiles $taskSources -EntryExpression '[MirageLoggingChecks]::Run($args[0])' -EntryArguments @($taskOutput) -CompilerOptions @('/nowarn:1701') -IncludeNewtonsoftJson
Write-Output 'This executes the actual .NET writer against real local files and ZIPs. Unity bootstrap/log callbacks/health sampling still require a Unity run.'

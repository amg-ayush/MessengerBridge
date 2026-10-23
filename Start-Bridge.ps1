param([switch]$Check, [switch]$Status, [switch]$RetryFailed)
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'src\MessengerBridge\MessengerBridge.csproj'
$bridgeArgs = @()
if ($Check) { $bridgeArgs += '--check' }
if ($Status) { $bridgeArgs += '--status' }
if ($RetryFailed) { $bridgeArgs += '--retry-failed' }
if ($bridgeArgs.Count -gt 1) { throw 'Use only one of -Check, -Status, -RetryFailed.' }
& dotnet run --project $project --configuration Release -- @bridgeArgs
exit $LASTEXITCODE
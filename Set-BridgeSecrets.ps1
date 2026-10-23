$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'src\MessengerBridge\MessengerBridge.csproj'
$telegram = Read-Host 'New Telegram bot token' -AsSecureString
$max = Read-Host 'MAX bot token' -AsSecureString
try {
    $payload = @{
        'Bridge:Telegram:Token' = [System.Net.NetworkCredential]::new('', $telegram).Password
        'Bridge:Max:Token' = [System.Net.NetworkCredential]::new('', $max).Password
    } | ConvertTo-Json -Compress
    # Pass secrets over stdin instead of command-line arguments or shell history.
    $payload | & dotnet user-secrets set --project $project
    if ($LASTEXITCODE -ne 0) { throw 'Could not save User Secrets.' }
}
finally {
    $payload = $null
    $telegram.Dispose()
    $max.Dispose()
}
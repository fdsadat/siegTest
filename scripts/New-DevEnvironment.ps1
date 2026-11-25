param([string]$OutputPath = (Join-Path $PSScriptRoot '..\.env'))
$ErrorActionPreference = 'Stop'
if (Test-Path -LiteralPath $OutputPath) { Write-Output '.env already exists; secrets preserved.'; exit 0 }
function New-RandomSecret([int]$Length) {
    $secretBytes = New-Object byte[] $Length
    $secretGenerator = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $secretGenerator.GetBytes($secretBytes) }
    finally { $secretGenerator.Dispose() }
    return [Convert]::ToBase64String($secretBytes)
}
$devEnvironment = @(
    ('SQL_SA_PASSWORD=Sql!' + (New-RandomSecret 32) + '1aA'),
    ('SQL_APP_PASSWORD=App!' + (New-RandomSecret 32) + '1aA'),
    ('RABBIT_PASSWORD=' + (New-RandomSecret 32)),
    ('HMAC_KEY=' + (New-RandomSecret 32)),
    ('JWT_SIGNING_KEY=' + (New-RandomSecret 48))
)
[System.IO.File]::WriteAllLines([System.IO.Path]::GetFullPath($OutputPath), $devEnvironment)
Write-Output 'Created ignored .env with independent random secrets. Keep it stable between runs.'

param(
    [string]$EnvironmentPath = (Join-Path $PSScriptRoot '..\.env'),
    [string]$Subject = 'demo-developer',
    [string]$Scopes = 'documents.read documents.write operations.replay'
)
$ErrorActionPreference = 'Stop'
$tokenSecret = $env:Jwt__SigningKey
if (-not $tokenSecret -and (Test-Path -LiteralPath $EnvironmentPath)) {
    $secretLine = Get-Content -LiteralPath $EnvironmentPath | Where-Object { $_.StartsWith('JWT_SIGNING_KEY=') } | Select-Object -First 1
    if ($secretLine) { $tokenSecret = $secretLine.Substring('JWT_SIGNING_KEY='.Length) }
}
if (-not $tokenSecret) { throw 'Set Jwt__SigningKey or run New-DevEnvironment.ps1 first.' }
function ConvertTo-Base64Url([byte[]]$Bytes) { return [Convert]::ToBase64String($Bytes).TrimEnd('=').Replace('+','-').Replace('/','_') }
$tokenNow = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
$tokenHeader = @{ alg = 'HS256'; typ = 'JWT' } | ConvertTo-Json -Compress
$tokenPayload = @{ iss = 'fiscal-documents'; aud = 'fiscal-api'; sub = $Subject; scope = $Scopes; iat = $tokenNow; nbf = $tokenNow; exp = $tokenNow + 7200 } | ConvertTo-Json -Compress
$unsignedToken = (ConvertTo-Base64Url ([Text.Encoding]::UTF8.GetBytes($tokenHeader))) + '.' + (ConvertTo-Base64Url ([Text.Encoding]::UTF8.GetBytes($tokenPayload)))
$tokenSigner = [Security.Cryptography.HMACSHA256]::new([Text.Encoding]::UTF8.GetBytes($tokenSecret))
try { $tokenSignature = ConvertTo-Base64Url ($tokenSigner.ComputeHash([Text.Encoding]::UTF8.GetBytes($unsignedToken))) }
finally { $tokenSigner.Dispose() }
Write-Output ($unsignedToken + '.' + $tokenSignature)

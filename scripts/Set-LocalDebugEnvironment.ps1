$ErrorActionPreference = 'Stop'
$repositoryPath = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$environmentPath = Join-Path $repositoryPath '.env'
if (-not (Test-Path -LiteralPath $environmentPath)) {
    throw 'Run scripts/New-DevEnvironment.ps1 first.'
}
$localSecrets = @{}
foreach ($line in Get-Content -LiteralPath $environmentPath) {
    if ([string]::IsNullOrWhiteSpace($line) -or $line.TrimStart().StartsWith('#')) { continue }
    $parts = $line -split '=', 2
    if ($parts.Length -eq 2) { $localSecrets[$parts[0].Trim()] = $parts[1] }
}
foreach ($requiredKey in @('SQL_APP_PASSWORD', 'HMAC_KEY', 'JWT_SIGNING_KEY', 'RABBIT_PASSWORD')) {
    if ([string]::IsNullOrWhiteSpace($localSecrets[$requiredKey])) { throw "Missing $requiredKey in .env." }
}
$sqlConnection = [System.Data.Common.DbConnectionStringBuilder]::new()
$sqlConnection['Server'] = 'localhost,1433'
$sqlConnection['Database'] = 'FiscalDocuments'
$sqlConnection['User Id'] = 'fiscal_app'
$sqlConnection['Password'] = $localSecrets['SQL_APP_PASSWORD']
$sqlConnection['TrustServerCertificate'] = 'true'
$debugSettings = @{
    ConnectionStrings = @{ SqlServer = $sqlConnection.ConnectionString }
    Privacy = @{ HmacKey = $localSecrets['HMAC_KEY'] }
}
function Write-DebugSettings([string]$ProjectName, [hashtable]$Values) {
    $settingsPath = Join-Path $repositoryPath "src/$ProjectName/appsettings.Development.json"
    $settings = [pscustomobject]@{}
    if (Test-Path -LiteralPath $settingsPath) {
        $settings = Get-Content -Raw -LiteralPath $settingsPath | ConvertFrom-Json
    }
    foreach ($sectionName in $Values.Keys) {
        $section = $settings.PSObject.Properties[$sectionName]
        if ($null -eq $section -or $null -eq $section.Value) {
            $settings | Add-Member -NotePropertyName $sectionName -NotePropertyValue ([pscustomobject]@{}) -Force
        }
        foreach ($key in $Values[$sectionName].Keys) {
            $settings.$sectionName | Add-Member -NotePropertyName $key -NotePropertyValue $Values[$sectionName][$key] -Force
        }
    }
    [System.IO.File]::WriteAllText($settingsPath, ($settings | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
    Write-Output "Updated local debug settings for $ProjectName."
}
$apiSettings = $debugSettings.Clone()
$apiSettings['Jwt'] = @{ SigningKey = $localSecrets['JWT_SIGNING_KEY'] }
Write-DebugSettings 'FiscalDocuments.Api' $apiSettings
$workerSettings = $debugSettings.Clone()
$workerSettings['Rabbit'] = @{ Host = 'localhost'; Port = 5673; Username = 'fiscal_app'; Password = $localSecrets['RABBIT_PASSWORD'] }
Write-DebugSettings 'FiscalDocuments.Worker' $workerSettings

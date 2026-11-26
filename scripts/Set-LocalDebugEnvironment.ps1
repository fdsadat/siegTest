param(
    [ValidateSet('Local', 'Docker')][string]$Mode = 'Local',
    [string]$SqlConnectionString,
    [string]$RabbitHost = 'localhost',
    [ValidateRange(1, 65535)][int]$RabbitPort,
    [string]$RabbitUsername,
    [string]$RabbitPassword,
    [string]$RabbitVirtualHost = '/',
    [ValidatePattern('^[A-Za-z0-9_.-]{1,100}$')][string]$RabbitTopologyPrefix = 'fiscal'
)
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
$requiredKeys = @('HMAC_KEY', 'JWT_SIGNING_KEY')
if ($Mode -eq 'Docker') { $requiredKeys += @('SQL_APP_PASSWORD', 'RABBIT_PASSWORD') }
foreach ($requiredKey in $requiredKeys) {
    if ([string]::IsNullOrWhiteSpace($localSecrets[$requiredKey])) { throw "Missing $requiredKey in .env." }
}
if (-not $PSBoundParameters.ContainsKey('SqlConnectionString')) {
    $SqlConnectionString = 'Server=(localdb)\MSSQLLocalDB;Database=FiscalDocuments;Integrated Security=true;TrustServerCertificate=true'
    if ($Mode -eq 'Docker') {
        $sqlConnection = [System.Data.Common.DbConnectionStringBuilder]::new()
        $sqlConnection['Server'] = 'localhost,1433'
        $sqlConnection['Database'] = 'FiscalDocuments'
        $sqlConnection['User Id'] = 'fiscal_app'
        $sqlConnection['Password'] = $localSecrets['SQL_APP_PASSWORD']
        $sqlConnection['TrustServerCertificate'] = 'true'
        $SqlConnectionString = $sqlConnection.get_ConnectionString()
    }
}
if ([string]::IsNullOrWhiteSpace($SqlConnectionString)) { throw 'SqlConnectionString must not be empty.' }
if (-not $PSBoundParameters.ContainsKey('RabbitPort')) {
    $RabbitPort = 5672
    if ($Mode -eq 'Docker') { $RabbitPort = 5673 }
}
if (-not $PSBoundParameters.ContainsKey('RabbitUsername')) {
    $RabbitUsername = 'guest'
    if ($Mode -eq 'Docker') { $RabbitUsername = 'fiscal_app' }
}
if (-not $PSBoundParameters.ContainsKey('RabbitPassword')) {
    $RabbitPassword = 'guest'
    if ($Mode -eq 'Docker') { $RabbitPassword = $localSecrets['RABBIT_PASSWORD'] }
}
if ([string]::IsNullOrWhiteSpace($RabbitHost) -or [string]::IsNullOrWhiteSpace($RabbitUsername) -or
    [string]::IsNullOrWhiteSpace($RabbitPassword) -or [string]::IsNullOrWhiteSpace($RabbitVirtualHost)) {
    throw 'RabbitMQ host, username, password and virtual host must not be empty.'
}
$debugSettings = @{
    ConnectionStrings = @{ SqlServer = $SqlConnectionString }
    Privacy = @{ HmacKey = $localSecrets['HMAC_KEY'] }
    Database = @{ ApplyMigrations = $false; ApplicationPassword = '' }
    Rabbit = @{ Host = $RabbitHost; Port = $RabbitPort; Username = $RabbitUsername; Password = $RabbitPassword;
        VirtualHost = $RabbitVirtualHost; TopologyPrefix = $RabbitTopologyPrefix }
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
    Write-Output "Updated $Mode debug settings for $ProjectName."
}
$apiSettings = $debugSettings.Clone()
$apiSettings['Jwt'] = @{ SigningKey = $localSecrets['JWT_SIGNING_KEY'] }
Write-DebugSettings 'FiscalDocuments.Api' $apiSettings
$workerSettings = $debugSettings.Clone()
Write-DebugSettings 'FiscalDocuments.Worker' $workerSettings

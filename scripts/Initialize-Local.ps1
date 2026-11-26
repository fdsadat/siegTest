param(
    [string]$SqlConnectionString,
    [string]$RabbitHost = 'localhost',
    [ValidateRange(1, 65535)][int]$RabbitPort,
    [string]$RabbitUsername,
    [string]$RabbitPassword,
    [string]$RabbitVirtualHost = '/',
    [ValidatePattern('^[A-Za-z0-9_.-]{1,100}$')][string]$RabbitTopologyPrefix = 'fiscal'
)
$ErrorActionPreference = 'Stop'
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'Install the .NET 10 SDK, open a new terminal, and run Initialize-Local.ps1 again.'
}
$installedSdks = & dotnet --list-sdks
if ($LASTEXITCODE -ne 0 -or -not ($installedSdks -match '^10\.')) {
    throw 'The .NET 10 SDK is required. Install it and run Initialize-Local.ps1 again.'
}
$repositoryPath = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
& (Join-Path $PSScriptRoot 'New-DevEnvironment.ps1')
& (Join-Path $PSScriptRoot 'Set-LocalDebugEnvironment.ps1') -Mode Local @PSBoundParameters
Push-Location $repositoryPath
try {
    Write-Output 'Checking native SQL Server and RabbitMQ connections (no containers are started)...'
    & dotnet run --project src/FiscalDocuments.Api --launch-profile 'Local Debug' -- --check-dependencies
    if ($LASTEXITCODE -ne 0) { throw 'Local dependency check failed. Follow the SQL/RabbitMQ diagnostic above and rerun initialization.' }
    & dotnet run --no-build --project src/FiscalDocuments.Api --launch-profile 'Local Debug' -- --migrate
    if ($LASTEXITCODE -ne 0) { throw 'Database initialization failed. The SQL account must be allowed to create the database and apply migrations.' }
    Write-Output 'Local initialization complete. Start API and worker with the Local Debug profiles. Swagger: http://localhost:54392/swagger'
} finally { Pop-Location }

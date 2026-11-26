[CmdletBinding(SupportsShouldProcess = $true)]
param()

$ErrorActionPreference = 'Stop'
$repositoryPath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$workerSettingsPath = Join-Path $repositoryPath 'src/FiscalDocuments.Worker/appsettings.Development.json'
if (-not (Test-Path -LiteralPath $workerSettingsPath)) {
    throw 'Execute scripts/Initialize-Local.ps1 antes de limpar o ambiente local.'
}
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'Instale o SDK .NET 10 para executar a limpeza.'
}
if (-not $PSCmdlet.ShouldProcess('Banco SQL e filas RabbitMQ configurados para Development', 'Excluir todos os dados da aplicação, incluindo recibos, outbox, inbox e chaves idempotentes')) {
    return
}
foreach ($processName in @('FiscalDocuments.Api', 'FiscalDocuments.Worker')) {
    foreach ($runningProcess in @(Get-Process -Name $processName -ErrorAction SilentlyContinue)) {
        if ($runningProcess.Path -and $runningProcess.Path.StartsWith($repositoryPath + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Pare a API e o worker deste projeto antes de executar a limpeza.'
        }
    }
}
Write-Output 'A limpeza utiliza a configuração Development do worker e variáveis de ambiente. Não remove bancos de outros sistemas, migrations, configurações ou segredos.'
Push-Location $repositoryPath
try {
    & dotnet build src/FiscalDocuments.Worker -m:1 --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw 'Falha ao compilar o worker; nenhum dado foi apagado.' }
    & dotnet run --no-build --project src/FiscalDocuments.Worker --launch-profile 'Local Debug' -- --reset-data
    if ($LASTEXITCODE -ne 0) { throw 'A limpeza não foi concluída. Corrija a causa indicada e execute o script novamente.' }
} finally {
    Pop-Location
}

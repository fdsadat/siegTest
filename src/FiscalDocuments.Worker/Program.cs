using FiscalDocuments.Application;
using FiscalDocuments.Infrastructure;
using FiscalDocuments.Infrastructure.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args.Where(argument => argument is not "--reconcile-dlq" and not "--reset-data").ToArray());
builder.Services.AddApplication().AddInfrastructure(builder.Configuration);
var reconcile = args.Contains("--reconcile-dlq", StringComparer.Ordinal);
var reset = args.Contains("--reset-data", StringComparer.Ordinal);
if (reset && (reconcile || !builder.Environment.IsDevelopment()))
{
    throw new InvalidOperationException("A limpeza exige Development e não pode ser combinada com reconciliação.");
}

if (!reconcile && !reset)
{
    builder.Services.AddProcessingWorkers();
}

using var host = builder.Build();
_ = host.Services.GetRequiredService<IFingerprints>();
if (reset)
{
    var count = await LocalDataReset.ResetAsync(host.Services);
    Console.WriteLine($"Limpeza concluída: {count} registros removidos e filas da aplicação esvaziadas. Configurações e segredos preservados.");
}
else if (reconcile)
{
    var count = await DlqReconciler.ReconcileAsync(host.Services);
    Console.WriteLine($"Reconciled {count} deliveries. Use the authenticated reprocess endpoint for current failures.");
}
else
{
    await host.RunAsync();
}

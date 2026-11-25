using FiscalDocuments.Application;
using FiscalDocuments.Infrastructure;
using FiscalDocuments.Infrastructure.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args.Where(argument => argument != "--reconcile-dlq").ToArray());
builder.Services.AddApplication().AddInfrastructure(builder.Configuration);
var reconcile = args.Contains("--reconcile-dlq", StringComparer.Ordinal);
if (!reconcile) builder.Services.AddProcessingWorkers();
using var host = builder.Build();
_ = host.Services.GetRequiredService<IFingerprints>();
if (reconcile)
{
    var count = await DlqReconciler.ReconcileAsync(host.Services);
    Console.WriteLine($"Reconciled {count} deliveries. Use the authenticated reprocess endpoint for current failures.");
}
else await host.RunAsync();

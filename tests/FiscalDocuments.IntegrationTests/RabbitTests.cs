using System.Net;
using System.Text.Json;
using FiscalDocuments.Application;
using FiscalDocuments.Domain;
using FiscalDocuments.Infrastructure;
using FiscalDocuments.Infrastructure.Messaging;
using FiscalDocuments.Infrastructure.Persistence;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;
using Testcontainers.RabbitMq;

namespace FiscalDocuments.IntegrationTests;

[TestFixture, NonParallelizable, Category("Rabbit")]
public sealed class RabbitTests : SqlFixture
{
    private RabbitMqContainer rabbit = null!;
    private const string RabbitPassword = "test-only-Rabbit!0123456789";

    [OneTimeSetUp]
    public async Task StartRabbitAsync()
    {
        rabbit = new RabbitMqBuilder("rabbitmq:4.1-management").WithUsername("fiscal_tests").WithPassword(RabbitPassword).Build();
        await rabbit.StartAsync();
    }
    [OneTimeTearDown]
    public async Task StopRabbitAsync() { if (rabbit is not null) await rabbit.DisposeAsync(); }

    private async Task<ServiceProvider> WorkersAsync(IXmlDocuments? parser = null)
    {
        var connection = await InScopeAsync(services => Task.FromResult(services.GetRequiredService<FiscalDbContext>().Database.GetConnectionString()));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:SqlServer"] = connection,
            ["Privacy:HmacKey"] = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray()),
            ["Rabbit:Host"] = rabbit.Hostname, ["Rabbit:Port"] = rabbit.GetMappedPublicPort(5672).ToString(),
            ["Rabbit:Username"] = "fiscal_tests", ["Rabbit:Password"] = RabbitPassword
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging().AddApplication().AddInfrastructure(configuration);
        if (parser is not null) services.AddSingleton(parser);
        var provider = services.BuildServiceProvider();
        var broker = provider.GetRequiredService<RabbitBroker>();
        await using var channel = await broker.ConsumerChannelAsync(CancellationToken.None);
        foreach (var type in new[] { IntegrationEvent.Received, IntegrationEvent.Processed })
        {
            await channel.QueuePurgeAsync(RabbitBroker.Queue(type));
            await channel.QueuePurgeAsync(RabbitBroker.Queue(type) + ".dead");
            for (var i = 1; i <= 3; i++) await channel.QueuePurgeAsync(RabbitBroker.Queue(type) + ".retry." + i);
        }
        return provider;
    }

    private Task<Acceptance> ReceiveAsync() => InScopeAsync(s => s.GetRequiredService<ISender>().Send(new ReceiveDocument(Sample(), "rabbit-test")));
    private static async Task WaitAsync(Func<Task<bool>> condition, int seconds = 30)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        while (!await condition()) await Task.Delay(100, deadline.Token);
    }
    private Task<bool> HasStatusAsync(Guid id, ProcessingStatus status) => InScopeAsync(s =>
        s.GetRequiredService<FiscalDbContext>().Processing.AnyAsync(p => p.DocumentId == id && p.Status == status));

    [Test]
    public async Task Full_outbox_broker_consumer_pipeline_retries_transient_error_and_commits_once()
    {
        var faulty = new FaultingXml(Factory.Services.GetRequiredService<IXmlDocuments>(), false);
        await using var provider = await WorkersAsync(faulty);
        var broker = provider.GetRequiredService<RabbitBroker>();
        using var dispatcher = new OutboxDispatcher(provider.GetRequiredService<IServiceScopeFactory>(), broker, TimeProvider.System,
            provider.GetRequiredService<ILogger<OutboxDispatcher>>());
        using var consumers = new RabbitConsumers(provider.GetRequiredService<IServiceScopeFactory>(), broker,
            provider.GetRequiredService<ILogger<RabbitConsumers>>());
        var accepted = await ReceiveAsync();
        await dispatcher.StartAsync(CancellationToken.None);
        await consumers.StartAsync(CancellationToken.None);
        try
        {
            await WaitAsync(() => HasStatusAsync(accepted.DocumentId, ProcessingStatus.Completed));
            Assert.That(faulty.Calls, Is.EqualTo(2));
            var counts = await InScopeAsync(async s =>
            {
                var db = s.GetRequiredService<FiscalDbContext>();
                return (await db.Summaries.CountAsync(), await db.Inbox.CountAsync(), await db.Failures.CountAsync());
            });
            Assert.That(counts, Is.EqualTo((1, 2, 0)));
        }
        finally
        {
            await dispatcher.StopAsync(CancellationToken.None);
            await consumers.StopAsync(CancellationToken.None);
        }
    }

    [Test]
    public async Task Unconfirmed_or_unroutable_publish_stays_in_outbox_for_recovery()
    {
        await using var provider = await WorkersAsync();
        var broker = provider.GetRequiredService<RabbitBroker>();
        await ReceiveAsync();
        await InScopeAsync(async s =>
        {
            await s.GetRequiredService<FiscalDbContext>().Outbox.ExecuteUpdateAsync(set => set.SetProperty(o => o.EventType, "unroutable.test"));
            return true;
        });
        using var dispatcher = new OutboxDispatcher(provider.GetRequiredService<IServiceScopeFactory>(), broker, TimeProvider.System,
            provider.GetRequiredService<ILogger<OutboxDispatcher>>());
        await dispatcher.DispatchOneAsync(CancellationToken.None);
        var retained = await InScopeAsync(s => s.GetRequiredService<FiscalDbContext>().Outbox.AsNoTracking().SingleAsync());
        Assert.That(retained.PublishedAt, Is.Null);
        Assert.That(retained.Attempts, Is.EqualTo(1));
        await InScopeAsync(async s =>
        {
            await s.GetRequiredService<FiscalDbContext>().Outbox.ExecuteUpdateAsync(set => set
                .SetProperty(o => o.EventType, IntegrationEvent.Received).SetProperty(o => o.AvailableAt, DateTimeOffset.UtcNow));
            return true;
        });
        await dispatcher.DispatchOneAsync(CancellationToken.None);
        var published = await InScopeAsync(s => s.GetRequiredService<FiscalDbContext>().Outbox.AsNoTracking().SingleAsync());
        Assert.That(published.PublishedAt, Is.Not.Null);
    }

    [Test]
    public async Task Connection_loss_before_ack_redelivers_without_repeating_sql_effects()
    {
        await using var provider = await WorkersAsync();
        var broker = provider.GetRequiredService<RabbitBroker>();
        var accepted = await ReceiveAsync();
        var outbox = await InScopeAsync(s => s.GetRequiredService<FiscalDbContext>().Outbox.AsNoTracking().SingleAsync());
        await broker.PublishAsync(outbox.Payload, outbox.EventType, RabbitBroker.EventsExchange, 0, null, CancellationToken.None);
        await using (var channel = await broker.ConsumerChannelAsync(CancellationToken.None))
        {
            var delivery = await channel.BasicGetAsync(RabbitBroker.Queue(IntegrationEvent.Received), false);
            Assert.That(delivery, Is.Not.Null);
            var message = JsonSerializer.Deserialize<IntegrationEvent>(delivery!.Body.Span, JsonDefaults.Options)!;
            await InScopeAsync(s => s.GetRequiredService<EventProcessor>().ProcessAsync(message, CancellationToken.None));
            // Deliberately close without ACK after commit.
        }
        await using var redeliveryChannel = await broker.ConsumerChannelAsync(CancellationToken.None);
        BasicGetResult? redelivery = null;
        await WaitAsync(async () => (redelivery = await redeliveryChannel.BasicGetAsync(RabbitBroker.Queue(IntegrationEvent.Received), false)) is not null);
        Assert.That(redelivery!.Redelivered, Is.True);
        var replay = JsonSerializer.Deserialize<IntegrationEvent>(redelivery.Body.Span, JsonDefaults.Options)!;
        var applied = await InScopeAsync(s => s.GetRequiredService<EventProcessor>().ProcessAsync(replay, CancellationToken.None));
        Assert.That(applied, Is.False);
        await redeliveryChannel.BasicAckAsync(redelivery.DeliveryTag, false);
        Assert.That(await HasStatusAsync(accepted.DocumentId, ProcessingStatus.PendingSummary), Is.True);
        Assert.That(await InScopeAsync(s => s.GetRequiredService<FiscalDbContext>().Outbox.CountAsync()), Is.EqualTo(2));
    }

    [Test]
    public async Task Permanent_failure_is_recorded_dead_lettered_and_can_be_reconciled()
    {
        var faulty = new FaultingXml(Factory.Services.GetRequiredService<IXmlDocuments>(), true);
        await using var provider = await WorkersAsync(faulty);
        var broker = provider.GetRequiredService<RabbitBroker>();
        var accepted = await ReceiveAsync();
        using var dispatcher = new OutboxDispatcher(provider.GetRequiredService<IServiceScopeFactory>(), broker, TimeProvider.System,
            provider.GetRequiredService<ILogger<OutboxDispatcher>>());
        using var consumers = new RabbitConsumers(provider.GetRequiredService<IServiceScopeFactory>(), broker,
            provider.GetRequiredService<ILogger<RabbitConsumers>>());
        await dispatcher.StartAsync(CancellationToken.None);
        await consumers.StartAsync(CancellationToken.None);
        try
        {
            await WaitAsync(() => HasStatusAsync(accepted.DocumentId, ProcessingStatus.Failed));
            await using var channel = await broker.ConsumerChannelAsync(CancellationToken.None);
            await WaitAsync(async () => (await channel.QueueDeclarePassiveAsync(RabbitBroker.Queue(IntegrationEvent.Received) + ".dead")).MessageCount > 0);
            var reconciled = await DlqReconciler.ReconcileAsync(provider);
            Assert.That(reconciled, Is.EqualTo(1));
            Assert.That(await InScopeAsync(s => s.GetRequiredService<FiscalDbContext>().Failures.CountAsync()), Is.EqualTo(1));
        }
        finally
        {
            await dispatcher.StopAsync(CancellationToken.None);
            await consumers.StopAsync(CancellationToken.None);
        }
    }

    private sealed class FaultingXml(IXmlDocuments inner, bool permanent) : IXmlDocuments
    {
        public int Calls;
        public FiscalXml ValidateAndSanitize(ReadOnlyMemory<byte> bytes) => inner.ValidateAndSanitize(bytes);
        public ExtractedData ExtractSanitized(string xml)
        {
            var call = Interlocked.Increment(ref Calls);
            if (permanent) throw new PermanentProcessingException("simulated_permanent_failure");
            if (call == 1) throw new InvalidOperationException("simulated_transient_failure");
            return inner.ExtractSanitized(xml);
        }
    }
}

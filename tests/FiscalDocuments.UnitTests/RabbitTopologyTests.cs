using FiscalDocuments.Application;
using FiscalDocuments.Infrastructure.Messaging;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace FiscalDocuments.UnitTests;

[TestFixture]
public sealed class RabbitTopologyTests
{
    [Test]
    public async Task Default_topology_uses_one_processing_queue()
    {
        await using var broker = new RabbitBroker(Options.Create(new RabbitOptions()));
        Assert.Multiple(() =>
        {
            Assert.That(broker.EventsExchange, Is.EqualTo("fiscal.events"));
            Assert.That(broker.RetryExchange, Is.EqualTo("fiscal.retry"));
            Assert.That(broker.DeadExchange, Is.EqualTo("fiscal.dead"));
            Assert.That(broker.ProcessingQueue, Is.EqualTo("fiscal.processing"));
        });
    }

    [Test]
    public async Task Different_prefixes_isolate_processing_retry_and_dead_letter_names()
    {
        await using var first = new RabbitBroker(Options.Create(new RabbitOptions { TopologyPrefix = "test_first" }));
        await using var second = new RabbitBroker(Options.Create(new RabbitOptions { TopologyPrefix = "test_second" }));
        Assert.Multiple(() =>
        {
            Assert.That(first.EventsExchange, Is.Not.EqualTo(second.EventsExchange));
            Assert.That(first.RetryExchange, Is.Not.EqualTo(second.RetryExchange));
            Assert.That(first.DeadExchange, Is.Not.EqualTo(second.DeadExchange));
            Assert.That(first.ProcessingQueue + ".retry.1", Is.Not.EqualTo(second.ProcessingQueue + ".retry.1"));
            Assert.That(first.ProcessingQueue + ".dead", Is.Not.EqualTo(second.ProcessingQueue + ".dead"));
        });
    }

    [TestCase("")]
    [TestCase("bad prefix")]
    [TestCase("bad/prefix")]
    public void Invalid_prefix_is_rejected_before_connecting(string prefix)
    {
        Assert.Throws<InvalidOperationException>(() => new RabbitBroker(Options.Create(new RabbitOptions { TopologyPrefix = prefix })));
    }
}

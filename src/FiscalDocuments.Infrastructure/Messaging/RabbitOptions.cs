namespace FiscalDocuments.Infrastructure.Messaging;

public sealed class RabbitOptions
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 5672;
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string VirtualHost { get; set; } = "/";
    public string TopologyPrefix { get; set; } = "fiscal";
}

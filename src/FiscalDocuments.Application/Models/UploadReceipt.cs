namespace FiscalDocuments.Application;

public sealed class UploadReceipt
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DocumentId { get; set; }
    public string ContentHmac { get; set; } = "";
    public DateTimeOffset ReceivedAt { get; set; }
}

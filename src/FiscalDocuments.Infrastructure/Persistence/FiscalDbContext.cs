using FiscalDocuments.Application;
using FiscalDocuments.Domain;
using Microsoft.EntityFrameworkCore;

namespace FiscalDocuments.Infrastructure.Persistence;

public sealed class FiscalDbContext(DbContextOptions<FiscalDbContext> options) : DbContext(options)
{
    public DbSet<FiscalDocument> Documents => Set<FiscalDocument>();
    public DbSet<DocumentProcessing> Processing => Set<DocumentProcessing>();
    public DbSet<UploadReceipt> Receipts => Set<UploadReceipt>();
    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();
    public DbSet<InboxMessage> Inbox => Set<InboxMessage>();
    public DbSet<IdempotentOperation> Operations => Set<IdempotentOperation>();
    public DbSet<DocumentSummary> Summaries => Set<DocumentSummary>();
    public DbSet<ProcessingFailure> Failures => Set<ProcessingFailure>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        var doc = model.Entity<FiscalDocument>();
        doc.ToTable("Documents");
        doc.HasKey(d => d.Id);
        doc.Property(d => d.Id).ValueGeneratedNever();
        doc.Property(d => d.IdentityHmac).HasMaxLength(64).IsUnicode(false);
        doc.Property(d => d.ContentHmac).HasMaxLength(64).IsUnicode(false);
        doc.Property(d => d.IssuerHmac).HasMaxLength(64).IsUnicode(false);
        doc.Property(d => d.IssuerMasked).HasMaxLength(14).IsUnicode(false);
        doc.Property(d => d.State).HasMaxLength(2).IsUnicode(false);
        doc.Property(d => d.SanitizedXml).HasMaxLength(4096);
        doc.Property(d => d.RowVersion).IsRowVersion();
        doc.HasIndex(d => new { d.Type, d.IdentityHmac }).IsUnique().HasDatabaseName("UX_Documents_Identity");
        doc.HasIndex(d => new { d.IssuedAt, d.Id }).IsDescending(true, true);
        doc.HasIndex(d => new { d.IssuerHmac, d.IssuedAt, d.Id });
        doc.HasIndex(d => new { d.State, d.IssuedAt, d.Id });

        var processing = model.Entity<DocumentProcessing>();
        processing.ToTable("DocumentProcessing");
        processing.HasKey(p => p.DocumentId);
        processing.Property(p => p.DocumentId).ValueGeneratedNever();
        processing.Ignore(p => p.IsTerminal);
        processing.Property(p => p.TotalAmount).HasPrecision(18, 2);
        processing.Property(p => p.Competence).HasMaxLength(7).IsUnicode(false);
        processing.Property(p => p.ErrorCode).HasMaxLength(80).IsUnicode(false);
        processing.HasIndex(p => p.Status);
        processing.HasOne<FiscalDocument>().WithOne().HasForeignKey<DocumentProcessing>(p => p.DocumentId).OnDelete(DeleteBehavior.Restrict);

        var receipt = model.Entity<UploadReceipt>();
        receipt.ToTable("UploadReceipts");
        receipt.HasKey(r => r.Id);
        receipt.Property(r => r.Id).ValueGeneratedNever();
        receipt.Property(r => r.ContentHmac).HasMaxLength(64).IsUnicode(false);
        receipt.HasIndex(r => new { r.DocumentId, r.ContentHmac }).IsUnique().HasDatabaseName("UX_Receipts_Content");
        receipt.HasOne<FiscalDocument>().WithMany().HasForeignKey(r => r.DocumentId).OnDelete(DeleteBehavior.Restrict);

        var outbox = model.Entity<OutboxMessage>();
        outbox.ToTable("OutboxMessages");
        outbox.HasKey(o => o.Id);
        outbox.Property(o => o.Id).ValueGeneratedNever();
        outbox.Property(o => o.EventType).HasMaxLength(60).IsUnicode(false);
        outbox.Property(o => o.Payload).HasMaxLength(2048);
        outbox.HasIndex(o => new { o.ProcessingId, o.EventType }).IsUnique().HasDatabaseName("UX_Outbox_RunEvent");
        outbox.HasIndex(o => new { o.PublishedAt, o.AvailableAt, o.LeaseUntil });

        var inbox = model.Entity<InboxMessage>();
        inbox.ToTable("InboxMessages");
        inbox.HasKey(i => new { i.Consumer, i.EventId });
        inbox.Property(i => i.Consumer).HasMaxLength(30).IsUnicode(false);

        var operation = model.Entity<IdempotentOperation>();
        operation.ToTable("IdempotentOperations");
        operation.HasKey(o => o.Scope);
        operation.Property(o => o.Scope).HasMaxLength(64).IsUnicode(false);
        operation.Property(o => o.Fingerprint).HasMaxLength(64).IsUnicode(false);
        operation.Property(o => o.ResponseJson).HasMaxLength(2048);
        operation.Property(o => o.ETag).HasMaxLength(32).IsUnicode(false);
        operation.Property(o => o.Location).HasMaxLength(100).IsUnicode(false);
        operation.HasIndex(o => o.ExpiresAt);

        var summary = model.Entity<DocumentSummary>();
        summary.ToTable("DocumentSummaries");
        summary.HasKey(s => s.DocumentId);
        summary.Property(s => s.DocumentId).ValueGeneratedNever();
        summary.Property(s => s.Competence).HasMaxLength(7).IsUnicode(false);
        summary.HasOne<FiscalDocument>().WithOne().HasForeignKey<DocumentSummary>(s => s.DocumentId).OnDelete(DeleteBehavior.Restrict);

        var failure = model.Entity<ProcessingFailure>();
        failure.ToTable("ProcessingFailures");
        failure.HasKey(f => f.Id);
        failure.Property(f => f.Id).ValueGeneratedNever();
        failure.Property(f => f.Stage).HasMaxLength(60).IsUnicode(false);
        failure.Property(f => f.ErrorCode).HasMaxLength(80).IsUnicode(false);
        failure.HasIndex(f => new { f.ProcessingId, f.Stage }).IsUnique().HasDatabaseName("UX_Failures_RunStage");
        failure.HasOne<FiscalDocument>().WithMany().HasForeignKey(f => f.DocumentId).OnDelete(DeleteBehavior.Restrict);
    }
}

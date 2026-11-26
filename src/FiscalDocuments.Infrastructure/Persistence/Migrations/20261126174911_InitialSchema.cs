using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FiscalDocuments.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Documents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<int>(type: "int", nullable: false),
                    IdentityHmac = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    ContentHmac = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    IssuerMasked = table.Column<string>(type: "varchar(14)", unicode: false, maxLength: 14, nullable: false),
                    IssuerHmac = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    IssuedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    State = table.Column<string>(type: "varchar(2)", unicode: false, maxLength: 2, nullable: false),
                    SanitizedXml = table.Column<string>(type: "nvarchar(max)", maxLength: 4096, nullable: false),
                    ContentRevision = table.Column<int>(type: "int", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Documents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "IdempotentOperations",
                columns: table => new
                {
                    Scope = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    Fingerprint = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    StatusCode = table.Column<int>(type: "int", nullable: false),
                    ResponseJson = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: true),
                    ETag = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: true),
                    Location = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: true),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IdempotentOperations", x => x.Scope);
                });

            migrationBuilder.CreateTable(
                name: "InboxMessages",
                columns: table => new
                {
                    EventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InboxMessages", x => x.EventId);
                });

            migrationBuilder.CreateTable(
                name: "OutboxMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventType = table.Column<string>(type: "varchar(60)", unicode: false, maxLength: 60, nullable: false),
                    ProcessingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Payload = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    AvailableAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    PublishedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LeaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LeaseUntil = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Attempts = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxMessages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DocumentProcessing",
                columns: table => new
                {
                    DocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProcessingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContentRevision = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    ErrorCode = table.Column<string>(type: "varchar(80)", unicode: false, maxLength: 80, nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentProcessing", x => x.DocumentId);
                    table.ForeignKey(
                        name: "FK_DocumentProcessing_Documents_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "Documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DocumentSummaries",
                columns: table => new
                {
                    DocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProcessingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Competence = table.Column<string>(type: "varchar(7)", unicode: false, maxLength: 7, nullable: false),
                    TotalAmountInCents = table.Column<long>(type: "bigint", nullable: false),
                    ProcessedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentSummaries", x => x.DocumentId);
                    table.ForeignKey(
                        name: "FK_DocumentSummaries_Documents_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "Documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ProcessingFailures",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProcessingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContentRevision = table.Column<int>(type: "int", nullable: false),
                    ErrorCode = table.Column<string>(type: "varchar(80)", unicode: false, maxLength: 80, nullable: false),
                    FailedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProcessingFailures", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProcessingFailures_Documents_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "Documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "UploadReceipts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContentHmac = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UploadReceipts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UploadReceipts_Documents_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "Documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentProcessing_Status",
                table: "DocumentProcessing",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Documents_IssuedAt_Id",
                table: "Documents",
                columns: new[] { "IssuedAt", "Id" },
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "IX_Documents_IssuerHmac_IssuedAt_Id",
                table: "Documents",
                columns: new[] { "IssuerHmac", "IssuedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Documents_State_IssuedAt_Id",
                table: "Documents",
                columns: new[] { "State", "IssuedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "UX_Documents_Identity",
                table: "Documents",
                columns: new[] { "Type", "IdentityHmac" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IdempotentOperations_ExpiresAt",
                table: "IdempotentOperations",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_PublishedAt_AvailableAt_LeaseUntil",
                table: "OutboxMessages",
                columns: new[] { "PublishedAt", "AvailableAt", "LeaseUntil" });

            migrationBuilder.CreateIndex(
                name: "UX_Outbox_RunEvent",
                table: "OutboxMessages",
                columns: new[] { "ProcessingId", "EventType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProcessingFailures_DocumentId",
                table: "ProcessingFailures",
                column: "DocumentId");

            migrationBuilder.CreateIndex(
                name: "UX_Failures_Run",
                table: "ProcessingFailures",
                column: "ProcessingId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_Receipts_Content",
                table: "UploadReceipts",
                columns: new[] { "DocumentId", "ContentHmac" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DocumentProcessing");

            migrationBuilder.DropTable(
                name: "DocumentSummaries");

            migrationBuilder.DropTable(
                name: "IdempotentOperations");

            migrationBuilder.DropTable(
                name: "InboxMessages");

            migrationBuilder.DropTable(
                name: "OutboxMessages");

            migrationBuilder.DropTable(
                name: "ProcessingFailures");

            migrationBuilder.DropTable(
                name: "UploadReceipts");

            migrationBuilder.DropTable(
                name: "Documents");
        }
    }
}

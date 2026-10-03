using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Newrest.Pos.Client.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AccountMovements",
                columns: table => new
                {
                    IdempotencyKey = table.Column<Guid>(type: "TEXT", nullable: false),
                    AccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    BadgeNumber = table.Column<string>(type: "TEXT", nullable: true),
                    Type = table.Column<string>(type: "TEXT", nullable: false),
                    Amount = table.Column<string>(type: "TEXT", nullable: false),
                    OccurredAt = table.Column<long>(type: "INTEGER", nullable: false),
                    PaymentMethod = table.Column<string>(type: "TEXT", nullable: true),
                    TicketId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CashSessionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ConfirmedOnline = table.Column<bool>(type: "INTEGER", nullable: false),
                    Synced = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountMovements", x => x.IdempotencyKey);
                });

            migrationBuilder.CreateTable(
                name: "Cache",
                columns: table => new
                {
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    LookupKey = table.Column<string>(type: "TEXT", nullable: true),
                    Json = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Cache", x => new { x.Kind, x.Id });
                });

            migrationBuilder.CreateTable(
                name: "CashSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    OperatorId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OpenedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    BusinessDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    OpeningFloat = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    ClosedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    ZReportId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CashSessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Outbox",
                columns: table => new
                {
                    Position = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    LastAttemptAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", nullable: true),
                    SentAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Outbox", x => x.Position);
                });

            migrationBuilder.CreateTable(
                name: "RegisterStates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    LastSequence = table.Column<long>(type: "INTEGER", nullable: false),
                    LastHash = table.Column<string>(type: "TEXT", nullable: false),
                    LastZNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    OpenSessionId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RegisterStates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Settings",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Settings", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "Tickets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    Number = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    CashSessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    BusinessDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    IssuedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    DinerId = table.Column<Guid>(type: "TEXT", nullable: true),
                    BadgeNumber = table.Column<string>(type: "TEXT", nullable: true),
                    TotalAmount = table.Column<string>(type: "TEXT", nullable: false),
                    SubsidyAmount = table.Column<string>(type: "TEXT", nullable: false),
                    DinerShare = table.Column<string>(type: "TEXT", nullable: false),
                    CreditedTicketId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Hash = table.Column<string>(type: "TEXT", nullable: false),
                    Json = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tickets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ZReports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CashSessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ZNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    GeneratedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Json = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ZReports", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AccountMovements_AccountId_Synced",
                table: "AccountMovements",
                columns: new[] { "AccountId", "Synced" });

            migrationBuilder.CreateIndex(
                name: "IX_AccountMovements_CashSessionId",
                table: "AccountMovements",
                column: "CashSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_Cache_Kind_LookupKey",
                table: "Cache",
                columns: new[] { "Kind", "LookupKey" });

            migrationBuilder.CreateIndex(
                name: "IX_Outbox_ItemId",
                table: "Outbox",
                column: "ItemId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Outbox_Status",
                table: "Outbox",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_CashSessionId_Sequence",
                table: "Tickets",
                columns: new[] { "CashSessionId", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_DinerId_BusinessDate",
                table: "Tickets",
                columns: new[] { "DinerId", "BusinessDate" });

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_Sequence",
                table: "Tickets",
                column: "Sequence",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ZReports_ZNumber",
                table: "ZReports",
                column: "ZNumber",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AccountMovements");

            migrationBuilder.DropTable(
                name: "Cache");

            migrationBuilder.DropTable(
                name: "CashSessions");

            migrationBuilder.DropTable(
                name: "Outbox");

            migrationBuilder.DropTable(
                name: "RegisterStates");

            migrationBuilder.DropTable(
                name: "Settings");

            migrationBuilder.DropTable(
                name: "Tickets");

            migrationBuilder.DropTable(
                name: "ZReports");
        }
    }
}

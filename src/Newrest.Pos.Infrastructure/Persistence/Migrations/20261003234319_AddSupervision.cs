using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Newrest.Pos.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSupervision : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "IntegrityChecks",
                schema: "pos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RegisterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CheckedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    TicketsChecked = table.Column<int>(type: "int", nullable: false),
                    IsValid = table.Column<bool>(type: "bit", nullable: false),
                    IssuesSummary = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IntegrityChecks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IntegrityChecks_Registers_RegisterId",
                        column: x => x.RegisterId,
                        principalSchema: "pos",
                        principalTable: "Registers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RegisterHeartbeats",
                schema: "pos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AppVersion = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    PendingCount = table.Column<int>(type: "int", nullable: false),
                    OldestPendingAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    BlockingError = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    LocalLastSequence = table.Column<long>(type: "bigint", nullable: false),
                    OpenSessionSince = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastBackupAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ReportedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RegisterHeartbeats", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RegisterHeartbeats_Registers_Id",
                        column: x => x.Id,
                        principalSchema: "pos",
                        principalTable: "Registers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IntegrityChecks_RegisterId_CheckedAt",
                schema: "pos",
                table: "IntegrityChecks",
                columns: new[] { "RegisterId", "CheckedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IntegrityChecks",
                schema: "pos");

            migrationBuilder.DropTable(
                name: "RegisterHeartbeats",
                schema: "pos");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Newrest.Pos.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddArchives : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ArchiveRecords",
                schema: "pos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Year = table.Column<int>(type: "int", nullable: false),
                    Month = table.Column<int>(type: "int", nullable: false),
                    StoragePath = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Sha256 = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    KeyId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    TicketCount = table.Column<int>(type: "int", nullable: false),
                    CheckpointsJson = table.Column<string>(type: "nvarchar(max)", maxLength: -1, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArchiveRecords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ArchiveRecords_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalSchema: "pos",
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ArchiveRecords_CompanyId_Year_Month",
                schema: "pos",
                table: "ArchiveRecords",
                columns: new[] { "CompanyId", "Year", "Month" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ArchiveRecords",
                schema: "pos");
        }
    }
}

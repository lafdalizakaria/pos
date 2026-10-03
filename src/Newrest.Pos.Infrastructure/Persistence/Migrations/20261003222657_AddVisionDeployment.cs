using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Newrest.Pos.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddVisionDeployment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RegisterVisionStatuses",
                schema: "pos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    ModelVersion = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    ServiceReachable = table.Column<bool>(type: "bit", nullable: false),
                    ProviderReady = table.Column<bool>(type: "bit", nullable: false),
                    Error = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ReportedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RegisterVisionStatuses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RegisterVisionStatuses_Registers_Id",
                        column: x => x.Id,
                        principalSchema: "pos",
                        principalTable: "Registers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "VisionModels",
                schema: "pos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ClassesJson = table.Column<string>(type: "nvarchar(max)", maxLength: -1, nullable: false),
                    ClassCount = table.Column<int>(type: "int", nullable: false),
                    ImageSize = table.Column<int>(type: "int", nullable: false),
                    Sha256 = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    StoragePath = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Map50 = table.Column<decimal>(type: "decimal(9,4)", precision: 9, scale: 4, nullable: true),
                    ManifestJson = table.Column<string>(type: "nvarchar(max)", maxLength: -1, nullable: false),
                    UploadedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    PublishedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VisionModels", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SiteVisionSettings",
                schema: "pos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SiteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ModelId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LowThreshold = table.Column<decimal>(type: "decimal(9,4)", precision: 9, scale: 4, nullable: false),
                    HighThreshold = table.Column<decimal>(type: "decimal(9,4)", precision: 9, scale: 4, nullable: false),
                    HybridMinConfidence = table.Column<decimal>(type: "decimal(9,4)", precision: 9, scale: 4, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SiteVisionSettings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SiteVisionSettings_Sites_SiteId",
                        column: x => x.SiteId,
                        principalSchema: "pos",
                        principalTable: "Sites",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SiteVisionSettings_VisionModels_ModelId",
                        column: x => x.ModelId,
                        principalSchema: "pos",
                        principalTable: "VisionModels",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_SiteVisionSettings_ModelId",
                schema: "pos",
                table: "SiteVisionSettings",
                column: "ModelId");

            migrationBuilder.CreateIndex(
                name: "IX_SiteVisionSettings_RowVersion",
                schema: "pos",
                table: "SiteVisionSettings",
                column: "RowVersion");

            migrationBuilder.CreateIndex(
                name: "IX_SiteVisionSettings_SiteId",
                schema: "pos",
                table: "SiteVisionSettings",
                column: "SiteId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VisionModels_RowVersion",
                schema: "pos",
                table: "VisionModels",
                column: "RowVersion");

            migrationBuilder.CreateIndex(
                name: "IX_VisionModels_Version",
                schema: "pos",
                table: "VisionModels",
                column: "Version",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RegisterVisionStatuses",
                schema: "pos");

            migrationBuilder.DropTable(
                name: "SiteVisionSettings",
                schema: "pos");

            migrationBuilder.DropTable(
                name: "VisionModels",
                schema: "pos");
        }
    }
}

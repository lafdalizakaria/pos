using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Newrest.Pos.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddUserAccessScopesAndDeviceKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeviceKeyIssuedAt",
                schema: "pos",
                table: "Registers",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "UserAccessScopes",
                schema: "pos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SiteId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserAccessScopes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserAccessScopes_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalSchema: "pos",
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_UserAccessScopes_Sites_SiteId",
                        column: x => x.SiteId,
                        principalSchema: "pos",
                        principalTable: "Sites",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_UserAccessScopes_CompanyId",
                schema: "pos",
                table: "UserAccessScopes",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_UserAccessScopes_SiteId",
                schema: "pos",
                table: "UserAccessScopes",
                column: "SiteId");

            migrationBuilder.CreateIndex(
                name: "IX_UserAccessScopes_UserName_CompanyId_SiteId",
                schema: "pos",
                table: "UserAccessScopes",
                columns: new[] { "UserName", "CompanyId", "SiteId" },
                unique: true,
                filter: "[SiteId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UserAccessScopes",
                schema: "pos");

            migrationBuilder.DropColumn(
                name: "DeviceKeyIssuedAt",
                schema: "pos",
                table: "Registers");
        }
    }
}

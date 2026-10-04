using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Newrest.Pos.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddContractRevision : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Revision",
                schema: "pos",
                table: "Contracts",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Revision",
                schema: "pos",
                table: "Contracts");
        }
    }
}

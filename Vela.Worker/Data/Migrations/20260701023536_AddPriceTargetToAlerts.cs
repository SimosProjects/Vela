using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vela.Worker.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPriceTargetToAlerts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "PriceTarget",
                table: "alerts",
                type: "numeric",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PriceTarget",
                table: "alerts");
        }
    }
}

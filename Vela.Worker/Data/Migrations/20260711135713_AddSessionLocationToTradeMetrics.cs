using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vela.Worker.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSessionLocationToTradeMetrics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "session_location",
                table: "trade_metrics",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "session_location",
                table: "trade_metrics");
        }
    }
}

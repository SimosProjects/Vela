using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vela.Worker.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddExitAnalytics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "exit_latency_ms",
                table: "trade_metrics",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "exit_slippage_pct",
                table: "trade_metrics",
                type: "numeric",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "exit_latency_ms",
                table: "trade_metrics");

            migrationBuilder.DropColumn(
                name: "exit_slippage_pct",
                table: "trade_metrics");
        }
    }
}

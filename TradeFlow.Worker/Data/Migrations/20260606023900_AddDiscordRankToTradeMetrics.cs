using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradeFlow.Worker.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDiscordRankToTradeMetrics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "discord_rank",
                table: "trade_metrics",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "discord_rank",
                table: "trade_metrics");
        }
    }
}

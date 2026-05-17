using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradeFlow.Worker.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTradeMetrics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "trade_metrics",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    alert_id = table.Column<string>(type: "text", nullable: true),
                    trader_name = table.Column<string>(type: "text", nullable: true),
                    symbol = table.Column<string>(type: "text", nullable: true),
                    trade_type = table.Column<string>(type: "text", nullable: true),
                    direction = table.Column<string>(type: "text", nullable: true),
                    options_contract = table.Column<string>(type: "text", nullable: true),
                    is_average = table.Column<bool>(type: "boolean", nullable: false),
                    alert_received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    order_submitted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    order_filled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    latency_ms = table.Column<int>(type: "integer", nullable: false),
                    alerted_price = table.Column<decimal>(type: "numeric", nullable: false),
                    fill_price = table.Column<decimal>(type: "numeric", nullable: false),
                    slippage_pct = table.Column<decimal>(type: "numeric", nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    entry_amount = table.Column<decimal>(type: "numeric", nullable: false),
                    stop_price = table.Column<decimal>(type: "numeric", nullable: false),
                    target_price = table.Column<decimal>(type: "numeric", nullable: false),
                    account_balance_at_entry = table.Column<decimal>(type: "numeric", nullable: false),
                    open_positions_value_at_entry = table.Column<decimal>(type: "numeric", nullable: false),
                    exposure_pct = table.Column<decimal>(type: "numeric", nullable: false),
                    exit_price = table.Column<decimal>(type: "numeric", nullable: true),
                    exit_amount = table.Column<decimal>(type: "numeric", nullable: true),
                    outcome = table.Column<string>(type: "text", nullable: true),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    pnl = table.Column<decimal>(type: "numeric", nullable: true),
                    pnl_pct = table.Column<decimal>(type: "numeric", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_trade_metrics", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "idx_trade_metrics_closed_at",
                table: "trade_metrics",
                column: "closed_at");

            migrationBuilder.CreateIndex(
                name: "idx_trade_metrics_received_at",
                table: "trade_metrics",
                column: "alert_received_at");

            migrationBuilder.CreateIndex(
                name: "idx_trade_metrics_symbol",
                table: "trade_metrics",
                column: "symbol");

            migrationBuilder.CreateIndex(
                name: "idx_trade_metrics_trader",
                table: "trade_metrics",
                column: "trader_name");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "trade_metrics");
        }
    }
}

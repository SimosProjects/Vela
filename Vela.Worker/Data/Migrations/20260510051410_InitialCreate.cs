using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vela.Worker.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "alerts",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    user_id = table.Column<string>(type: "text", nullable: true),
                    user_name = table.Column<string>(type: "text", nullable: true),
                    xscore = table.Column<double>(type: "double precision", nullable: true),
                    symbol = table.Column<string>(type: "text", nullable: true),
                    type = table.Column<string>(type: "text", nullable: true),
                    direction = table.Column<string>(type: "text", nullable: true),
                    strike = table.Column<decimal>(type: "numeric", nullable: true),
                    expiration = table.Column<string>(type: "text", nullable: true),
                    options_contract_symbol = table.Column<string>(type: "text", nullable: true),
                    contract_description = table.Column<string>(type: "text", nullable: true),
                    side = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "text", nullable: true),
                    result = table.Column<string>(type: "text", nullable: true),
                    actual_price_at_time_of_alert = table.Column<decimal>(type: "numeric", nullable: true),
                    price_paid = table.Column<decimal>(type: "numeric", nullable: true),
                    price_at_exit = table.Column<decimal>(type: "numeric", nullable: true),
                    last_checked_price = table.Column<decimal>(type: "numeric", nullable: true),
                    last_known_percent_profit = table.Column<decimal>(type: "numeric", nullable: true),
                    risk = table.Column<string>(type: "text", nullable: true),
                    is_profitable_trade = table.Column<bool>(type: "boolean", nullable: true),
                    can_average = table.Column<bool>(type: "boolean", nullable: true),
                    time_of_entry_alert = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    time_of_full_exit_alert = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    formatted_length = table.Column<string>(type: "text", nullable: true),
                    is_swing = table.Column<bool>(type: "boolean", nullable: true),
                    is_bullish = table.Column<bool>(type: "boolean", nullable: true),
                    is_short = table.Column<bool>(type: "boolean", nullable: true),
                    strategy = table.Column<string>(type: "text", nullable: true),
                    original_message = table.Column<string>(type: "text", nullable: true),
                    original_exit_message = table.Column<string>(type: "text", nullable: true),
                    ingested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    risk_approved = table.Column<bool>(type: "boolean", nullable: false),
                    risk_reason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_alerts", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "idx_alerts_side_time",
                table: "alerts",
                columns: new[] { "side", "time_of_entry_alert" });

            migrationBuilder.CreateIndex(
                name: "idx_alerts_symbol",
                table: "alerts",
                column: "symbol");

            migrationBuilder.CreateIndex(
                name: "idx_alerts_time_of_entry",
                table: "alerts",
                column: "time_of_entry_alert");

            migrationBuilder.CreateIndex(
                name: "idx_alerts_username",
                table: "alerts",
                column: "user_name");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "alerts");
        }
    }
}

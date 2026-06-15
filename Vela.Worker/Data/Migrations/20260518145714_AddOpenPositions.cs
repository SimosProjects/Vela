using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vela.Worker.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddOpenPositions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "open_positions",
                columns: table => new
                {
                    order_id = table.Column<string>(type: "text", nullable: false),
                    stop_order_id = table.Column<string>(type: "text", nullable: true),
                    target_order_id = table.Column<string>(type: "text", nullable: true),
                    alert_id = table.Column<string>(type: "text", nullable: false),
                    user_name = table.Column<string>(type: "text", nullable: false),
                    symbol = table.Column<string>(type: "text", nullable: false),
                    trade_type = table.Column<string>(type: "text", nullable: false),
                    options_contract = table.Column<string>(type: "text", nullable: true),
                    direction = table.Column<string>(type: "text", nullable: true),
                    strike = table.Column<decimal>(type: "numeric", nullable: true),
                    expiration = table.Column<string>(type: "text", nullable: true),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    entry_price = table.Column<decimal>(type: "numeric", nullable: false),
                    entry_amount = table.Column<decimal>(type: "numeric", nullable: false),
                    stop_price = table.Column<decimal>(type: "numeric", nullable: false),
                    target_price = table.Column<decimal>(type: "numeric", nullable: false),
                    opened_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    is_average = table.Column<bool>(type: "boolean", nullable: false),
                    has_averaged = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_open_positions", x => x.order_id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "open_positions");
        }
    }
}

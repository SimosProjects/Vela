using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vela.Worker.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSystemState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "system_state",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    regime_tier = table.Column<string>(type: "text", nullable: false),
                    sizing_multiplier = table.Column<decimal>(type: "numeric", nullable: false),
                    block_calls = table.Column<bool>(type: "boolean", nullable: false),
                    spy_price = table.Column<decimal>(type: "numeric", nullable: true),
                    ma20 = table.Column<decimal>(type: "numeric", nullable: true),
                    ma50 = table.Column<decimal>(type: "numeric", nullable: true),
                    ma200 = table.Column<decimal>(type: "numeric", nullable: true),
                    vix = table.Column<decimal>(type: "numeric", nullable: true),
                    vix_delta = table.Column<decimal>(type: "numeric", nullable: true),
                    chop_score = table.Column<int>(type: "integer", nullable: true),
                    is_paused = table.Column<bool>(type: "boolean", nullable: false),
                    ibkr_connected = table.Column<bool>(type: "boolean", nullable: false),
                    worker_heartbeat = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    account_balance = table.Column<decimal>(type: "numeric", nullable: true),
                    open_value = table.Column<decimal>(type: "numeric", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_system_state", x => x.id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "system_state");
        }
    }
}

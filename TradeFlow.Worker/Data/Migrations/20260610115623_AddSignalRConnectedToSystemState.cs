using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradeFlow.Worker.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSignalRConnectedToSystemState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "SignalRConnected",
                table: "system_state",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SignalRConnected",
                table: "system_state");
        }
    }
}

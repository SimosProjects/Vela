using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradeFlow.Worker.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBlockHighLottoOverrideToSystemState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "BlockHighOverride",
                table: "system_state",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "BlockLottoOverride",
                table: "system_state",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BlockHighOverride",
                table: "system_state");

            migrationBuilder.DropColumn(
                name: "BlockLottoOverride",
                table: "system_state");
        }
    }
}

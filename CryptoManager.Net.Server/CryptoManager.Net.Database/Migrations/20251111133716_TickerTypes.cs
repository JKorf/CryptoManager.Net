using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CryptoManager.Net.Database.Migrations
{
    /// <inheritdoc />
    public partial class TickerTypes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "TickerType",
                table: "Symbols",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "TickerType",
                table: "ExchangeAssets",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TickerType",
                table: "Symbols");

            migrationBuilder.DropColumn(
                name: "TickerType",
                table: "ExchangeAssets");
        }
    }
}

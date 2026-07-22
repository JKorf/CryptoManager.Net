using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CryptoManager.Net.Database.Migrations
{
    /// <inheritdoc />
    public partial class Assettypes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "BaseAssetSubType",
                table: "Symbols",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "QuoteAssetSubType",
                table: "Symbols",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AssetSubType",
                table: "ExchangeAssets",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AssetSubType",
                table: "Assets",
                type: "int",
                nullable: true);

            migrationBuilder.Sql("UPDATE Symbols SET BaseAssetType = 1 WHERE BaseAssetType = 2");
            migrationBuilder.Sql("UPDATE Symbols SET BaseAssetType = 2 WHERE BaseAssetType = 0");
            migrationBuilder.Sql("UPDATE Symbols SET BaseAssetType = 1, BaseAssetSubType = 0 WHERE BaseAssetType = 1");
            migrationBuilder.Sql("UPDATE Symbols SET QuoteAssetType = 1 WHERE QuoteAssetType = 2");
            migrationBuilder.Sql("UPDATE Symbols SET QuoteAssetType = 2 WHERE QuoteAssetType = 0");
            migrationBuilder.Sql("UPDATE Symbols SET QuoteAssetType = 1, BaseAssetSubType = 0 WHERE QuoteAssetType = 1");

            migrationBuilder.Sql("UPDATE ExchangeAssets SET AssetType = 1 WHERE AssetType = 2");
            migrationBuilder.Sql("UPDATE ExchangeAssets SET AssetType = 2 WHERE AssetType = 0");
            migrationBuilder.Sql("UPDATE ExchangeAssets SET AssetType = 1, AssetSubType = 0 WHERE AssetType = 1");

            migrationBuilder.Sql("UPDATE Assets SET AssetType = 1 WHERE AssetType = 2");
            migrationBuilder.Sql("UPDATE Assets SET AssetType = 2 WHERE AssetType = 0");
            migrationBuilder.Sql("UPDATE Assets SET AssetType = 1, AssetSubType = 0 WHERE AssetType = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BaseAssetSubType",
                table: "Symbols");

            migrationBuilder.DropColumn(
                name: "QuoteAssetSubType",
                table: "Symbols");

            migrationBuilder.DropColumn(
                name: "AssetSubType",
                table: "ExchangeAssets");

            migrationBuilder.DropColumn(
                name: "AssetSubType",
                table: "Assets");
        }
    }
}

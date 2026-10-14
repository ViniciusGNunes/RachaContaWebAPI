using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RachaConta.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddGroupAccessTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AccessToken",
                table: "Groups",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "AdminToken",
                table: "Groups",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            // Existing groups get random tokens (128 bits of randomblob) so they are not left open or locked.
            migrationBuilder.Sql(
                "UPDATE \"Groups\" SET \"AccessToken\" = lower(hex(randomblob(16))), \"AdminToken\" = lower(hex(randomblob(16)));");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AccessToken",
                table: "Groups");

            migrationBuilder.DropColumn(
                name: "AdminToken",
                table: "Groups");
        }
    }
}

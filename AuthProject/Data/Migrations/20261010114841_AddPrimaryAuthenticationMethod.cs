using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthProject.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPrimaryAuthenticationMethod : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PrimaryMethod",
                table: "AuthSessions",
                type: "varchar(16)",
                unicode: false,
                maxLength: 16,
                nullable: false,
                defaultValue: "Password");

            migrationBuilder.AddColumn<string>(
                name: "PrimaryMethod",
                table: "AuthChallenges",
                type: "varchar(16)",
                unicode: false,
                maxLength: 16,
                nullable: false,
                defaultValue: "Password");

            migrationBuilder.AddCheckConstraint(
                name: "CK_AuthSessions_PrimaryMethod",
                table: "AuthSessions",
                sql: "[PrimaryMethod] IN ('Password', 'Google')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_AuthChallenges_PrimaryMethod",
                table: "AuthChallenges",
                sql: "[PrimaryMethod] IN ('Password', 'Google')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_AuthSessions_PrimaryMethod",
                table: "AuthSessions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_AuthChallenges_PrimaryMethod",
                table: "AuthChallenges");

            migrationBuilder.DropColumn(
                name: "PrimaryMethod",
                table: "AuthSessions");

            migrationBuilder.DropColumn(
                name: "PrimaryMethod",
                table: "AuthChallenges");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthProject.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLockoutFailureWindow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "FailedLoginWindowStart",
                table: "Users",
                type: "datetimeoffset",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FailedLoginWindowStart",
                table: "Users");
        }
    }
}

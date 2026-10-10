using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthProject.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditDeliveryState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeadLetteredAt",
                table: "OutboxMessages",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastErrorCode",
                table: "OutboxMessages",
                type: "varchar(32)",
                unicode: false,
                maxLength: 32,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeadLetteredAt",
                table: "OutboxMessages");

            migrationBuilder.DropColumn(
                name: "LastErrorCode",
                table: "OutboxMessages");
        }
    }
}

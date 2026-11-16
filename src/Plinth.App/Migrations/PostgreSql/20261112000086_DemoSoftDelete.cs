using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plinth.App.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class DemoSoftDelete : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAtUtc",
                table: "demo_records",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDeleted",
                table: "demo_records",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) =>
            throw new NotSupportedException("DemoSoftDelete 仅支持向前迁移，请恢复兼容版本的备份。");
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plinth.App.Migrations.MySql
{
    /// <inheritdoc />
    public partial class DemoTypedColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Active",
                table: "demo_records",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "DueDate",
                table: "demo_records",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Priority",
                table: "demo_records",
                type: "varchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Normal")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<int>(
                name: "Quantity",
                table: "demo_records",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) =>
            throw new NotSupportedException("DemoTypedColumns 仅支持向前迁移，请恢复兼容版本的备份。");
    }
}

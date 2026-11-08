using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Plinth.App.Data;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Plinth.App.Migrations.MySql;

[DbContext(typeof(MySqlMigrationContext))]
[Migration("20261104000000_MySqlInitialSchema")]
public sealed class MySqlInitialSchema : Migration
{
    public const string MigrationId = "20261104000000_MySqlInitialSchema";

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "demo_records",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                Code = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: false).Annotation("MySql:CharSet", "utf8mb4"),
                CodeKey = table.Column<string>(type: "varchar(300)", maxLength: 300, nullable: false).Annotation("MySql:CharSet", "utf8mb4"),
                Name = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false).Annotation("MySql:CharSet", "utf8mb4"),
                Remark = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: false).Annotation("MySql:CharSet", "utf8mb4"),
                Version = table.Column<int>(type: "int", nullable: false),
                CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                CreatedBy = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false).Annotation("MySql:CharSet", "utf8mb4"),
                UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                UpdatedBy = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false).Annotation("MySql:CharSet", "utf8mb4"),
            },
            constraints: table => table.PrimaryKey("PK_demo_records", x => x.Id)).Annotation("MySql:CharSet", "utf8mb4");
        migrationBuilder.CreateIndex("IX_demo_records_CodeKey", "demo_records", "CodeKey", unique: true);
        migrationBuilder.CreateTable(
            name: "notes_entries",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                Code = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: false).Annotation("MySql:CharSet", "utf8mb4"),
                CodeKey = table.Column<string>(type: "varchar(300)", maxLength: 300, nullable: false).Annotation("MySql:CharSet", "utf8mb4"),
                Text = table.Column<string>(type: "varchar(2000)", maxLength: 2000, nullable: false).Annotation("MySql:CharSet", "utf8mb4"),
                Version = table.Column<int>(type: "int", nullable: false),
                CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                CreatedBy = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false).Annotation("MySql:CharSet", "utf8mb4"),
                UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                UpdatedBy = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false).Annotation("MySql:CharSet", "utf8mb4"),
            },
            constraints: table => table.PrimaryKey("PK_notes_entries", x => x.Id)).Annotation("MySql:CharSet", "utf8mb4");
        migrationBuilder.CreateIndex("IX_notes_entries_CodeKey", "notes_entries", "CodeKey", unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("服务器数据库仅支持向前迁移，请恢复兼容版本的备份。");

    protected override void BuildTargetModel(ModelBuilder modelBuilder)
    {
        modelBuilder.HasAnnotation("ProductVersion", "9.0.22");
        modelBuilder.HasAnnotation("Relational:MaxIdentifierLength", 64);
        modelBuilder.HasAnnotation("MySql:CharSet", "utf8mb4");
        modelBuilder.HasAnnotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn);
        modelBuilder.Entity("Plinth.App.Modules.Demo.Data.DemoRecord", entity =>
        {
            entity.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("char(36)").UseCollation("ascii_general_ci");
            entity.Property<string>("Code").IsRequired().HasMaxLength(50).HasColumnType("varchar(50)").HasAnnotation("MySql:CharSet", "utf8mb4");
            entity.Property<string>("CodeKey").IsRequired().HasMaxLength(300).HasColumnType("varchar(300)").HasAnnotation("MySql:CharSet", "utf8mb4");
            entity.Property<string>("Name").IsRequired().HasMaxLength(200).HasColumnType("varchar(200)").HasAnnotation("MySql:CharSet", "utf8mb4");
            entity.Property<string>("Remark").IsRequired().HasMaxLength(500).HasColumnType("varchar(500)").HasAnnotation("MySql:CharSet", "utf8mb4");
            entity.Property<int>("Version").IsConcurrencyToken().HasColumnType("int");
            entity.Property<DateTime>("CreatedAtUtc").HasColumnType("datetime(6)");
            entity.Property<string>("CreatedBy").IsRequired().HasMaxLength(100).HasColumnType("varchar(100)").HasAnnotation("MySql:CharSet", "utf8mb4");
            entity.Property<DateTime>("UpdatedAtUtc").HasColumnType("datetime(6)");
            entity.Property<string>("UpdatedBy").IsRequired().HasMaxLength(100).HasColumnType("varchar(100)").HasAnnotation("MySql:CharSet", "utf8mb4");
            entity.HasKey("Id");
            entity.HasIndex("CodeKey").IsUnique();
            entity.ToTable("demo_records");
            entity.HasAnnotation("MySql:CharSet", "utf8mb4");
        });
        modelBuilder.Entity("Plinth.App.Modules.Notes.Data.Note", entity =>
        {
            entity.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("char(36)").UseCollation("ascii_general_ci");
            entity.Property<string>("Code").IsRequired().HasMaxLength(50).HasColumnType("varchar(50)").HasAnnotation("MySql:CharSet", "utf8mb4");
            entity.Property<string>("CodeKey").IsRequired().HasMaxLength(300).HasColumnType("varchar(300)").HasAnnotation("MySql:CharSet", "utf8mb4");
            entity.Property<string>("Text").IsRequired().HasMaxLength(2000).HasColumnType("varchar(2000)").HasAnnotation("MySql:CharSet", "utf8mb4");
            entity.Property<int>("Version").IsConcurrencyToken().HasColumnType("int");
            entity.Property<DateTime>("CreatedAtUtc").HasColumnType("datetime(6)");
            entity.Property<string>("CreatedBy").IsRequired().HasMaxLength(100).HasColumnType("varchar(100)").HasAnnotation("MySql:CharSet", "utf8mb4");
            entity.Property<DateTime>("UpdatedAtUtc").HasColumnType("datetime(6)");
            entity.Property<string>("UpdatedBy").IsRequired().HasMaxLength(100).HasColumnType("varchar(100)").HasAnnotation("MySql:CharSet", "utf8mb4");
            entity.HasKey("Id");
            entity.HasIndex("CodeKey").IsUnique();
            entity.ToTable("notes_entries");
            entity.HasAnnotation("MySql:CharSet", "utf8mb4");
        });
    }
}

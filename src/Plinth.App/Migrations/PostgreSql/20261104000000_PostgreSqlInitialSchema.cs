using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Plinth.App.Data;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Plinth.App.Migrations.PostgreSql;

[DbContext(typeof(PostgreSqlMigrationContext))]
[Migration("20261104000000_PostgreSqlInitialSchema")]
public sealed class PostgreSqlInitialSchema : Migration
{
    public const string MigrationId = "20261104000000_PostgreSqlInitialSchema";

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "demo_records",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                CodeKey = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                Remark = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                Version = table.Column<int>(type: "integer", nullable: false),
                CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                CreatedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                UpdatedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_demo_records", x => x.Id));
        migrationBuilder.CreateIndex("IX_demo_records_CodeKey", "demo_records", "CodeKey", unique: true);
        migrationBuilder.CreateTable(
            name: "notes_entries",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                CodeKey = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                Text = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                Version = table.Column<int>(type: "integer", nullable: false),
                CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                CreatedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                UpdatedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_notes_entries", x => x.Id));
        migrationBuilder.CreateIndex("IX_notes_entries_CodeKey", "notes_entries", "CodeKey", unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("服务器数据库仅支持向前迁移，请恢复兼容版本的备份。");

    protected override void BuildTargetModel(ModelBuilder modelBuilder)
    {
        modelBuilder.HasAnnotation("ProductVersion", "9.0.22");
        modelBuilder.HasAnnotation("Relational:MaxIdentifierLength", 63);
        NpgsqlModelBuilderExtensions.UseIdentityByDefaultColumns(modelBuilder);
        modelBuilder.Entity("Plinth.App.Modules.Demo.Data.DemoRecord", entity =>
        {
            entity.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("uuid");
            entity.Property<string>("Code").IsRequired().HasMaxLength(50).HasColumnType("character varying(50)");
            entity.Property<string>("CodeKey").IsRequired().HasMaxLength(300).HasColumnType("character varying(300)");
            entity.Property<string>("Name").IsRequired().HasMaxLength(200).HasColumnType("character varying(200)");
            entity.Property<string>("Remark").IsRequired().HasMaxLength(500).HasColumnType("character varying(500)");
            entity.Property<int>("Version").IsConcurrencyToken().HasColumnType("integer");
            entity.Property<DateTime>("CreatedAtUtc").HasColumnType("timestamp with time zone");
            entity.Property<string>("CreatedBy").IsRequired().HasMaxLength(100).HasColumnType("character varying(100)");
            entity.Property<DateTime>("UpdatedAtUtc").HasColumnType("timestamp with time zone");
            entity.Property<string>("UpdatedBy").IsRequired().HasMaxLength(100).HasColumnType("character varying(100)");
            entity.HasKey("Id");
            entity.HasIndex("CodeKey").IsUnique();
            entity.ToTable("demo_records");
        });
        modelBuilder.Entity("Plinth.App.Modules.Notes.Data.Note", entity =>
        {
            entity.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("uuid");
            entity.Property<string>("Code").IsRequired().HasMaxLength(50).HasColumnType("character varying(50)");
            entity.Property<string>("CodeKey").IsRequired().HasMaxLength(300).HasColumnType("character varying(300)");
            entity.Property<string>("Text").IsRequired().HasMaxLength(2000).HasColumnType("character varying(2000)");
            entity.Property<int>("Version").IsConcurrencyToken().HasColumnType("integer");
            entity.Property<DateTime>("CreatedAtUtc").HasColumnType("timestamp with time zone");
            entity.Property<string>("CreatedBy").IsRequired().HasMaxLength(100).HasColumnType("character varying(100)");
            entity.Property<DateTime>("UpdatedAtUtc").HasColumnType("timestamp with time zone");
            entity.Property<string>("UpdatedBy").IsRequired().HasMaxLength(100).HasColumnType("character varying(100)");
            entity.HasKey("Id");
            entity.HasIndex("CodeKey").IsUnique();
            entity.ToTable("notes_entries");
        });
    }
}

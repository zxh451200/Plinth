using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Plinth.App.Data;

// These contexts exist only for EF migrations. Runtime CRUD keeps AppDbContext and its audit hooks.
// EF discovers migrations/snapshots by DbContextAttribute; no custom migrations assembly is needed.
public sealed class PostgreSqlMigrationContext(DbContextOptions<PostgreSqlMigrationContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder) => AppDbContext.ConfigureModel(modelBuilder, Database.ProviderName);
}

public sealed class MySqlMigrationContext(DbContextOptions<MySqlMigrationContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder) => AppDbContext.ConfigureModel(modelBuilder, Database.ProviderName);
}

// Offline design-time factories never read deployment secrets or connect to a database.
// dotnet ef migrations add NAME --context PostgreSqlMigrationContext --output-dir Migrations/PostgreSql
public sealed class PostgreSqlMigrationContextFactory : IDesignTimeDbContextFactory<PostgreSqlMigrationContext>
{
    public PostgreSqlMigrationContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<PostgreSqlMigrationContext>()
            .UseNpgsql("Host=localhost;Database=plinth_design;Username=plinth").Options);
}

// dotnet ef migrations add NAME --context MySqlMigrationContext --output-dir Migrations/MySql
public sealed class MySqlMigrationContextFactory : IDesignTimeDbContextFactory<MySqlMigrationContext>
{
    public MySqlMigrationContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<MySqlMigrationContext>()
            .UseMySql("Server=localhost;Database=plinth_design;User=plinth",
                new MySqlServerVersion(new System.Version(8, 0, 12))).Options);
}

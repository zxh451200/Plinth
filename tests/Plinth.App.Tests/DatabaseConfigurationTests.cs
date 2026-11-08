using System;
using System.Linq;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using MySqlConnector;
using Npgsql;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Plinth.App.Data;
using Plinth.App.Services;
using Xunit;

namespace Plinth.App.Tests;

public sealed class DatabaseConfigurationTests
{
    [Fact]
    public void Defaults_to_sqlite()
    {
        var configuration = DatabaseConfiguration.Resolve(new DatabaseOptions(), null, null, null);

        Assert.True(configuration.IsSqlite);
        Assert.False(configuration.IsServer);
        Assert.Empty(configuration.ConnectionString);
    }

    [Theory]
    [InlineData("sqlite3")]
    [InlineData("")]
    public void Unknown_provider_is_rejected(string provider)
    {
        Assert.Throws<InvalidDataException>(() =>
            DatabaseConfiguration.Resolve(new DatabaseOptions { Provider = provider }, null, null, null));
    }

    [Fact]
    public void Server_provider_requires_a_connection_string()
    {
        Assert.Throws<InvalidDataException>(() =>
            DatabaseConfiguration.Resolve(new DatabaseOptions { Provider = DatabaseConfiguration.PostgreSql }, null, null, null));
    }

    [Fact]
    public void User_file_overrides_deployment_and_environment_wins()
    {
        var deployment = new DatabaseOptions { Provider = DatabaseConfiguration.Sqlite };
        var user = """{"provider":"MySql","connectionString":"Server=localhost;Database=plinth;"}""";

        var fromFile = DatabaseConfiguration.Resolve(deployment, user, null, null);
        Assert.Equal(DatabaseConfiguration.MySql, fromFile.Provider);

        var fromEnvironment = DatabaseConfiguration.Resolve(deployment, user, DatabaseConfiguration.PostgreSql,
            "Host=localhost;Database=plinth;Username=plinth");
        Assert.Equal(DatabaseConfiguration.PostgreSql, fromEnvironment.Provider);
    }

    [Theory]
    [InlineData("{}", "original")]
    [InlineData("{\"connectionString\":\"Host=localhost;Database=override\"}", "override")]
    public void Partial_user_file_preserves_deployment_provider(string json, string database)
    {
        var configuration = DatabaseConfiguration.Resolve(new DatabaseOptions
        {
            Provider = DatabaseConfiguration.PostgreSql,
            ConnectionString = "Host=localhost;Database=original",
        }, json, null, null);
        Assert.Equal(DatabaseConfiguration.PostgreSql, configuration.Provider);
        Assert.Contains(database, configuration.ConnectionString);
    }

    [Fact]
    public void Null_provider_is_rejected_with_a_configuration_error()
    {
        Assert.Throws<InvalidDataException>(() =>
            DatabaseConfiguration.Resolve(new DatabaseOptions { Provider = null! }, null, null, null));
    }

    [Theory]
    [InlineData(DatabaseConfiguration.MySql, "Server=localhost;Password=secret;UnknownOption=secret")]
    [InlineData(DatabaseConfiguration.PostgreSql, "Host=localhost;Password=secret;UnknownOption=secret")]
    [InlineData(DatabaseConfiguration.MySql, "Server=localhost;Port=secret")]
    [InlineData(DatabaseConfiguration.PostgreSql, "Host=localhost;Port=secret")]
    public void Invalid_provider_options_do_not_expose_secrets(string provider, string connection)
    {
        var error = Assert.Throws<InvalidDataException>(() => DatabaseConfiguration.Resolve(
            new DatabaseOptions { Provider = provider, ConnectionString = connection }, null, null, null));
        // 只断言错误正文：ToString 会包含测试自身的方法名（含 "secret"），不属于用户可见错误。
        Assert.DoesNotContain("secret", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(error.InnerException);
    }

    [Fact]
    public void Migration_history_must_be_a_known_prefix()
    {
        var known = new[] { "001", "002", "003" };
        DatabaseInitializer.ValidateMigrationHistory(known, Array.Empty<string>());
        DatabaseInitializer.ValidateMigrationHistory(known, new[] { "001", "002" });
        DatabaseInitializer.ValidateMigrationHistory(known, known);
        foreach (var applied in new[]
        {
            new[] { "001", "999" }, new[] { "002" },
            new[] { "001", "003" }, new[] { "001", "002", "003", "004" },
        })
            Assert.Throws<InvalidDataException>(() => DatabaseInitializer.ValidateMigrationHistory(known, applied));
    }

    [Theory]
    [InlineData(DatabaseConfiguration.PostgreSql)]
    [InlineData(DatabaseConfiguration.MySql)]
    public void Server_migrations_have_independent_ids_snapshots_and_matching_models(string provider)
    {
        using var runtime = ServerRuntime(provider);
        using var db = DatabaseConfiguration.CreateMigrationContext(runtime);
        var initial = provider == DatabaseConfiguration.MySql
            ? Plinth.App.Migrations.MySql.MySqlInitialSchema.MigrationId
            : Plinth.App.Migrations.PostgreSql.PostgreSqlInitialSchema.MigrationId;
        var expected = db.Database.GetMigrations().ToArray();
        Assert.Equal(2, expected.Length);
        Assert.Equal(initial, expected[0]);
        Assert.Contains("DemoTypedColumns", expected[1], StringComparison.Ordinal);
        var assembly = db.GetService<IMigrationsAssembly>();
        Assert.NotNull(assembly.ModelSnapshot);
        Assert.Equal("9.0.22", assembly.ModelSnapshot!.Model.FindAnnotation("ProductVersion")?.Value);
        Assert.Equal(provider == DatabaseConfiguration.MySql
            ? typeof(Plinth.App.Migrations.MySql.MySqlMigrationContextModelSnapshot)
            : typeof(Plinth.App.Migrations.PostgreSql.PostgreSqlMigrationContextModelSnapshot),
            assembly.ModelSnapshot!.GetType());
        Assert.False(db.Database.HasPendingModelChanges());
        var otherProviderId = provider == DatabaseConfiguration.MySql
            ? Plinth.App.Migrations.PostgreSql.PostgreSqlInitialSchema.MigrationId
            : Plinth.App.Migrations.MySql.MySqlInitialSchema.MigrationId;
        Assert.Throws<InvalidDataException>(() =>
            DatabaseInitializer.ValidateMigrationHistory(expected, new[] { otherProviderId }));

        var differ = db.GetService<IMigrationsModelDiffer>();
        var current = db.GetService<Microsoft.EntityFrameworkCore.Metadata.IDesignTimeModel>().Model;
        var runtimeModel = runtime.GetService<Microsoft.EntityFrameworkCore.Metadata.IDesignTimeModel>().Model;
        Assert.False(differ.HasDifferences(current.GetRelationalModel(), runtimeModel.GetRelationalModel()));
        var migration = assembly.CreateMigration(assembly.Migrations[expected[^1]], db.Database.ProviderName!);
        Assert.Equal("9.0.22", migration.TargetModel.FindAnnotation("ProductVersion")?.Value);
        var target = db.GetService<IModelRuntimeInitializer>().Initialize(migration.TargetModel, designTime: true);
        Assert.False(differ.HasDifferences(target.GetRelationalModel(), current.GetRelationalModel()));

        var script = db.GetService<IMigrator>().GenerateScript();
        Assert.Contains(initial, script, StringComparison.Ordinal);
        Assert.Contains(expected[^1], script, StringComparison.Ordinal);
        Assert.Contains("demo_records", script, StringComparison.Ordinal);
        Assert.Contains("notes_entries", script, StringComparison.Ordinal);
        Assert.Contains("CodeKey", script, StringComparison.Ordinal);
        Assert.Contains(provider == DatabaseConfiguration.MySql ? "datetime(6)" : "timestamp with time zone", script);
        Assert.DoesNotContain("NOCASE", script, StringComparison.Ordinal);
        foreach (var id in new[] { Plinth.App.Migrations.InitialSchema.MigrationId,
            Plinth.App.Migrations.NotesEntries.MigrationId, Plinth.App.Migrations.CodeIdentityKeys.MigrationId })
        {
            Assert.DoesNotContain(id, script, StringComparison.Ordinal);
            Assert.Throws<InvalidDataException>(() => DatabaseInitializer.ValidateMigrationHistory(expected, new[] { id }));
        }
    }

    [Fact]
    public void Design_time_server_factories_are_offline_and_select_independent_sets()
    {
        using var pg = new PostgreSqlMigrationContextFactory().CreateDbContext(Array.Empty<string>());
        using var mysql = new MySqlMigrationContextFactory().CreateDbContext(Array.Empty<string>());
        Assert.Equal(2, pg.Database.GetMigrations().Count());
        Assert.Equal(2, mysql.Database.GetMigrations().Count());
        Assert.False(pg.Database.HasPendingModelChanges());
        Assert.False(mysql.Database.HasPendingModelChanges());
        Assert.Empty(pg.Database.GetMigrations().Intersect(mysql.Database.GetMigrations()));
    }

    [Theory]
    [InlineData(DatabaseConfiguration.PostgreSql, true)]
    [InlineData(DatabaseConfiguration.PostgreSql, false)]
    [InlineData(DatabaseConfiguration.MySql, true)]
    [InlineData(DatabaseConfiguration.MySql, false)]
    public async Task Migration_context_borrows_external_connection_and_preserves_runtime_ownership(
        string provider, bool runtimeOwnsConnection)
    {
        using DbConnection connection = provider == DatabaseConfiguration.MySql
            ? new MySqlConnection("Server=localhost;Database=plinth;User=plinth")
            : new NpgsqlConnection("Host=localhost;Database=plinth;Username=plinth");
        var builder = new DbContextOptionsBuilder<AppDbContext>();
        if (provider == DatabaseConfiguration.MySql)
        {
            builder.UseMySql(connection, new MySqlServerVersion(new Version(8, 0, 12)),
                options => options.CommandTimeout(73).MaxBatchSize(17));
            // Pomelo 9's connection overload has no contextOwnsConnection argument.
            // Set ownership on its real relational extension using EF's options API.
            var relational = builder.Options.Extensions.OfType<RelationalOptionsExtension>().Single();
            ((IDbContextOptionsBuilderInfrastructure)builder).AddOrUpdateExtension(
                relational.WithConnection(connection, owned: runtimeOwnsConnection));
        }
        else
        {
            builder.UseNpgsql(connection, contextOwnsConnection: runtimeOwnsConnection,
                npgsqlOptionsAction: options => options.SetPostgresVersion(14, 0).CommandTimeout(73).MaxBatchSize(17));
        }

        using var runtime = new AppDbContext(builder.Options);
        var original = runtime.GetService<IDbContextOptions>().Extensions.OfType<RelationalOptionsExtension>().Single();
        using (var migration = DatabaseConfiguration.CreateMigrationContext(runtime))
        {
            // Force both relational services to materialize; disposing an unused DbContext
            // would not exercise connection disposal at all.
            Assert.Same(connection, runtime.Database.GetDbConnection());
            Assert.Same(connection, migration.Database.GetDbConnection());
            var borrowed = migration.GetService<IDbContextOptions>().Extensions.OfType<RelationalOptionsExtension>().Single();
            Assert.NotSame(original, borrowed);
            Assert.Equal(original.GetType(), borrowed.GetType());
            Assert.False(borrowed.IsConnectionOwned);
            Assert.Equal(runtimeOwnsConnection, original.IsConnectionOwned);
            Assert.Equal(73, borrowed.CommandTimeout);
            Assert.Equal(17, borrowed.MaxBatchSize);
            Assert.Equal(runtime.Database.GenerateCreateScript(), migration.Database.GenerateCreateScript());
        }

        Assert.Same(original, runtime.GetService<IDbContextOptions>().Extensions.OfType<RelationalOptionsExtension>().Single());
        await AssertConnectionUsableWithoutServer(connection);
        runtime.Dispose();
        if (runtimeOwnsConnection)
            await AssertConnectionDisposedWithoutServer(connection);
        else
        {
            await AssertConnectionUsableWithoutServer(connection);
            connection.Dispose();
            await AssertConnectionDisposedWithoutServer(connection);
        }
    }

    private static async Task AssertConnectionUsableWithoutServer(DbConnection connection)
    {
        if (connection is NpgsqlConnection postgres)
        {
            using var command = postgres.CreateCommand(); // Checks disposed without opening.
        }
        else
        {
            // MySqlConnector.CreateCommand does NOT check disposed. OpenAsync checks
            // disposed before cancellation; an already-cancelled token prevents networking.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                connection.OpenAsync(new CancellationToken(canceled: true)));
        }
    }

    private static async Task AssertConnectionDisposedWithoutServer(DbConnection connection)
    {
        if (connection is NpgsqlConnection postgres)
            Assert.Throws<ObjectDisposedException>(() => { using var command = postgres.CreateCommand(); });
        else
            await Assert.ThrowsAsync<ObjectDisposedException>(() =>
                connection.OpenAsync(new CancellationToken(canceled: true)));
    }

    private static AppDbContext ServerRuntime(string provider)
    {
        var builder = new DbContextOptionsBuilder<AppDbContext>();
        if (provider == DatabaseConfiguration.MySql)
            builder.UseMySql("Server=localhost;Database=plinth;User=plinth",
                new MySqlServerVersion(new Version(8, 0, 12)));
        else
            builder.UseNpgsql("Host=localhost;Database=plinth;Username=plinth");
        return new AppDbContext(builder.Options);
    }
}

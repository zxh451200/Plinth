using System;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using MySqlConnector;
using Npgsql;
using Plinth.App.Services;

namespace Plinth.App.Data;

public sealed class DatabaseOptions
{
    /// <summary>Sqlite、MySql 或 PostgreSql。默认 Sqlite。</summary>
    public string Provider { get; set; } = DatabaseConfiguration.Sqlite;
    /// <summary>服务器数据库连接串；Sqlite 固定使用应用数据目录下的 plinth.db。</summary>
    public string ConnectionString { get; set; } = string.Empty;
}

/// <summary>
/// 数据库选择：环境变量 &gt; 用户数据目录 database.json &gt; deployment.json &gt; Sqlite。
/// connection string 可能含密码，任何日志和异常都不得回显它。
/// </summary>
public sealed class DatabaseConfiguration
{
    public const string Sqlite = "Sqlite";
    public const string MySql = "MySql";
    public const string PostgreSql = "PostgreSql";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private DatabaseConfiguration(string provider, string connectionString)
    {
        Provider = provider;
        ConnectionString = connectionString;
    }

    public static DatabaseConfiguration Current { get; } = Load();

    public string Provider { get; }

    public string ConnectionString { get; }

    public bool IsSqlite => Provider == Sqlite;

    public bool IsServer => !IsSqlite;

    public void Configure(DbContextOptionsBuilder options)
    {
        switch (Provider)
        {
            case Sqlite:
                options.UseSqlite(new SqliteConnectionStringBuilder { DataSource = AppPaths.DatabasePath }.ToString());
                break;
            case MySql:
                options.UseMySql(ConnectionString, ServerVersion.AutoDetect(ConnectionString));
                break;
            case PostgreSql:
                options.UseNpgsql(ConnectionString);
                break;
            default:
                throw new InvalidDataException("Unsupported database provider.");
        }
    }

    /// <summary>Use the configured provider/connection/version, but a distinct EF migration identity.
    /// The caller owns this context. Runtime CRUD continues through AppDbContext.</summary>
    internal static DbContext CreateMigrationContext(AppDbContext runtime)
    {
        DbContextOptionsBuilder builder = runtime.Database.ProviderName switch
        {
            "Npgsql.EntityFrameworkCore.PostgreSQL" => new DbContextOptionsBuilder<PostgreSqlMigrationContext>(),
            "Pomelo.EntityFrameworkCore.MySql" => new DbContextOptionsBuilder<MySqlMigrationContext>(),
            _ => throw new NotSupportedException("Unsupported server database provider."),
        };
        // Copy options rather than rebuilding from a connection string: preserve the detected MySQL
        // version, timeouts and provider settings without another connection. The runtime retains
        // ownership of any supplied connection; the migration context only borrows it.
        foreach (var extension in runtime.GetService<IDbContextOptions>().Extensions)
        {
            var migrationExtension = extension is RelationalOptionsExtension { Connection: { } connection } relational
                ? relational.WithConnection(connection, owned: false)
                : extension;
            ((IDbContextOptionsBuilderInfrastructure)builder).AddOrUpdateExtension(migrationExtension);
        }
        return builder.Options is DbContextOptions<PostgreSqlMigrationContext> postgres
            ? new PostgreSqlMigrationContext(postgres)
            : new MySqlMigrationContext((DbContextOptions<MySqlMigrationContext>)builder.Options);
    }

    internal static DatabaseConfiguration Resolve(DatabaseOptions deployment, string? userFileJson,
        string? environmentProvider, string? environmentConnectionString)
    {
        var provider = deployment.Provider;
        var connectionString = deployment.ConnectionString;
        if (!string.IsNullOrWhiteSpace(userFileJson))
        {
            DatabaseOverrides? user = null;
            try { user = JsonSerializer.Deserialize<DatabaseOverrides>(userFileJson, JsonOptions); }
            catch (JsonException) { throw new InvalidDataException("Invalid database configuration file."); }
            if (user is not null)
            {
                if (!string.IsNullOrWhiteSpace(user.Provider)) provider = user.Provider;
                if (!string.IsNullOrWhiteSpace(user.ConnectionString)) connectionString = user.ConnectionString;
            }
        }

        if (!string.IsNullOrWhiteSpace(environmentProvider)) provider = environmentProvider;
        if (!string.IsNullOrWhiteSpace(environmentConnectionString)) connectionString = environmentConnectionString;

        provider = provider?.Trim();
        if (provider is not (Sqlite or MySql or PostgreSql))
            throw new InvalidDataException("Unsupported database provider.");
        if (provider == Sqlite) return new DatabaseConfiguration(provider, string.Empty);
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidDataException("Server database requires a connection string.");
        try
        {
            // 校验 Provider 参数，不打开连接；驱动异常可能含秘密，不应传播。
            if (provider == MySql) _ = new MySqlConnectionStringBuilder(connectionString);
            else _ = new NpgsqlConnectionStringBuilder(connectionString);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
        {
            throw new InvalidDataException("Invalid database connection string.");
        }
        return new DatabaseConfiguration(provider, connectionString);
    }

    // 未提供的字段继承低优先级配置，不能套用 DatabaseOptions 的默认 Provider。
    private sealed class DatabaseOverrides
    {
        public DatabaseOverrides() { }

        public string? Provider { get; set; }
        public string? ConnectionString { get; set; }
    }

    private static DatabaseConfiguration Load()
    {
        var options = DeploymentConfiguration.Current.Database;
        var userPath = Path.Combine(AppPaths.DataDirectory, "database.json");
        var userJson = DeploymentConfiguration.ReadOptionalConfiguration(userPath);
        var environmentProvider = Environment.GetEnvironmentVariable("PLINTH_Database__Provider");
        var environmentConnection = Environment.GetEnvironmentVariable("PLINTH_Database__ConnectionString");
        return Resolve(options, userJson, environmentProvider, environmentConnection);
    }
}

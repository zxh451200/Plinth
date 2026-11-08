using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Plinth.App.Modules.OfflineSync;

namespace Plinth.App.Data;

// Provider bootstrap belongs to infrastructure, outside portable module code.
// Not used by AppDbContext, DatabaseInitializer or the production DI graph.
internal static class PilotDatabaseOptions
{
    internal static DbContextOptions<PilotDbContext> Create(string databasePath) =>
        new DbContextOptionsBuilder<PilotDbContext>().UseSqlite(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
            DefaultTimeout = 5
        }.ToString()).Options;
}

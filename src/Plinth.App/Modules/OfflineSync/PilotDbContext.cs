using Microsoft.EntityFrameworkCore;

namespace Plinth.App.Modules.OfflineSync;

// No IEntityTypeConfiguration: AppDbContext's assembly scan must stay isolated.
internal sealed class PilotDbContext(DbContextOptions<PilotDbContext> options) : DbContext(options)
{
    public DbSet<PilotOutboxEntry> Outbox => Set<PilotOutboxEntry>();
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var entry = modelBuilder.Entity<PilotOutboxEntry>();
        entry.ToTable("offline_pilot_outbox_v1");
        entry.HasKey(x => x.OperationId);
        entry.Property(x => x.Revision).IsConcurrencyToken();
        entry.Property(x => x.Scope).IsRequired();
        entry.Property(x => x.LocalJson).IsRequired();
        entry.HasIndex(x => x.ActiveKey).IsUnique();
        entry.HasIndex(x => new { x.Scope, x.State, x.NextAttemptTicks });
    }
}

internal sealed class PilotOutboxEntry
{
    public Guid OperationId { get; set; }
    public string Scope { get; set; } = "";
    public Guid RecordId { get; set; }
    public int ExpectedVersion { get; set; }
    public string LocalJson { get; set; } = "";
    public string? ServerJson { get; set; }
    public string? ActiveKey { get; set; }
    public PilotState State { get; set; }
    public int Attempts { get; set; }
    public long NextAttemptTicks { get; set; }
    public long CreatedTicks { get; set; }
    public long Revision { get; set; }
    public Guid? SuccessorId { get; set; }
    public string? Resolution { get; set; }
}

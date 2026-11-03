using Microsoft.EntityFrameworkCore;
using Plinth.App.Data;
using Plinth.App.Modules.Notes.Data;
using Plinth.App.Services;

namespace Plinth.App.Modules.Notes.Services;

public sealed class NoteService(IDbContextFactory<AppDbContext> factory, SensitiveOperationService operations)
{
    private static readonly string DefaultCodeKey = CodeIdentity.Create("default");
    public Task<Note?> LoadAsync(CancellationToken token = default) =>
        operations.ExecuteAsync("notes:read", "Notes", "read", async ct =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            return await db.Set<Note>().AsNoTracking().SingleOrDefaultAsync(note => note.CodeKey == DefaultCodeKey, ct);
        }, token);

    public Task<Note> SaveAsync(string text, int expectedVersion, CancellationToken token = default) =>
        operations.ExecuteConfirmedAsync("notes:save", "Notes", "save", async ct =>
        {
            if (text.Length > 2000) throw new ArgumentException("notes.textTooLong", nameof(text));
            await using var db = await factory.CreateDbContextAsync(ct);
            var note = await db.Set<Note>().SingleOrDefaultAsync(item => item.CodeKey == DefaultCodeKey, ct);
            if (note is null)
            {
                if (expectedVersion != 0) throw new DbUpdateConcurrencyException();
                note = new Note();
                db.Add(note);
            }
            else if (note.Version != expectedVersion) throw new DbUpdateConcurrencyException();
            note.Text = text;
            note.Version++;
            await db.SaveChangesAsync(ct);
            return note;
        }, token);
}

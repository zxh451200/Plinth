using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Plinth.App.Data;

namespace Plinth.App.Modules.Notes.Data;

/// <summary>固定 Code 的单页便笺，演示独立模块持久化。</summary>
public sealed class Note : AuditableEntity
{
    private string _code = string.Empty;
    public Note() => Code = "default";
    public string Code
    {
        get => _code;
        set
        {
            var key = CodeIdentity.Create(value);
            _code = value;
            CodeKey = key;
        }
    }
    public string CodeKey { get; private set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
}

public sealed class NoteConfiguration : IEntityTypeConfiguration<Note>
{
    public void Configure(EntityTypeBuilder<Note> builder)
    {
        builder.ToTable("notes_entries");
        builder.HasKey(note => note.Id);
        builder.Property(note => note.Code).IsRequired().HasMaxLength(50);
        builder.Property(note => note.CodeKey).IsRequired().HasMaxLength(300);
        builder.HasIndex(note => note.CodeKey).IsUnique();
        builder.Property(note => note.Text).IsRequired().HasMaxLength(2000);
        builder.Property(note => note.Version).IsConcurrencyToken();
    }
}

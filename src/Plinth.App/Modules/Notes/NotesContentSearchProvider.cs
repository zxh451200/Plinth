using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Plinth.App.Localization;
using Plinth.App.Navigation;
using Plinth.App.Modules.Notes.Services;

namespace Plinth.App.Modules.Notes;

/// <summary>全局搜索的便笺内容:唯一一份便笺正文命中时返回一条,带命中附近摘录。</summary>
public sealed class NotesContentSearchProvider(NoteService service) : IContentSearchProvider
{
    public string PageId => NotesModuleRegistration.PageId;

    public async Task<IReadOnlyList<ContentSearchHit>> SearchAsync(string query, CancellationToken token)
    {
        var note = await service.LoadAsync(token);
        if (note is null || string.IsNullOrWhiteSpace(note.Text)) return [];
        var index = note.Text.IndexOf(query, StringComparison.CurrentCultureIgnoreCase);
        if (index < 0) return [];
        return [new ContentSearchHit(PageId, Loc.Get("notes.title"), Excerpt(note.Text, index, query.Length), null)];
    }

    /// <summary>命中位置前后各留 30 字符的摘录,换行压成空格,超界截断。</summary>
    private static string Excerpt(string text, int index, int queryLength)
    {
        const int radius = 30;
        var start = Math.Max(0, index - radius);
        var end = Math.Min(text.Length, index + queryLength + radius);
        var excerpt = text[start..end].Replace("\r", " ").Replace("\n", " ");
        return (start > 0 ? "…" : "") + excerpt + (end < text.Length ? "…" : "");
    }
}

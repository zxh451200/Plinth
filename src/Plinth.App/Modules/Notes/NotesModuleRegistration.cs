using Microsoft.Extensions.DependencyInjection;
using Plinth.App.Localization;
using Plinth.App.Modules.Notes.Services;
using Plinth.App.Modules.Notes.ViewModels;
using Plinth.App.Navigation;

namespace Plinth.App.Modules.Notes;

public static class NotesModuleRegistration
{
    public const string PageId = "notes.editor";
    public static IServiceCollection AddNotesModule(this IServiceCollection services)
    {
        services.AddTransient<NoteService>();
        services.AddTransient<NoteViewModel>();
        services.AddSingleton<HelpTopic>(_ => new("notes.editing", Loc.Get("help.notes.title"),
            Loc.Get("help.notes.body"), PageId)
        { TitleKey = "help.notes.title", BodyKey = "help.notes.body" });
        services.AddSingleton(new CommandMenuContribution(PageId, "menu.file.save",
            page => (page as NoteViewModel)?.SaveCommand, PageCommandAction.Save));
        services.AddSingleton(new CommandMenuContribution(PageId, "notes.reload",
            page => (page as NoteViewModel)?.ReloadCommand));
        services.AddSingleton<NavigationItem>(provider => new NavigationItem(PageId, Loc.Get("notes.title"),
            () => provider.GetRequiredService<NoteViewModel>())
        { Order = 210, Icon = "\uE70B", TitleKey = "notes.title" });
        return services;
    }
}

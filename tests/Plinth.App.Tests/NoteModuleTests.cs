using Avalonia.Headless.XUnit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Plinth.App.Modules.Notes;
using Plinth.App.Modules.Notes.Services;
using Plinth.App.Modules.Notes.ViewModels;
using Plinth.App.Modules.Notes.Views;
using Plinth.App.Navigation;
using Plinth.App.Security;
using Plinth.App.Services;
using Xunit;

namespace Plinth.App.Tests;

public sealed class NoteModuleTests
{
    private sealed class Audit : ISecurityAudit
    {
        public List<AuditEvent> Events { get; } = [];
        public void Write(AuditEvent entry) => Events.Add(entry);
    }

    private static SensitiveOperationService Operations(Audit audit, bool authentication = false, bool production = false)
    {
        var users = new OfflineUserContext();
        return new(new AuthorizedOperation(new PermissionService(users, audit), users, audit),
            audit, authentication, production);
    }

    [Fact]
    public async Task Service_preserves_audit_and_rejects_stale_updates_and_oversized_text()
    {
        using var database = new TestDatabase();
        var audit = new Audit();
        var service = new NoteService(database.Factory, Operations(audit));
        Assert.Null(await service.LoadAsync());
        var first = await service.SaveAsync("first", 0);
        Assert.Equal(1, first.Version);
        Assert.NotEqual(default, first.CreatedAtUtc);
        var second = await service.SaveAsync("second", first.Version);
        Assert.Equal(first.CreatedAtUtc, second.CreatedAtUtc);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => service.SaveAsync("stale", first.Version));
        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveAsync(new string('x', 2001), second.Version));
        Assert.Equal("second", (await service.LoadAsync())!.Text);
        Assert.Contains(audit.Events, entry => entry.Module == "Notes" && entry.Result == "success");
    }

    [Fact]
    public async Task Authorization_and_production_cancellation_leave_database_unchanged()
    {
        using var database = new TestDatabase();
        var audit = new Audit();
        var denied = new NoteService(database.Factory, Operations(audit, authentication: true));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => denied.LoadAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => denied.SaveAsync("denied", 0));
        var operations = Operations(audit, production: true);
        operations.ConfirmProductionAsync = () => Task.FromResult(false);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new NoteService(database.Factory, operations).SaveAsync("cancelled", 0));
        Assert.Null(await new NoteService(database.Factory, Operations(audit)).LoadAsync());
    }

    [AvaloniaFact]
    public async Task Dirty_page_cancels_or_saves_before_leaving_and_keeps_text_on_conflict()
    {
        using var database = new TestDatabase();
        var service = new NoteService(database.Factory, Operations(new Audit()));
        var changes = new UnsavedChangesService();
        var page = new NoteViewModel(service, changes, NullLogger<NoteViewModel>.Instance);
        await page.ReloadCommand.ExecuteAsync(null);
        page.Text = "unsaved";
        changes.PromptAsync = () => Task.FromResult(UnsavedChangesChoice.Cancel);
        Assert.False(await page.TryLeaveAsync());
        Assert.Equal("unsaved", page.Text);
        changes.PromptAsync = () => Task.FromResult(UnsavedChangesChoice.Save);
        Assert.True(await page.TryLeaveAsync());
        Assert.Equal("unsaved", (await service.LoadAsync())!.Text);
        await service.SaveAsync("external", 1);
        page.Text = "my text";
        Assert.False(await page.TryLeaveAsync());
        Assert.Equal("my text", page.Text);
        Assert.True(page.IsDirty);
    }

    [AvaloniaFact]
    public async Task Module_registration_resolves_a_view_and_contributes_only_its_own_commands()
    {
        using var database = new TestDatabase();
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton(database.Factory);
        services.AddSingleton(Operations(new Audit()));
        services.AddSingleton<UnsavedChangesService>();
        services.AddNotesModule();
        using var provider = services.BuildServiceProvider();
        var navigation = Assert.Single(provider.GetServices<NavigationItem>());
        Assert.Equal(NotesModuleRegistration.PageId, navigation.Id);
        var page = Assert.IsType<NoteViewModel>(navigation.CreateViewModel());
        Assert.IsType<NoteView>(new ViewLocator().Build(page));
        await page.ReloadCommand.ExecuteAsync(null);
        page.Text = "via menu";
        var contribution = provider.GetServices<CommandMenuContribution>()
            .Single(item => item.Action == PageCommandAction.Save);
        Assert.Same(page.SaveCommand, contribution.ResolveCommand(page));
        Assert.Null(contribution.ResolveCommand(new object()));
        await page.SaveCommand.ExecuteAsync(null);
        Assert.False(page.IsDirty);
    }
}

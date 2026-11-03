using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Plinth.App.Localization;
using Plinth.App.Modules.Notes.Services;
using Plinth.App.Navigation;
using Plinth.App.Services;
using Plinth.App.ViewModels;

namespace Plinth.App.Modules.Notes.ViewModels;

public partial class NoteViewModel(NoteService service, UnsavedChangesService changes,
    ILogger<NoteViewModel> logger) : ViewModelBase, IUnsavedChanges, IPageDetails
{
    private string _savedText = string.Empty;
    private int _version;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial string Text { get; set; } = string.Empty;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(ReloadCommand))]
    public partial bool IsBusy { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Details))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial bool IsLoaded { get; set; }
    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;
    public bool IsDirty => Text != _savedText;
    public System.Collections.Generic.IReadOnlyList<PageDetail> Details => IsLoaded
        ? [new(Loc.Get("notes.title"), _savedText)]
        : [];
    private bool CanSave() => IsLoaded && !IsBusy && IsDirty && Text.Length <= 2000;
    private bool CanReload() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanReload))]
    private async Task ReloadAsync()
    {
        if (!await TryLeaveAsync()) return;
        IsBusy = true;
        try
        {
            var note = await service.LoadAsync();
            _savedText = note?.Text ?? string.Empty;
            _version = note?.Version ?? 0;
            Text = _savedText;
            IsLoaded = true;
            OnPropertyChanged(nameof(Details));
            StatusText = string.Empty;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Note load failed");
            StatusText = Loc.Get("notes.failed");
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(IsDirty));
            SaveCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        IsBusy = true;
        try
        {
            var note = await service.SaveAsync(Text, _version);
            _version = note.Version;
            _savedText = note.Text;
            OnPropertyChanged(nameof(Details));
            StatusText = Loc.Get("settings.saved");
        }
        catch (OperationCanceledException) { StatusText = Loc.Get("settings.cancelled"); }
        catch (DbUpdateException ex)
        {
            logger.LogWarning(ex, "Note save rejected");
            StatusText = Loc.Get("notes.conflict");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Note save failed");
            StatusText = Loc.Get("notes.failed");
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(IsDirty));
            SaveCommand.NotifyCanExecuteChanged();
        }
    }

    public async Task<bool> TryLeaveAsync()
    {
        if (IsBusy) return false;
        if (!IsDirty) return true;
        switch (await changes.AskAsync())
        {
            case UnsavedChangesChoice.Discard:
                Text = _savedText;
                return true;
            case UnsavedChangesChoice.Save when CanSave():
                await SaveCommand.ExecuteAsync(null);
                return !IsDirty;
            default: return false;
        }
    }
}

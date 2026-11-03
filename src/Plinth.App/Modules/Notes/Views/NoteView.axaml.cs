using Avalonia.Controls;
using Plinth.App.Modules.Notes.ViewModels;
using Plinth.App.Services;

namespace Plinth.App.Modules.Notes.Views;

public partial class NoteView : UserControl
{
    public NoteView()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            try
            {
                if (DataContext is NoteViewModel { IsLoaded: false } vm && vm.ReloadCommand.CanExecute(null))
                    await vm.ReloadCommand.ExecuteAsync(null);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { UnhandledErrorReporter.Report(ex); }
        };
    }
}

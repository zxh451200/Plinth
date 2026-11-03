using Avalonia.Controls;
using Plinth.App.ViewModels;
using Plinth.App.Services;

namespace Plinth.App.Views;

public partial class LicenseView : UserControl
{
    private CancellationTokenSource? _attachmentCancellation;
    private LicenseViewModel? _loadedViewModel;

    public LicenseView()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            try
            {
                if (DataContext is not LicenseViewModel vm) return;
                _attachmentCancellation?.Cancel();
                _attachmentCancellation?.Dispose();
                _attachmentCancellation = new CancellationTokenSource();
                _loadedViewModel = vm;
                await vm.LoadAsync(_attachmentCancellation.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { UnhandledErrorReporter.Report(ex); }
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _attachmentCancellation?.Cancel();
            _attachmentCancellation?.Dispose();
            _attachmentCancellation = null;
            _loadedViewModel?.PageClosed();
            _loadedViewModel = null;
        };
    }
}

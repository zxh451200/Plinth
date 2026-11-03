using Avalonia.Controls;
using Avalonia.Interactivity;
using Plinth.App.Services;
using Plinth.App.ViewModels;

namespace Plinth.App.Views;

/// <summary>仅展示安全错误摘要、建议和关联 ID，不接收原始异常消息。</summary>
public partial class ErrorDialog : Window
{
    private string _helpTopicKey = UnhandledErrorPresentation.ErrorCode;
    private TaskError? _error;
    public ErrorDialog()
    {
        InitializeComponent();
    }

    public ErrorDialog(TaskError error) : this()
    {
        _helpTopicKey = error.Code;
        _error = error;
        RelatedLogsButton.IsEnabled = !string.IsNullOrWhiteSpace(error.CorrelationId);
        DetailText.Text = UnhandledErrorPresentation.Format(error);
        // 默认摘要不显示机器路径，防止截图或复制泄露用户目录。
        LogPathText.IsVisible = false;
    }

    private void OnOkClick(object? sender, RoutedEventArgs e) => Close();

    private void OnHelpClick(object? sender, RoutedEventArgs e)
    {
        if (Owner?.DataContext is not MainViewModel shell) return;
        shell.ShowHelpCommand.Execute(_helpTopicKey);
        Close();
    }

    private async void OnLogsClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (Owner?.DataContext is not MainViewModel shell || _error is null) return;
            var error = _error;
            Close();
            await shell.ShowErrorLogsCommand.ExecuteAsync(error);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { UnhandledErrorReporter.Report(ex); }
    }
}

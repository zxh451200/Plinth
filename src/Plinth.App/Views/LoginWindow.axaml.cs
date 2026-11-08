using Avalonia.Controls;
using Avalonia.Input;
using Plinth.App.ViewModels;

namespace Plinth.App.Views;

public partial class LoginWindow : Window
{
    public LoginWindow()
    {
        InitializeComponent();
        Opened += (_, _) =>
        {
            if (DataContext is LoginViewModel { IsSetup: true } && string.IsNullOrWhiteSpace(UsernameBox.Text))
                UsernameBox.Focus();
            else
                PasswordBox.Focus();
        };
        KeyDown += (_, e) =>
        {
            if (DataContext is not LoginViewModel viewModel) return;
            if (e.Key is Key.Enter or Key.Return)
            {
                viewModel.SubmitCommand.Execute(null);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                viewModel.ExitCommand.Execute(null);
                e.Handled = true;
            }
        };
    }
}

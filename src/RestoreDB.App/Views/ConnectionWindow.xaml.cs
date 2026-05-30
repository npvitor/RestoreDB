using System.Windows;
using RestoreDB.Domain.Models;
using RestoreDB.Services;

namespace RestoreDB.App.Views;

public partial class ConnectionWindow : Window
{
    private readonly ConnectionService _connectionService = new();
    private bool _busy;

    public ConnectionWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => ServerBox.Focus();
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        var info = new ConnectionInfo
        {
            Server = ServerBox.Text,
            User = UserBox.Text,
            Password = PasswordBox.Password
        };

        SetBusy(true);
        HideError();
        try
        {
            var connectionString = await _connectionService.ConnectAsync(info);

            var main = new MainWindow(connectionString, info.Server.Trim());
            Application.Current.MainWindow = main;
            main.Show();
            Close();
        }
        catch (InvalidOperationException ex)
        {
            ShowError(ex.Message);
        }
        catch (Exception ex)
        {
            ShowError("Falha ao conectar.\n\nDetalhe: " + ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        ConnectButton.IsEnabled = !busy;
        ServerBox.IsEnabled = !busy;
        UserBox.IsEnabled = !busy;
        PasswordBox.IsEnabled = !busy;
        BusyBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ConnectButton.Content = busy ? "Conectando..." : "Conectar";
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorPanel.Visibility = Visibility.Visible;
    }

    private void HideError() => ErrorPanel.Visibility = Visibility.Collapsed;
}

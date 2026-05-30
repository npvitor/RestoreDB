using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RestoreDB.Domain.Models;
using RestoreDB.Services;

namespace RestoreDB.App.Views;

public partial class MainWindow : Window
{
    private readonly string _connectionString;
    private readonly string _serverName;

    private readonly DatabaseService _databaseService = new();
    private readonly LogService _logService = new();
    private readonly RepairService _repairService;

    private readonly ObservableCollection<DatabaseInfo> _databases = new();
    private readonly ObservableCollection<RepairLogEntry> _logEntries = new();
    private readonly IProgress<RepairLogEntry> _progress;

    private bool _busy;

    private static readonly Brush HealthyBack = new SolidColorBrush(Color.FromRgb(0xE7, 0xF3, 0xEA));
    private static readonly Brush HealthyFore = new SolidColorBrush(Color.FromRgb(0x1B, 0x7A, 0x2F));
    private static readonly Brush ProblemBack = new SolidColorBrush(Color.FromRgb(0xFC, 0xEA, 0xEA));
    private static readonly Brush ProblemFore = new SolidColorBrush(Color.FromRgb(0x9A, 0x14, 0x14));
    private static readonly Brush WarnBack = new SolidColorBrush(Color.FromRgb(0xFF, 0xF6, 0xE5));
    private static readonly Brush WarnFore = new SolidColorBrush(Color.FromRgb(0x8A, 0x52, 0x00));

    public MainWindow(string connectionString, string serverName)
    {
        InitializeComponent();

        _connectionString = connectionString;
        _serverName = serverName;
        _repairService = new RepairService(_logService);
        _progress = new Progress<RepairLogEntry>(OnLogEntry);

        DatabasesGrid.ItemsSource = _databases;
        LogList.ItemsSource = _logEntries;

        ServerInfoText.Text = $"Servidor: {serverName}   •   Catálogo: master";
        LogPathText.Text = "Log: " + _logService.LogFilePath;

        Loaded += async (_, _) => await LoadDatabasesAsync();
    }

    // ------------------------------------------------------------------ Listagem

    private async Task LoadDatabasesAsync()
    {
        if (_busy) return;
        SetBusy(true);
        StatusText.Text = "Carregando lista de bancos...";

        var previousSelection = (DatabasesGrid.SelectedItem as DatabaseInfo)?.Name;
        try
        {
            var list = await _databaseService.ListDatabasesAsync(_connectionString);

            _databases.Clear();
            foreach (var db in list)
                _databases.Add(db);

            var problemCount = list.Count(d => d.IsProblematic);
            StatusText.Text = $"{list.Count} banco(s) encontrado(s)" +
                              (problemCount > 0
                                  ? $" — {problemCount} em estado problemático."
                                  : " — todos saudáveis.");

            if (previousSelection is not null)
            {
                var match = _databases.FirstOrDefault(d =>
                    string.Equals(d.Name, previousSelection, StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                    DatabasesGrid.SelectedItem = match;
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = "Falha ao carregar bancos.";
            ShowError("Não foi possível carregar os bancos de dados.", ex);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => _ = LoadDatabasesAsync();

    private void ReconnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var window = new ConnectionWindow();
        Application.Current.MainWindow = window;
        window.Show();
        Close();
    }

    // ------------------------------------------------------------------ Seleção / detalhes

    private void DatabasesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => UpdateDetailsPanel(DatabasesGrid.SelectedItem as DatabaseInfo, resetActions: true);

    private void UpdateDetailsPanel(DatabaseInfo? db, bool resetActions)
    {
        if (db is null)
        {
            NoSelectionText.Visibility = Visibility.Visible;
            DetailsPanel.Visibility = Visibility.Collapsed;
            return;
        }

        NoSelectionText.Visibility = Visibility.Collapsed;
        DetailsPanel.Visibility = Visibility.Visible;

        RefreshStateLabels(db);

        if (resetActions)
        {
            ResultPanel.Visibility = Visibility.Collapsed;
            DataLossPanel.Visibility = Visibility.Collapsed;
            DataLossConfirmCheck.IsChecked = false;
            DataLossRepairButton.IsEnabled = false;

            HealthyPanel.Visibility = db.IsHealthy ? Visibility.Visible : Visibility.Collapsed;
            SystemDbPanel.Visibility = db is { IsProblematic: true, IsSystemDatabase: true }
                ? Visibility.Visible : Visibility.Collapsed;
            SafeRepairPanel.Visibility = db.IsProblematic ? Visibility.Visible : Visibility.Collapsed;
            SafeRepairButton.IsEnabled = db.IsProblematic && !_busy;
        }
    }

    private void RefreshStateLabels(DatabaseInfo db)
    {
        DbNameText.Text = db.Name + (db.IsSystemDatabase ? "  (sistema)" : string.Empty);
        StateText.Text = db.StateDesc;
        StateFriendlyText.Text = db.StateFriendly;
        RecommendedActionText.Text = db.RecommendedAction;

        StateBadge.Background = db.IsHealthy ? HealthyBack : ProblemBack;
        StateText.Foreground = db.IsHealthy ? HealthyFore : ProblemFore;
    }

    // ------------------------------------------------------------------ Reparo

    private void SafeRepairButton_Click(object sender, RoutedEventArgs e) => _ = RunRepairAsync(allowDataLoss: false);

    private void DataLossConfirmCheck_Changed(object sender, RoutedEventArgs e)
        => DataLossRepairButton.IsEnabled = DataLossConfirmCheck.IsChecked == true && !_busy;

    private void DataLossRepairButton_Click(object sender, RoutedEventArgs e)
    {
        if (DatabasesGrid.SelectedItem is not DatabaseInfo db) return;

        var confirm = MessageBox.Show(
            $"Você está prestes a executar o reparo COM POSSÍVEL PERDA DE DADOS no banco \"{db.Name}\".\n\n" +
            "Esta operação pode descartar dados corrompidos de forma irreversível.\n\n" +
            "Deseja continuar?",
            "Confirmar reparo com perda de dados",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);

        if (confirm == MessageBoxResult.Yes)
            _ = RunRepairAsync(allowDataLoss: true);
    }

    private async Task RunRepairAsync(bool allowDataLoss)
    {
        if (_busy) return;
        if (DatabasesGrid.SelectedItem is not DatabaseInfo db) return;

        SetBusy(true);
        ResultPanel.Visibility = Visibility.Collapsed;
        if (!allowDataLoss)
            DataLossPanel.Visibility = Visibility.Collapsed;
        StatusText.Text = $"Reparando \"{db.Name}\"...";

        try
        {
            var result = await _repairService.RepairAsync(db, _connectionString, allowDataLoss, _progress);
            HandleRepairResult(db, result);
            StatusText.Text = result.Message;
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Operação cancelada.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Falha no reparo.";
            ShowError("Ocorreu uma falha durante o reparo.", ex);
        }
        finally
        {
            SetBusy(false);
            RefreshStateLabels(db);
        }
    }

    private void HandleRepairResult(DatabaseInfo db, RepairResult result)
    {
        ResultPanel.Visibility = Visibility.Visible;
        ResultText.Text = result.Message;

        switch (result.Status)
        {
            case RepairStatus.Success:
            case RepairStatus.NoActionNeeded:
                ResultPanel.Background = HealthyBack;
                ResultPanel.BorderBrush = HealthyFore;
                ResultText.Foreground = HealthyFore;
                SafeRepairPanel.Visibility = Visibility.Collapsed;
                DataLossPanel.Visibility = Visibility.Collapsed;
                HealthyPanel.Visibility = db.IsHealthy ? Visibility.Visible : Visibility.Collapsed;
                break;

            case RepairStatus.NeedsDataLossConfirmation:
                ResultPanel.Background = WarnBack;
                ResultPanel.BorderBrush = WarnFore;
                ResultText.Foreground = WarnFore;
                // Oferece o reparo destrutivo, exigindo confirmação explícita.
                DataLossPanel.Visibility = Visibility.Visible;
                DataLossConfirmCheck.IsChecked = false;
                DataLossRepairButton.IsEnabled = false;
                break;

            default: // Failed
                ResultPanel.Background = ProblemBack;
                ResultPanel.BorderBrush = ProblemFore;
                ResultText.Foreground = ProblemFore;
                break;
        }
    }

    // ------------------------------------------------------------------ Log / estado da UI

    private void OnLogEntry(RepairLogEntry entry)
    {
        _logEntries.Add(entry);
        LogList.ScrollIntoView(entry);
    }

    private void ClearLogButton_Click(object sender, RoutedEventArgs e) => _logEntries.Clear();

    private void SetBusy(bool busy)
    {
        _busy = busy;

        RefreshButton.IsEnabled = !busy;
        ReconnectButton.IsEnabled = !busy;
        ClearLogButton.IsEnabled = !busy;
        DatabasesGrid.IsEnabled = !busy;

        var db = DatabasesGrid.SelectedItem as DatabaseInfo;
        SafeRepairButton.IsEnabled = !busy && db is { IsProblematic: true };
        DataLossRepairButton.IsEnabled = !busy && DataLossConfirmCheck.IsChecked == true;

        RepairBusyBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private static void ShowError(string title, Exception ex)
    {
        MessageBox.Show(title + "\n\nDetalhe: " + ex.Message, "RestoreDB",
            MessageBoxButton.OK, MessageBoxImage.Error);
    }
}

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
            LogRebuildPanel.Visibility = Visibility.Collapsed;
            LogRebuildConfirmCheck.IsChecked = false;
            LogRebuildButton.IsEnabled = false;
            DiagnosisResultPanel.Visibility = Visibility.Collapsed;

            DiagnosePanel.Visibility = db.IsProblematic ? Visibility.Visible : Visibility.Collapsed;
            DiagnoseButton.IsEnabled = db.IsProblematic && !_busy;
            HealthyPanel.Visibility = db.IsHealthy ? Visibility.Visible : Visibility.Collapsed;
            SystemDbPanel.Visibility = db is { IsProblematic: true, IsSystemDatabase: true }
                ? Visibility.Visible : Visibility.Collapsed;
            // As opções de reparo só são liberadas DEPOIS do diagnóstico.
            SafeRepairTitle.Visibility = Visibility.Collapsed;
            SafeRepairPanel.Visibility = Visibility.Collapsed;
            SafeRepairButton.IsEnabled = false;
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

    // ------------------------------------------------------------------ Diagnóstico (somente leitura)

    private void DiagnoseButton_Click(object sender, RoutedEventArgs e) => _ = RunDiagnoseAsync();

    private async Task RunDiagnoseAsync()
    {
        if (_busy) return;
        if (DatabasesGrid.SelectedItem is not DatabaseInfo db) return;

        SetBusy(true);
        DiagnosisResultPanel.Visibility = Visibility.Collapsed;
        StatusText.Text = $"Diagnosticando \"{db.Name}\"...";

        try
        {
            var diag = await _repairService.DiagnoseAsync(db, _connectionString, _progress);

            DiagnosisSummaryText.Text = diag.Summary;
            DiagnosisRecoText.Text = string.IsNullOrEmpty(diag.Recommendation)
                ? string.Empty
                : "Recomendação: " + diag.Recommendation;

            // Cor conforme a conclusão.
            Brush back, fore;
            if (diag.LogCorruptionSuspected && !diag.DataCorruptionSuspected) { back = WarnBack; fore = WarnFore; }
            else if (diag.DataCorruptionSuspected) { back = ProblemBack; fore = ProblemFore; }
            else { back = HealthyBack; fore = HealthyFore; }

            DiagnosisResultPanel.Background = back;
            DiagnosisResultPanel.BorderBrush = fore;
            DiagnosisSummaryText.Foreground = fore;
            DiagnosisRecoText.Foreground = fore;
            DiagnosisResultPanel.Visibility = Visibility.Visible;

            // Se o log está corrompido, já deixa visível a opção recomendada de reconstrução.
            if (diag.LogCorruptionSuspected && !db.IsSystemDatabase)
                ShowLogRebuildOption(db);

            StatusText.Text = diag.Summary;
        }
        catch (Exception ex)
        {
            StatusText.Text = "Falha no diagnóstico.";
            ShowError("Não foi possível concluir o diagnóstico.", ex);
        }
        finally
        {
            // O diagnóstico é a 1ª etapa: somente depois dele liberamos as opções de reparo.
            if (db.IsProblematic)
            {
                SafeRepairTitle.Visibility = Visibility.Visible;
                SafeRepairPanel.Visibility = Visibility.Visible;
            }
            SetBusy(false);
        }
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
            // Mesmo em falha inesperada, oferecer o último recurso (reconstruir o log) p/ bancos de usuário.
            ResultPanel.Visibility = Visibility.Visible;
            ResultPanel.Background = ProblemBack;
            ResultPanel.BorderBrush = ProblemFore;
            ResultText.Foreground = ProblemFore;
            ResultText.Text = "Ocorreu uma falha durante o reparo: " + ex.Message;
            ShowLogRebuildOption(db);
        }
        finally
        {
            SetBusy(false);
            RefreshStateLabels(db);
        }
    }

    /// <summary>Exibe a opção de último recurso (reconstruir o log), exceto em bancos de sistema.</summary>
    private void ShowLogRebuildOption(DatabaseInfo db)
    {
        if (db.IsSystemDatabase)
        {
            LogRebuildPanel.Visibility = Visibility.Collapsed;
            return;
        }
        LogRebuildPanel.Visibility = Visibility.Visible;
        LogRebuildConfirmCheck.IsChecked = false;
        LogRebuildButton.IsEnabled = false;
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
                DiagnosePanel.Visibility = Visibility.Collapsed;
                DiagnosisResultPanel.Visibility = Visibility.Collapsed;
                SafeRepairTitle.Visibility = Visibility.Collapsed;
                SafeRepairPanel.Visibility = Visibility.Collapsed;
                DataLossPanel.Visibility = Visibility.Collapsed;
                LogRebuildPanel.Visibility = Visibility.Collapsed;
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
                // E também oferece o último recurso (reconstruir o log) como alternativa —
                // útil quando o problema é o próprio log de transações (ex.: RECOVERY_PENDING).
                ShowLogRebuildOption(db);
                break;

            default: // Failed
                ResultPanel.Background = ProblemBack;
                ResultPanel.BorderBrush = ProblemFore;
                ResultText.Foreground = ProblemFore;
                // Irreparável: oferecer o último recurso (reconstruir o log).
                ShowLogRebuildOption(db);
                break;
        }
    }

    // ------------------------------------------------------------------ Último recurso: reconstruir log

    private void LogRebuildConfirmCheck_Changed(object sender, RoutedEventArgs e)
        => LogRebuildButton.IsEnabled = LogRebuildConfirmCheck.IsChecked == true && !_busy;

    private void LogRebuildButton_Click(object sender, RoutedEventArgs e)
    {
        if (DatabasesGrid.SelectedItem is not DatabaseInfo db) return;

        var confirm = MessageBox.Show(
            $"ÚLTIMO RECURSO para o banco \"{db.Name}\".\n\n" +
            "O banco será desanexado, o arquivo de log (.ldf) será removido e um novo log será criado " +
            "para forçar o retorno a ONLINE.\n\n" +
            "Pode haver PERDA DE DADOS e inconsistências. Continuar?",
            "Confirmar reconstrução do log",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);

        if (confirm == MessageBoxResult.Yes)
            _ = RunRebuildLogAsync(db);
    }

    private async Task RunRebuildLogAsync(DatabaseInfo db)
    {
        if (_busy) return;

        SetBusy(true);
        ResultPanel.Visibility = Visibility.Collapsed;
        StatusText.Text = $"Reconstruindo o log de \"{db.Name}\"...";

        try
        {
            var result = await _repairService.RebuildTransactionLogAsync(db, _connectionString, _progress);
            HandleRepairResult(db, result);
            if (result.Status == RepairStatus.Success)
                LogRebuildPanel.Visibility = Visibility.Collapsed;
            StatusText.Text = result.Message;
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Operação cancelada.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Falha na reconstrução do log.";
            ShowError("Ocorreu uma falha durante a reconstrução do log.", ex);
        }
        finally
        {
            SetBusy(false);
            RefreshStateLabels(db);
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
        DiagnoseButton.IsEnabled = !busy && db is { IsProblematic: true };
        SafeRepairButton.IsEnabled = !busy && db is { IsProblematic: true };
        DataLossRepairButton.IsEnabled = !busy && DataLossConfirmCheck.IsChecked == true;
        LogRebuildButton.IsEnabled = !busy && LogRebuildConfirmCheck.IsChecked == true;

        RepairBusyBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private static void ShowError(string title, Exception ex)
    {
        MessageBox.Show(title + "\n\nDetalhe: " + ex.Message, "RestoreDB",
            MessageBoxButton.OK, MessageBoxImage.Error);
    }
}

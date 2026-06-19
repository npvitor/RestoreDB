using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using RestoreDB.Domain.Models;
using RestoreDB.Services;

namespace RestoreDB.App.Views;

public partial class MainWindow : Window
{
    private readonly string _connectionString;
    private readonly string _serverName;
    private readonly ServerInfo _serverInfo;

    private readonly DatabaseService _databaseService = new();
    private readonly LogService _logService = new();
    private readonly RepairService _repairService;
    private readonly RestoreService _restoreService;
    private readonly ReportService _reportService = new();

    private readonly ObservableCollection<DatabaseInfo> _databases = new();
    private readonly ObservableCollection<RepairLogEntry> _logEntries = new();
    private readonly ObservableCollection<BackupSetInfo> _backupSets = new();
    private readonly IProgress<RepairLogEntry> _progress;

    private bool _busy;
    private CancellationTokenSource? _cts;

    public MainWindow(string connectionString, string serverName, ServerInfo? serverInfo = null)
    {
        InitializeComponent();

        _connectionString = connectionString;
        _serverName = serverName;
        _serverInfo = serverInfo ?? new ServerInfo();
        _repairService = new RepairService(_logService);
        _restoreService = new RestoreService(_logService);
        _progress = new Progress<RepairLogEntry>(OnLogEntry);

        DatabasesGrid.ItemsSource = _databases;
        LogList.ItemsSource = _logEntries;
        BackupSetsList.ItemsSource = _backupSets;

        // Reflete o tema atual (persiste por toda a aplicação, mesmo ao trocar de conexão).
        SyncThemeToggle();

        var caps = _serverInfo.ProductVersion.Length > 0
            ? $"   •   {_serverInfo.Edition} {_serverInfo.ProductVersion}   •   {(_serverInfo.IsSysadmin ? "sysadmin" : "SEM sysadmin")}"
            : string.Empty;
        ServerInfoText.Text = $"Servidor: {serverName}   •   Catálogo: master{caps}";
        LogPathText.Text = "Log: " + _logService.LogFilePath;

        Loaded += async (_, _) => await LoadDatabasesAsync();
    }

    // ------------------------------------------------------------------ Tema (claro/escuro)

    private void ThemeToggleButton_Click(object sender, RoutedEventArgs e)
    {
        var dark = ThemeToggleButton.IsChecked == true;
        App.ApplyTheme(dark ? AppTheme.Dark : AppTheme.Light);
        SyncThemeToggle();
        LogList.Items.Refresh(); // reaplica as cores do registro (converter) ao novo tema
    }

    private void SyncThemeToggle()
    {
        var dark = App.CurrentTheme == AppTheme.Dark;
        ThemeToggleButton.IsChecked = dark;
        ThemeToggleButton.Content = dark ? "☀  Tema claro" : "🌙  Tema escuro";
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
            TopologyWarnPanel.Visibility = Visibility.Collapsed;
            IndexRepairPanel.Visibility = Visibility.Collapsed;
            ConstraintFixPanel.Visibility = Visibility.Collapsed;
            ConstraintFixConfirmCheck.IsChecked = false;
            ConstraintFixButton.IsEnabled = false;
            RestorePanel.Visibility = Visibility.Collapsed;
            ViewScriptButton.Visibility = Visibility.Collapsed;
            _backupSets.Clear();
            BackupSourceText.Text = string.Empty;

            // Qualquer banco pode ser diagnosticado — inclusive ONLINE: a corrupção física ou de
            // integridade (FK órfã) nem sempre se reflete no estado do banco.
            DiagnosePanel.Visibility = Visibility.Visible;
            DiagnoseButton.IsEnabled = !_busy;
            HealthyPanel.Visibility = db.IsHealthy ? Visibility.Visible : Visibility.Collapsed;
            SystemDbPanel.Visibility = db is { IsProblematic: true, IsSystemDatabase: true }
                ? Visibility.Visible : Visibility.Collapsed;
            // As opções de reparo só são liberadas DEPOIS do diagnóstico.
            RepairOptionsTitle.Visibility = Visibility.Collapsed;
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

        StateBadge.SetResourceReference(Border.BackgroundProperty, db.IsHealthy ? "OkBackBrush" : "ProblemBackBrush");
        StateText.SetResourceReference(TextBlock.ForegroundProperty, db.IsHealthy ? "OkForeBrush" : "ProblemForeBrush");
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
            var diag = await _repairService.DiagnoseAsync(db, _connectionString, _progress, _cts!.Token);

            DiagnosisSummaryText.Text = diag.Summary;
            RecoActionNameText.Text = RecommendedActionLabel(diag.RecommendedAction);
            DiagnosisRecoText.Text = diag.Recommendation;
            DiagnosisRecoText.Visibility = string.IsNullOrEmpty(diag.Recommendation)
                ? Visibility.Collapsed : Visibility.Visible;

            // Cor conforme a severidade da conclusão.
            var (backKey, foreKey) = SeverityKeys(diag.RecommendedAction);
            DiagnosisResultPanel.SetResourceReference(Border.BackgroundProperty, backKey);
            DiagnosisResultPanel.SetResourceReference(Border.BorderBrushProperty, foreKey);
            DiagnosisSummaryText.SetResourceReference(TextBlock.ForegroundProperty, foreKey);
            RecoActionNameText.SetResourceReference(TextBlock.ForegroundProperty, foreKey);
            DiagnosisRecoText.SetResourceReference(TextBlock.ForegroundProperty, foreKey);
            DiagnosisResultPanel.Visibility = Visibility.Visible;

            // Limita as opções exibidas ÀS aplicáveis para a situação diagnosticada.
            await ApplyDiagnosisGatingAsync(db, diag);

            StatusText.Text = diag.Summary;
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Diagnóstico cancelado.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Falha no diagnóstico.";
            ShowError("Não foi possível concluir o diagnóstico.", ex);
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>
    /// Aplica o resultado do diagnóstico à UI exibindo SOMENTE as opções de reparo apropriadas
    /// para a ação recomendada — guiando o usuário ao caminho correto e evitando ações inválidas.
    /// </summary>
    private async Task ApplyDiagnosisGatingAsync(DatabaseInfo db, DiagnosisResult diag)
    {
        // Estado inicial: tudo oculto; cada caso revela o que faz sentido.
        IndexRepairPanel.Visibility = Visibility.Collapsed;
        ConstraintFixPanel.Visibility = Visibility.Collapsed;
        SafeRepairPanel.Visibility = Visibility.Collapsed;
        ViewScriptButton.Visibility = Visibility.Collapsed;
        RestorePanel.Visibility = Visibility.Collapsed;
        DataLossPanel.Visibility = Visibility.Collapsed;
        LogRebuildPanel.Visibility = Visibility.Collapsed;
        RepairOptionsTitle.Visibility = Visibility.Collapsed;

        // Aviso de topologia: independente da ação, alerta quando há alta disponibilidade/replicação.
        if (diag.Topology is { IsRisky: true })
        {
            TopologyWarnText.Text = diag.Topology.Summary + " " + string.Join(" ", diag.Topology.Warnings);
            TopologyWarnPanel.Visibility = Visibility.Visible;
        }

        // Banco de sistema: somente o reparo NÃO destrutivo (modo seguro) é permitido.
        if (db.IsSystemDatabase)
        {
            if (db.IsProblematic)
            {
                RepairOptionsTitle.Visibility = Visibility.Visible;
                SafeRepairPanel.Visibility = Visibility.Visible;
                ViewScriptButton.Visibility = Visibility.Visible;
            }
            return;
        }

        var showOptions = true;
        switch (diag.RecommendedAction)
        {
            // Corrupção restrita a índices não-clusterizados → reconstrução SEM perda.
            case RecommendedActionKind.RebuildIndexesOnly:
                IndexRepairPanel.Visibility = Visibility.Visible;
                break;

            // Corrupção LÓGICA (FK órfã) com físico íntegro → remover registros órfãos.
            case RecommendedActionKind.FixConstraints:
                ConstraintFixPanel.Visibility = Visibility.Visible;
                ConstraintFixConfirmCheck.IsChecked = false;
                ConstraintFixButton.IsEnabled = false;
                if (diag.ForeignKeyViolations.Count > 0)
                    ConstraintFixText.Text =
                        $"Foram encontrados {diag.ForeignKeyViolations.Count} registro(s) órfão(s) (chave estrangeira apontando para um registro inexistente) — o DBCC CHECKDB não detecta isso. " +
                        "A correção REMOVE essas linhas para restaurar a integridade (operação destrutiva, com backup de segurança antes). Para preservar 100% dos registros, prefira restaurar de backup.";
                // Restaurar de backup também é uma alternativa que preserva os dados.
                await ShowRestoreOptionAsync(db);
                break;

            // Corrupção de DADOS → restaurar de backup é o caminho correto; o reparo (REPAIR_REBUILD →
            // e, com confirmação, REPAIR_ALLOW_DATA_LOSS) fica disponível como alternativa.
            case RecommendedActionKind.RestoreFromBackup:
            case RecommendedActionKind.PageRestore:
                await ShowRestoreOptionAsync(db);
                SafeRepairPanel.Visibility = Visibility.Visible;
                ViewScriptButton.Visibility = Visibility.Visible;
                break;

            // Problema de LOG → reparo seguro (reconstrói o log sem Administrador) e, como
            // alternativa explícita, a reconstrução do log (último recurso).
            case RecommendedActionKind.RebuildLog:
                SafeRepairPanel.Visibility = Visibility.Visible;
                ViewScriptButton.Visibility = Visibility.Visible;
                ShowLogRebuildOption(db);
                break;

            // Log cheio / pouco espaço: liberar espaço e religar (modo seguro).
            case RecommendedActionKind.FreeDiskSpace:
                SafeRepairPanel.Visibility = Visibility.Visible;
                ViewScriptButton.Visibility = Visibility.Visible;
                break;

            // CHECKDB íntegro: só age se o estado ainda for problemático (religar).
            case RecommendedActionKind.NoAction:
                if (db.IsProblematic)
                {
                    SafeRepairPanel.Visibility = Visibility.Visible;
                    ViewScriptButton.Visibility = Visibility.Visible;
                }
                else
                {
                    showOptions = false;
                }
                break;

            // Reparo seguro (OFFLINE/RESTORING/RECOVERY genéricos) e fallback.
            case RecommendedActionKind.SafeRepair:
            default:
                SafeRepairPanel.Visibility = Visibility.Visible;
                ViewScriptButton.Visibility = Visibility.Visible;
                break;
        }

        RepairOptionsTitle.Visibility = showOptions ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string RecommendedActionLabel(RecommendedActionKind action) => action switch
    {
        RecommendedActionKind.NoAction => "✔ Nenhuma ação necessária — banco íntegro.",
        RecommendedActionKind.RebuildIndexesOnly => "→ Ação recomendada: reparar índices (sem perda de dados).",
        RecommendedActionKind.SafeRepair => "→ Ação recomendada: reparo no modo seguro (sem perda de dados).",
        RecommendedActionKind.RestoreFromBackup => "→ Ação recomendada: restaurar de backup.",
        RecommendedActionKind.PageRestore => "→ Ação recomendada: restaurar as páginas afetadas (ou de backup).",
        RecommendedActionKind.RebuildLog => "→ Ação recomendada: reconstruir o log de transações.",
        RecommendedActionKind.FixConstraints => "→ Ação recomendada: corrigir integridade lógica (remover registros órfãos).",
        RecommendedActionKind.FreeDiskSpace => "→ Ação recomendada: liberar espaço em disco e religar o banco.",
        RecommendedActionKind.ReviewTopology => "→ Ação recomendada: revisar a topologia antes de qualquer reparo.",
        _ => "→ Ação recomendada: reparo no modo seguro."
    };

    /// <summary>Cor (chaves de recurso) do painel de diagnóstico conforme a severidade da ação.</summary>
    private static (string back, string fore) SeverityKeys(RecommendedActionKind action) => action switch
    {
        RecommendedActionKind.NoAction => ("OkBackBrush", "OkForeBrush"),
        RecommendedActionKind.RebuildIndexesOnly => ("OkBackBrush", "OkForeBrush"),
        RecommendedActionKind.SafeRepair => ("OkBackBrush", "OkForeBrush"),
        RecommendedActionKind.RestoreFromBackup => ("ProblemBackBrush", "ProblemForeBrush"),
        RecommendedActionKind.PageRestore => ("ProblemBackBrush", "ProblemForeBrush"),
        RecommendedActionKind.FixConstraints => ("WarnBackBrush", "WarnForeBrush"),
        _ => ("WarnBackBrush", "WarnForeBrush")
    };

    // ------------------------------------------------------------------ Reparo SEM perda: índices

    private void RepairIndexesButton_Click(object sender, RoutedEventArgs e)
        => _ = RunRebuildIndexesAsync();

    private async Task RunRebuildIndexesAsync()
    {
        if (_busy) return;
        if (DatabasesGrid.SelectedItem is not DatabaseInfo db) return;

        SetBusy(true);
        ResultPanel.Visibility = Visibility.Collapsed;
        StatusText.Text = $"Reparando índices de \"{db.Name}\" (sem perda de dados)...";
        try
        {
            var result = await _repairService.RebuildIndexesAsync(db, _connectionString, _progress, _cts!.Token);
            HandleRepairResult(db, result);
            StatusText.Text = result.Message;
        }
        catch (OperationCanceledException) { StatusText.Text = "Operação cancelada."; }
        catch (Exception ex)
        {
            StatusText.Text = "Falha ao reparar índices.";
            ShowError("Não foi possível reparar os índices.", ex);
        }
        finally
        {
            SetBusy(false);
            RefreshStateLabels(db);
        }
    }

    // ------------------------------------------------------------------ Corrigir integridade lógica (FK órfã)

    private void ConstraintFixConfirmCheck_Changed(object sender, RoutedEventArgs e)
        => ConstraintFixButton.IsEnabled = ConstraintFixConfirmCheck.IsChecked == true && !_busy;

    private void ConstraintFixButton_Click(object sender, RoutedEventArgs e)
    {
        if (DatabasesGrid.SelectedItem is not DatabaseInfo db) return;

        var confirm = MessageBox.Show(
            $"Corrigir a integridade do banco \"{db.Name}\" REMOVENDO os registros órfãos?\n\n" +
            "As linhas cuja chave estrangeira aponta para um registro inexistente serão EXCLUÍDAS " +
            "de forma irreversível. Será feito um backup de segurança antes.\n\n" +
            "Para preservar 100% dos registros, prefira restaurar de backup. Deseja continuar?",
            "Confirmar correção de integridade",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);

        if (confirm == MessageBoxResult.Yes)
            _ = RunFixConstraintsAsync(db);
    }

    private async Task RunFixConstraintsAsync(DatabaseInfo db)
    {
        if (_busy) return;

        SetBusy(true);
        ResultPanel.Visibility = Visibility.Collapsed;
        StatusText.Text = $"Corrigindo integridade de \"{db.Name}\"...";
        try
        {
            var result = await _repairService.FixConstraintViolationsAsync(db, _connectionString, _progress, _cts!.Token);
            HandleRepairResult(db, result);
            if (result.Status == RepairStatus.Success)
                ConstraintFixPanel.Visibility = Visibility.Collapsed;
            StatusText.Text = result.Message;
        }
        catch (OperationCanceledException) { StatusText.Text = "Operação cancelada."; }
        catch (Exception ex)
        {
            StatusText.Text = "Falha ao corrigir a integridade.";
            ShowError("Não foi possível corrigir a integridade do banco.", ex);
        }
        finally
        {
            SetBusy(false);
            RefreshStateLabels(db);
        }
    }

    // ------------------------------------------------------------------ Restaurar de backup

    /// <summary>Exibe o painel de restauração e carrega o histórico de backups do banco (msdb).</summary>
    private async Task ShowRestoreOptionAsync(DatabaseInfo db)
    {
        RestorePanel.Visibility = Visibility.Visible;
        _backupSets.Clear();
        try
        {
            var history = await _restoreService.ListBackupHistoryAsync(db.Name, _connectionString, _cts!.Token);
            foreach (var b in history) _backupSets.Add(b);
            BackupSourceText.Text = history.Count > 0
                ? $"{history.Count} backup(s) no histórico (msdb). Selecione um ou escolha um arquivo .bak."
                : "Nenhum backup no histórico. Escolha um arquivo .bak manualmente.";
        }
        catch
        {
            BackupSourceText.Text = "Não foi possível ler o histórico de backups. Escolha um arquivo .bak manualmente.";
        }
    }

    private async void ChooseBackupButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (DatabasesGrid.SelectedItem is not DatabaseInfo db) return;

        var dialog = new OpenFileDialog
        {
            Title = "Escolher arquivo de backup",
            Filter = "Backup do SQL Server (*.bak;*.trn)|*.bak;*.trn|Todos os arquivos (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != true) return;

        SetBusy(true);
        StatusText.Text = "Lendo o arquivo de backup...";
        try
        {
            var sets = await _restoreService.ListBackupSetsInFileAsync(dialog.FileName, _connectionString, _cts!.Token);
            _backupSets.Clear();
            foreach (var s in sets) _backupSets.Add(s);
            BackupSourceText.Text = $"Arquivo: {dialog.FileName} — {sets.Count} conjunto(s).";
            StatusText.Text = $"{sets.Count} conjunto(s) de backup encontrados.";
        }
        catch (OperationCanceledException) { StatusText.Text = "Operação cancelada."; }
        catch (Exception ex)
        {
            StatusText.Text = "Falha ao ler o backup.";
            ShowError("Não foi possível ler o arquivo de backup.", ex);
        }
        finally { SetBusy(false); }
    }

    private void RestoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (DatabasesGrid.SelectedItem is not DatabaseInfo db) return;
        if (BackupSetsList.SelectedItem is not BackupSetInfo set)
        {
            ShowError("Selecione um conjunto de backup na lista.", new InvalidOperationException("Nenhum backup selecionado."));
            return;
        }

        var confirm = MessageBox.Show(
            $"Restaurar o banco \"{db.Name}\" a partir do backup selecionado?\n\n" +
            $"{set.Display}\n\n" +
            (RestoreReplaceCheck.IsChecked == true
                ? "O banco atual será SUBSTITUÍDO (REPLACE) pelos dados do backup."
                : "A restauração falhará se o banco já existir (REPLACE desmarcado).") +
            "\n\nDeseja continuar?",
            "Confirmar restauração", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);

        if (confirm == MessageBoxResult.Yes)
            _ = RunRestoreAsync(db, set);
    }

    private async Task RunRestoreAsync(DatabaseInfo db, BackupSetInfo set)
    {
        if (_busy) return;

        SetBusy(true);
        ResultPanel.Visibility = Visibility.Collapsed;
        StatusText.Text = $"Restaurando \"{db.Name}\"...";
        try
        {
            var request = new RestoreRequest
            {
                DatabaseName = db.Name,
                Replace = RestoreReplaceCheck.IsChecked == true,
                Pieces =
                {
                    new RestorePiece
                    {
                        DiskPath = set.PhysicalDevice,
                        Position = set.Position,
                        IsLog = string.Equals(set.BackupTypeCode, "L", StringComparison.OrdinalIgnoreCase)
                    }
                }
            };
            var result = await _restoreService.RestoreDatabaseAsync(request, _connectionString, _progress, _cts!.Token);
            HandleRepairResult(db, result);
            StatusText.Text = result.Message;
        }
        catch (OperationCanceledException) { StatusText.Text = "Operação cancelada."; }
        catch (Exception ex)
        {
            StatusText.Text = "Falha na restauração.";
            ShowError("Não foi possível restaurar o banco.", ex);
        }
        finally
        {
            SetBusy(false);
            RefreshStateLabels(db);
        }
    }

    // ------------------------------------------------------------------ Testar backup / cadeia completa

    private void TestRestoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (DatabasesGrid.SelectedItem is not DatabaseInfo db) return;
        if (BackupSetsList.SelectedItem is not BackupSetInfo set)
        {
            ShowError("Selecione um backup na lista para testar.", new InvalidOperationException("Nenhum backup selecionado."));
            return;
        }
        _ = RunTestRestoreAsync(db, set);
    }

    private async Task RunTestRestoreAsync(DatabaseInfo db, BackupSetInfo set)
    {
        if (_busy) return;
        SetBusy(true);
        ResultPanel.Visibility = Visibility.Collapsed;
        StatusText.Text = $"Testando o backup de \"{db.Name}\" (restauração de teste)...";
        try
        {
            var pieces = new List<RestorePiece>
            {
                new() { DiskPath = set.PhysicalDevice, Position = set.Position, IsLog = false }
            };
            var result = await _restoreService.TestRestoreAsync(db.Name, pieces, _connectionString, _progress, _cts!.Token);
            // É um teste: não altera o estado do banco real; mostra só o resultado.
            ResultPanel.Visibility = Visibility.Visible;
            ResultText.Text = result.Message;
            var okKeys = result.Status == RepairStatus.Success;
            ResultPanel.SetResourceReference(Border.BackgroundProperty, okKeys ? "OkBackBrush" : "WarnBackBrush");
            ResultPanel.SetResourceReference(Border.BorderBrushProperty, okKeys ? "OkForeBrush" : "WarnLineBrush");
            ResultText.SetResourceReference(TextBlock.ForegroundProperty, okKeys ? "OkForeBrush" : "WarnForeBrush");
            StatusText.Text = result.Message;
        }
        catch (OperationCanceledException) { StatusText.Text = "Operação cancelada."; }
        catch (Exception ex)
        {
            StatusText.Text = "Falha ao testar o backup.";
            ShowError("Não foi possível testar o backup.", ex);
        }
        finally { SetBusy(false); }
    }

    private void RestoreChainButton_Click(object sender, RoutedEventArgs e)
    {
        if (DatabasesGrid.SelectedItem is not DatabaseInfo db) return;

        var confirm = MessageBox.Show(
            $"Restaurar o banco \"{db.Name}\" pela CADEIA COMPLETA até o ponto mais recente?\n\n" +
            "A ferramenta monta automaticamente FULL → diferencial → logs do histórico (msdb), captura o " +
            "tail-log das transações mais recentes e SUBSTITUI o banco atual. A cadeia é validada antes de executar.\n\n" +
            "Deseja continuar?",
            "Confirmar restauração da cadeia completa",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);

        if (confirm == MessageBoxResult.Yes)
            _ = RunRestoreChainAsync(db);
    }

    private async Task RunRestoreChainAsync(DatabaseInfo db)
    {
        if (_busy) return;
        SetBusy(true);
        ResultPanel.Visibility = Visibility.Collapsed;
        StatusText.Text = $"Restaurando a cadeia completa de \"{db.Name}\"...";
        try
        {
            var result = await _restoreService.RestoreToPointInTimeAsync(
                db.Name, target: null, tailLogBackup: true, _connectionString, _progress, _cts!.Token);
            HandleRepairResult(db, result);
            StatusText.Text = result.Message;
        }
        catch (OperationCanceledException) { StatusText.Text = "Operação cancelada."; }
        catch (Exception ex)
        {
            StatusText.Text = "Falha na restauração da cadeia.";
            ShowError("Não foi possível restaurar a cadeia completa.", ex);
        }
        finally
        {
            SetBusy(false);
            RefreshStateLabels(db);
        }
    }

    // ------------------------------------------------------------------ Ver script (dry-run)

    private void ViewScriptButton_Click(object sender, RoutedEventArgs e)
    {
        if (DatabasesGrid.SelectedItem is not DatabaseInfo db) return;
        var script = RepairService.BuildPlannedScript(db, allowDataLoss: true);
        var window = new Window
        {
            Title = $"Script planejado — {db.Name}",
            Width = 720, Height = 480, Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (Brush)Application.Current.FindResource("WindowBackBrush"),
            Content = new TextBox
            {
                Text = script,
                IsReadOnly = true,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                AcceptsReturn = true,
                Background = (Brush)Application.Current.FindResource("CardBrush"),
                Foreground = (Brush)Application.Current.FindResource("TextBrush"),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                Margin = new Thickness(8)
            }
        };
        window.ShowDialog();
    }

    // ------------------------------------------------------------------ Exportar relatório

    private void ExportReportButton_Click(object sender, RoutedEventArgs e)
    {
        if (_logEntries.Count == 0)
        {
            StatusText.Text = "Nada a exportar (o registro está vazio).";
            return;
        }
        var dialog = new SaveFileDialog
        {
            Title = "Exportar relatório",
            Filter = "Página HTML (*.html)|*.html",
            FileName = $"RestoreDB_{DateTime.Now:yyyyMMdd_HHmmss}.html"
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            _reportService.SaveHtmlReport(_logEntries, _serverName, dialog.FileName);
            StatusText.Text = "Relatório exportado: " + dialog.FileName;
        }
        catch (Exception ex)
        {
            ShowError("Não foi possível exportar o relatório.", ex);
        }
    }

    // ------------------------------------------------------------------ Cancelar

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_cts is { IsCancellationRequested: false })
        {
            _cts.Cancel();
            StatusText.Text = "Cancelando...";
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
            var result = await _repairService.RepairAsync(db, _connectionString, allowDataLoss, _progress, _cts!.Token);
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
            ResultPanel.SetResourceReference(Border.BackgroundProperty, "ProblemBackBrush");
            ResultPanel.SetResourceReference(Border.BorderBrushProperty, "ProblemForeBrush");
            ResultText.SetResourceReference(TextBlock.ForegroundProperty, "ProblemForeBrush");
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
                ResultPanel.SetResourceReference(Border.BackgroundProperty, "OkBackBrush");
                ResultPanel.SetResourceReference(Border.BorderBrushProperty, "OkForeBrush");
                ResultText.SetResourceReference(TextBlock.ForegroundProperty, "OkForeBrush");
                DiagnosePanel.Visibility = Visibility.Collapsed;
                DiagnosisResultPanel.Visibility = Visibility.Collapsed;
                RepairOptionsTitle.Visibility = Visibility.Collapsed;
                SafeRepairPanel.Visibility = Visibility.Collapsed;
                DataLossPanel.Visibility = Visibility.Collapsed;
                LogRebuildPanel.Visibility = Visibility.Collapsed;
                IndexRepairPanel.Visibility = Visibility.Collapsed;
                ConstraintFixPanel.Visibility = Visibility.Collapsed;
                RestorePanel.Visibility = Visibility.Collapsed;
                TopologyWarnPanel.Visibility = Visibility.Collapsed;
                ViewScriptButton.Visibility = Visibility.Collapsed;
                HealthyPanel.Visibility = db.IsHealthy ? Visibility.Visible : Visibility.Collapsed;
                break;

            case RepairStatus.NeedsDataLossConfirmation:
                ResultPanel.SetResourceReference(Border.BackgroundProperty, "WarnBackBrush");
                ResultPanel.SetResourceReference(Border.BorderBrushProperty, "WarnLineBrush");
                ResultText.SetResourceReference(TextBlock.ForegroundProperty, "WarnForeBrush");
                // Oferece o reparo destrutivo, exigindo confirmação explícita.
                DataLossPanel.Visibility = Visibility.Visible;
                DataLossConfirmCheck.IsChecked = false;
                DataLossRepairButton.IsEnabled = false;
                // E também oferece o último recurso (reconstruir o log) como alternativa —
                // útil quando o problema é o próprio log de transações (ex.: RECOVERY_PENDING).
                ShowLogRebuildOption(db);
                break;

            default: // Failed
                ResultPanel.SetResourceReference(Border.BackgroundProperty, "ProblemBackBrush");
                ResultPanel.SetResourceReference(Border.BorderBrushProperty, "ProblemForeBrush");
                ResultText.SetResourceReference(TextBlock.ForegroundProperty, "ProblemForeBrush");
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
            var result = await _repairService.RebuildTransactionLogAsync(db, _connectionString, _progress, _cts!.Token);
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

        if (busy)
        {
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
        }

        RefreshButton.IsEnabled = !busy;
        ReconnectButton.IsEnabled = !busy;
        ClearLogButton.IsEnabled = !busy;
        ExportReportButton.IsEnabled = !busy;
        DatabasesGrid.IsEnabled = !busy;
        CancelButton.IsEnabled = busy;

        var db = DatabasesGrid.SelectedItem as DatabaseInfo;
        // Qualquer banco selecionado pode ser diagnosticado (mesmo ONLINE aparentemente saudável).
        DiagnoseButton.IsEnabled = !busy && db is not null;
        SafeRepairButton.IsEnabled = !busy && db is not null;
        DataLossRepairButton.IsEnabled = !busy && DataLossConfirmCheck.IsChecked == true;
        LogRebuildButton.IsEnabled = !busy && LogRebuildConfirmCheck.IsChecked == true;
        ConstraintFixButton.IsEnabled = !busy && ConstraintFixConfirmCheck.IsChecked == true;
        RepairIndexesButton.IsEnabled = !busy;
        ChooseBackupButton.IsEnabled = !busy;
        RestoreButton.IsEnabled = !busy;
        TestRestoreButton.IsEnabled = !busy;
        RestoreChainButton.IsEnabled = !busy;
        ViewScriptButton.IsEnabled = !busy;

        RepairBusyBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private static void ShowError(string title, Exception ex)
    {
        MessageBox.Show(title + "\n\nDetalhe: " + ex.Message, "RestoreDB",
            MessageBoxButton.OK, MessageBoxImage.Error);
    }
}

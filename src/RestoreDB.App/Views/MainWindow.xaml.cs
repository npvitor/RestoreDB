using System.Collections.ObjectModel;
using System.ComponentModel;
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
    private readonly MaintenanceService _maintenanceService;
    private readonly AvailabilityService _availabilityService;
    private readonly ReportService _reportService = new();

    private readonly ObservableCollection<DatabaseInfo> _databases = new();
    private readonly ObservableCollection<RepairLogEntry> _logEntries = new();
    private readonly ObservableCollection<BackupSetInfo> _backupSets = new();
    private readonly IProgress<RepairLogEntry> _progress;

    private bool _busy;
    private CancellationTokenSource? _cts;

    /// <summary>Nome do banco de cópia recuperada, para o assistente de salvamento (cópia → vivo).</summary>
    private string? _salvageCopyName;

    /// <summary>.bak escolhido pelo usuário para a recuperação de registros-pai órfãos (opcional; vazio = automático via histórico).</summary>
    private string? _recoveryBackupPath;

    /// <summary>Transações destrutivas listadas pela forense, para a restauração ponto-no-tempo.</summary>
    private List<ForensicTransaction> _forensicTx = new();

    /// <summary>Último diagnóstico do banco selecionado (arquivos inacessíveis, etc.).</summary>
    private DiagnosisResult? _lastDiagnosis;

    /// <summary>Novos caminhos escolhidos pelo usuário para arquivos ausentes (nome lógico → caminho).</summary>
    private readonly Dictionary<string, string> _fileRelocations = new(StringComparer.OrdinalIgnoreCase);

    public MainWindow(string connectionString, string serverName, ServerInfo? serverInfo = null)
    {
        InitializeComponent();

        _connectionString = connectionString;
        _serverName = serverName;
        _serverInfo = serverInfo ?? new ServerInfo();
        _restoreService = new RestoreService(_logService);
        _repairService = new RepairService(_logService, _restoreService);
        _maintenanceService = new MaintenanceService(_logService);
        _availabilityService = new AvailabilityService(_logService);
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

    /// <summary>
    /// Fechar a janela no meio de uma operação pode deixar o banco em acesso restrito
    /// (SINGLE_USER/EMERGENCY) sem a limpeza automática — exige confirmação explícita.
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (_busy)
        {
            var r = MessageBox.Show(
                "Há uma operação EM ANDAMENTO. Fechar agora pode deixar o banco em acesso restrito " +
                "(SINGLE_USER) ou em estado intermediário.\n\n" +
                "Prefira usar 'Cancelar' e aguardar a operação terminar.\n\nFechar mesmo assim?",
                "Operação em andamento", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (r != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }
        base.OnClosing(e);
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
            _recoveryBackupPath = null; // a escolha de backup não vaza entre bancos
            UpdateRecoveryBackupLabels();
            RestorePanel.Visibility = Visibility.Collapsed;
            SalvagePanel.Visibility = Visibility.Collapsed;
            _salvageCopyName = null;
            SalvageTableCombo.ItemsSource = null;
            // Sem limpar, o combo mantém as tabelas do banco selecionado ANTERIORMENTE.
            CheckTableCombo.ItemsSource = null;
            CheckTableCombo.Text = string.Empty;
            PagesBox.Text = string.Empty;
            SimulateResultText.Visibility = Visibility.Collapsed;
            ForensicList.Visibility = Visibility.Collapsed;
            ForensicRestoreButton.Visibility = Visibility.Collapsed;
            ForensicList.ItemsSource = null;
            ForensicRecoverRowsButton.Visibility = Visibility.Collapsed;
            LogFullPanel.Visibility = Visibility.Collapsed;
            FileAccessPanel.Visibility = Visibility.Collapsed;
            _fileRelocations.Clear();
            _lastDiagnosis = null;
            RecoverLostRowsCheck.IsChecked = true;
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
            var physicalOnly = PhysicalOnlyCheck.IsChecked == true;
            var diag = await _repairService.DiagnoseAsync(db, _connectionString, _progress, _cts!.Token, physicalOnly);
            _lastDiagnosis = diag;

            DiagnosisSummaryText.Text = diag.Summary;
            RecoActionNameText.Text = RecommendedActionLabel(diag.RecommendedAction);
            DiagnosisRecoText.Text = diag.Recommendation;
            DiagnosisRecoText.Visibility = string.IsNullOrEmpty(diag.Recommendation)
                ? Visibility.Collapsed : Visibility.Visible;

            // Surfacing: novidades (B5) + política de backup (B3) + saúde lógica (B4) no painel.
            PopulateDiagnosisFindings(diag);

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
        LogFullPanel.Visibility = Visibility.Collapsed;
        FileAccessPanel.Visibility = Visibility.Collapsed;
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
                // O suporte não tem como descobrir fileId:pageId sozinho — o diagnóstico já sabe.
                if (diag.RecommendedAction == RecommendedActionKind.PageRestore)
                    PagesBox.Text = string.Join(",", diag.SuspectPages
                        .Where(p => p.IsActiveCorruption)
                        .Select(p => $"{p.FileId}:{p.PageId}")
                        .Distinct()
                        .Take(100));
                break;

            // Problema de LOG → reparo seguro (reconstrói o log sem Administrador) e, como
            // alternativa explícita, a reconstrução do log (último recurso).
            case RecommendedActionKind.RebuildLog:
                SafeRepairPanel.Visibility = Visibility.Visible;
                ViewScriptButton.Visibility = Visibility.Visible;
                ShowLogRebuildOption(db, force: true);
                break;

            // Log de transações cheio (9002): resolução dedicada, sem perda de dados.
            case RecommendedActionKind.ResolveLogFull:
                LogFullPanel.Visibility = Visibility.Visible;
                if (diag.LogSpace is { } ls)
                    LogFullText.Text = "Não é corrupção. " + ls.Summary + " A ferramenta libera o espaço (backup de log, crescimento do " +
                                       "arquivo ou um arquivo de log extra em outro disco) e religa o banco.";
                break;

            // Arquivo ausente / sem permissão / preso por outro processo: apontar, conceder acesso, religar.
            case RecommendedActionKind.FixFileAccess:
                FileAccessPanel.Visibility = Visibility.Visible;
                _fileRelocations.Clear();
                UpdateFileAccessText(diag);
                LocateFileButton.Visibility = diag.FileProblems.Any(f => f.Kind == FileProblemKind.Missing)
                    ? Visibility.Visible : Visibility.Collapsed;
                break;

            // Pouco espaço em disco: liberar espaço e religar (modo seguro).
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
        RecommendedActionKind.ResolveLogFull => "→ Ação recomendada: resolver o log de transações cheio (sem perda de dados).",
        RecommendedActionKind.FixFileAccess => "→ Ação recomendada: corrigir o acesso aos arquivos do banco e religar (sem perda de dados).",
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
        RecommendedActionKind.ResolveLogFull => ("OkBackBrush", "OkForeBrush"),
        RecommendedActionKind.FixFileAccess => ("OkBackBrush", "OkForeBrush"),
        _ => ("WarnBackBrush", "WarnForeBrush")
    };

    /// <summary>Mostra no painel de diagnóstico as novidades (B5) e os achados de política/saúde (B3/B4).</summary>
    private void PopulateDiagnosisFindings(DiagnosisResult diag)
    {
        NoveltyList.ItemsSource = diag.NewSinceLastRun;
        NoveltyPanel.Visibility = diag.NewSinceLastRun.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        var findings = new List<PolicyFinding>();
        findings.AddRange(diag.RootCauseFindings);          // causa-raiz primeiro (mais crítico)
        if (diag.BackupPolicy is not null) findings.AddRange(diag.BackupPolicy.Findings);
        findings.AddRange(diag.HealthFindings);
        FindingsList.ItemsSource = findings;
        PolicyHeader.Visibility = findings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ------------------------------------------------------------------ Log cheio (9002) — sem perda

    private void ResolveLogFullButton_Click(object sender, RoutedEventArgs e)
    {
        if (DatabasesGrid.SelectedItem is not DatabaseInfo db) return;
        _ = RunSimpleActionAsync(db, $"Liberando o log de transações de \"{db.Name}\"...",
            ct => _availabilityService.ResolveLogFullAsync(db, _connectionString, _progress, ct),
            "Não foi possível resolver o log cheio.");
    }

    // ------------------------------------------------------------------ Arquivos inacessíveis — sem perda

    private void UpdateFileAccessText(DiagnosisResult diag)
    {
        var lines = diag.FileProblems.Select(f =>
            "• " + f.Display + (_fileRelocations.TryGetValue(f.LogicalName, out var np) ? $"  →  novo caminho: {np}" : string.Empty));
        FileAccessText.Text = "Não é corrupção — o SQL Server não consegue abrir:\n" + string.Join("\n", lines) +
                              "\n\nAponte onde está cada arquivo ausente (se foi movido) e clique em 'Corrigir acesso aos arquivos e religar'.";
    }

    private void LocateFileButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lastDiagnosis is null) return;
        foreach (var f in _lastDiagnosis.FileProblems.Where(x => x.Kind == FileProblemKind.Missing))
        {
            var dlg = new OpenFileDialog
            {
                Title = $"Onde está agora o arquivo {Path.GetFileName(f.PhysicalName)} ({f.LogicalName})?",
                FileName = Path.GetFileName(f.PhysicalName),
                Filter = f.IsLog ? "Arquivo de log SQL (*.ldf)|*.ldf|Todos os arquivos (*.*)|*.*"
                                 : "Arquivo de dados SQL (*.mdf;*.ndf)|*.mdf;*.ndf|Todos os arquivos (*.*)|*.*"
            };
            if (dlg.ShowDialog(this) == true) _fileRelocations[f.LogicalName] = dlg.FileName;
        }
        UpdateFileAccessText(_lastDiagnosis);
    }

    private void FixFileAccessButton_Click(object sender, RoutedEventArgs e)
    {
        if (DatabasesGrid.SelectedItem is not DatabaseInfo db) return;
        var relocations = new Dictionary<string, string>(_fileRelocations, StringComparer.OrdinalIgnoreCase);
        _ = RunSimpleActionAsync(db, $"Corrigindo o acesso aos arquivos de \"{db.Name}\"...",
            ct => _availabilityService.FixFileAccessAsync(db, _connectionString, _progress, ct, relocations),
            "Não foi possível corrigir o acesso aos arquivos.");
    }

    /// <summary>Executa uma ação de serviço NÃO destrutiva com o mesmo ciclo das demais (busy, resultado, estado).</summary>
    private async Task RunSimpleActionAsync(DatabaseInfo db, string status, Func<CancellationToken, Task<RepairResult>> action, string errorTitle)
    {
        if (_busy) return;
        SetBusy(true);
        ResultPanel.Visibility = Visibility.Collapsed;
        StatusText.Text = status;
        try
        {
            var result = await action(_cts!.Token);
            HandleRepairResult(db, result);
            StatusText.Text = result.Message;
        }
        catch (OperationCanceledException) { StatusText.Text = "Operação cancelada."; }
        catch (Exception ex) { StatusText.Text = errorTitle; ShowError(errorTitle, ex); }
        finally { SetBusy(false); RefreshStateLabels(db); }
    }

    // ------------------------------------------------------------------ Verificações avançadas (CHECKTABLE / reseed)

    private async void CheckTableCombo_DropDownOpened(object sender, EventArgs e)
    {
        if (CheckTableCombo.HasItems) return;
        if (DatabasesGrid.SelectedItem is not DatabaseInfo db) return;
        try { CheckTableCombo.ItemsSource = await _restoreService.ListUserTablesAsync(db.Name, _connectionString); }
        catch { /* best-effort */ }
    }

    private void CheckTableButton_Click(object sender, RoutedEventArgs e)
    {
        if (DatabasesGrid.SelectedItem is not DatabaseInfo db) return;
        var table = (CheckTableCombo.SelectedItem as string) ?? CheckTableCombo.Text;
        if (string.IsNullOrWhiteSpace(table))
        {
            ShowError("Informe ou selecione uma tabela (schema.tabela).", new InvalidOperationException("Tabela não informada."));
            return;
        }
        _ = RunCheckTableAsync(db, table.Trim());
    }

    private async Task RunCheckTableAsync(DatabaseInfo db, string table)
    {
        if (_busy) return;
        SetBusy(true);
        StatusText.Text = $"Verificando a tabela {table}...";
        try
        {
            var rep = await _repairService.CheckTableAsync(db, table, _connectionString, _progress, _cts!.Token);
            ResultPanel.Visibility = Visibility.Visible;
            var ok = rep.Executed && !rep.HasErrors;
            ResultText.Text = !rep.Executed
                ? $"Não foi possível verificar {table} (banco/objeto inacessível)."
                : ok ? $"{table}: sem erros de consistência." : $"{table}: {rep.ErrorCount} erro(s) — veja o registro.";
            ResultPanel.SetResourceReference(Border.BackgroundProperty, ok ? "OkBackBrush" : "WarnBackBrush");
            ResultPanel.SetResourceReference(Border.BorderBrushProperty, ok ? "OkForeBrush" : "WarnLineBrush");
            ResultText.SetResourceReference(TextBlock.ForegroundProperty, ok ? "OkForeBrush" : "WarnForeBrush");
            StatusText.Text = ResultText.Text;
        }
        catch (OperationCanceledException) { StatusText.Text = "Operação cancelada."; }
        catch (Exception ex) { StatusText.Text = "Falha ao verificar a tabela."; ShowError("Não foi possível verificar a tabela.", ex); }
        finally { SetBusy(false); }
    }

    private void ReseedButton_Click(object sender, RoutedEventArgs e)
    {
        if (DatabasesGrid.SelectedItem is not DatabaseInfo db) return;
        _ = RunReseedAsync(db);
    }

    private async Task RunReseedAsync(DatabaseInfo db)
    {
        if (_busy) return;
        SetBusy(true);
        StatusText.Text = $"Reajustando identidades de \"{db.Name}\"...";
        try
        {
            var n = await _repairService.ReseedIdentitiesAsync(db, _connectionString, _progress, _cts!.Token);
            ResultPanel.Visibility = Visibility.Visible;
            ResultText.Text = $"{n} tabela(s) com IDENTITY reajustada(s).";
            ResultPanel.SetResourceReference(Border.BackgroundProperty, "OkBackBrush");
            ResultPanel.SetResourceReference(Border.BorderBrushProperty, "OkForeBrush");
            ResultText.SetResourceReference(TextBlock.ForegroundProperty, "OkForeBrush");
            StatusText.Text = ResultText.Text;
        }
        catch (OperationCanceledException) { StatusText.Text = "Operação cancelada."; }
        catch (Exception ex) { StatusText.Text = "Falha ao reajustar identidades."; ShowError("Não foi possível reajustar as identidades.", ex); }
        finally { SetBusy(false); }
    }

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

    // ---- Backup opcional para a recuperação de registros-pai (usado pelos painéis de correção e de reparo com perda) ----

    private void ChooseRecoveryBackupButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Escolher backup (.bak) para recuperar registros-pai",
            Filter = "Backup do SQL Server (*.bak)|*.bak|Todos os arquivos (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != true) return;
        _recoveryBackupPath = dialog.FileName;
        UpdateRecoveryBackupLabels();
    }

    private void ClearRecoveryBackupButton_Click(object sender, RoutedEventArgs e)
    {
        _recoveryBackupPath = null;
        UpdateRecoveryBackupLabels();
    }

    private void UpdateRecoveryBackupLabels()
    {
        var text = string.IsNullOrEmpty(_recoveryBackupPath)
            ? "Automático: usa o backup FULL mais recente do histórico. Escolha um .bak se o backup estiver em outra mídia (o SQL Server precisa conseguir lê-lo)."
            : $"Usando o backup escolhido: {_recoveryBackupPath}";
        RecoveryBakFixText.Text = text;
        RecoveryBakLossText.Text = text;
    }

    private ParentRecoveryOptions BuildRecoveryOptions() => new()
    {
        InitialBackupPath = _recoveryBackupPath,
        RecoverLostRows = RecoverLostRowsCheck.IsChecked == true,
        ChooseAnotherBackup = PromptForAnotherBackupAsync
    };

    /// <summary>
    /// Chamado pelo serviço quando a recuperação de registros-pai falhou/foi parcial: pergunta ao
    /// usuário se quer escolher OUTRO .bak (ex.: em outra mídia) antes de remover os órfãos. Sempre
    /// na thread de UI. Devolve o novo caminho, ou null para prosseguir com a remoção.
    /// </summary>
    private Task<string?> PromptForAnotherBackupAsync(ParentRecoveryPrompt prompt, CancellationToken ct)
        => Dispatcher.InvokeAsync(() =>
        {
            var msg =
                $"A recuperação de registros-pai de \"{prompt.DatabaseName}\" não resolveu tudo:\n\n" +
                $"• Motivo: {prompt.Reason}\n" +
                $"• Registros-pai recuperados até agora: {prompt.Recovered}\n" +
                $"• Órfãos ainda SEM pai: {prompt.RemainingOrphans}\n\n" +
                "Deseja escolher OUTRO arquivo .bak (ex.: em outra mídia) para tentar recuperar os pais restantes?\n\n" +
                "Sim = escolher outro backup e tentar de novo.\n" +
                "Não = prosseguir REMOVENDO os órfãos remanescentes (irreversível).";
            var choice = MessageBox.Show(msg, "Recuperação de registros-pai incompleta",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.Yes);
            if (choice != MessageBoxResult.Yes) return (string?)null;

            var dlg = new OpenFileDialog
            {
                Title = "Escolher outro .bak para recuperar registros-pai",
                Filter = "Backup do SQL Server (*.bak)|*.bak|Todos os arquivos (*.*)|*.*"
            };
            return dlg.ShowDialog(this) == true ? dlg.FileName : (string?)null;
        }).Task;

    private void ConstraintFixButton_Click(object sender, RoutedEventArgs e)
    {
        if (DatabasesGrid.SelectedItem is not DatabaseInfo db) return;

        var confirm = MessageBox.Show(
            $"Corrigir a integridade do banco \"{db.Name}\" REMOVENDO os registros órfãos?\n\n" +
            "As linhas cuja chave estrangeira aponta para um registro inexistente serão EXCLUÍDAS " +
            "de forma irreversível. Será feito um backup de segurança antes.\n\n" +
            "Se houver um backup completo disponível, a ferramenta tentará primeiro recuperar " +
            "automaticamente os registros-pai a partir dele para evitar excluir os registros filhos " +
            "(melhor esforço — pode não encontrar tudo, ou não haver backup disponível).\n\n" +
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
            var result = await _repairService.FixConstraintViolationsAsync(db, _connectionString, _progress, _cts!.Token, BuildRecoveryOptions());
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

        // Diferencial/log sozinho normalmente não restaura (exige o FULL antes) — orienta o usuário.
        if (!string.Equals(set.BackupTypeCode, "D", StringComparison.OrdinalIgnoreCase))
        {
            var proceed = MessageBox.Show(
                "O conjunto selecionado NÃO é um backup COMPLETO (é diferencial ou de log). Sozinho, ele " +
                "normalmente não pode ser restaurado.\n\n" +
                "Recomendado: use 'Restaurar cadeia completa', que monta FULL → diferencial → logs automaticamente.\n\n" +
                "Continuar mesmo assim (uso avançado)?",
                "Backup não é FULL", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (proceed != MessageBoxResult.Yes) return;
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
                        DiskPath = set.AllDevices.Count > 0 ? set.AllDevices[0] : set.PhysicalDevice,
                        // Backup striped (dividido em vários arquivos): todos os volumes são necessários.
                        AdditionalDevices = set.AllDevices.Skip(1).ToList(),
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
        if (!string.Equals(set.BackupTypeCode, "D", StringComparison.OrdinalIgnoreCase))
        {
            ShowError("O teste de restauração precisa de um backup COMPLETO (full). Selecione um conjunto FULL na lista.",
                new InvalidOperationException("Conjunto selecionado não é FULL."));
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
                new()
                {
                    DiskPath = set.AllDevices.Count > 0 ? set.AllDevices[0] : set.PhysicalDevice,
                    AdditionalDevices = set.AllDevices.Skip(1).ToList(),
                    Position = set.Position,
                    IsLog = false
                }
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

    // ------------------------------------------------------------------ Ponto-no-tempo (STOPAT)

    private void PointInTimeCheck_Changed(object sender, RoutedEventArgs e)
        => PointInTimeRow.Visibility = PointInTimeCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Alvo de ponto-no-tempo (STOPAT). Devolve false quando a opção está marcada mas incompleta —
    /// prosseguir silenciosamente restauraria "até o mais recente", contrariando a intenção do usuário.
    /// </summary>
    private bool TryGetPointInTimeTarget(out DateTime? target)
    {
        target = null;
        if (PointInTimeCheck.IsChecked != true) return true;
        if (StopAtDate.SelectedDate is not DateTime d)
        {
            ShowError("A opção 'Restaurar até um ponto no tempo' está marcada, mas a DATA não foi informada. " +
                      "Informe a data (e a hora) ou desmarque a opção.",
                new InvalidOperationException("Data do STOPAT não informada."));
            return false;
        }
        if (!TimeSpan.TryParse(StopAtTime.Text?.Trim(), out var t)) t = new TimeSpan(23, 59, 59);
        target = d.Date + t;
        return true;
    }

    private void RestoreChainButton_Click(object sender, RoutedEventArgs e)
    {
        if (DatabasesGrid.SelectedItem is not DatabaseInfo db) return;

        if (!TryGetPointInTimeTarget(out var target)) return;
        var alvo = target.HasValue ? $"até {target.Value:yyyy-MM-dd HH:mm:ss}" : "até o ponto mais recente";
        var confirm = MessageBox.Show(
            $"Restaurar o banco \"{db.Name}\" pela CADEIA COMPLETA {alvo}?\n\n" +
            "A ferramenta monta automaticamente FULL → diferencial → logs do histórico (msdb), captura o " +
            "tail-log das transações mais recentes e SUBSTITUI o banco atual. A cadeia é validada antes de executar.\n\n" +
            "Deseja continuar?",
            "Confirmar restauração da cadeia completa",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);

        if (confirm == MessageBoxResult.Yes)
            _ = RunRestoreChainAsync(db, target);
    }

    private async Task RunRestoreChainAsync(DatabaseInfo db, DateTime? target)
    {
        if (_busy) return;
        SetBusy(true);
        ResultPanel.Visibility = Visibility.Collapsed;
        StatusText.Text = $"Restaurando a cadeia completa de \"{db.Name}\"...";
        try
        {
            var result = await _restoreService.RestoreToPointInTimeAsync(
                db.Name, target, tailLogBackup: true, _connectionString, _progress, _cts!.Token);
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

    // ------------------------------------------------------------------ Recuperar para cópia / saúde de backups

    private void RestoreCopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (DatabasesGrid.SelectedItem is not DatabaseInfo db) return;

        var confirm = MessageBox.Show(
            $"Criar uma CÓPIA recuperada de \"{db.Name}\" (banco \"{db.Name}_Recuperado\") a partir do backup mais recente?\n\n" +
            "O banco original NÃO é alterado. A cópia serve para extrair/salvar dados quando o banco vivo está corrompido.\n\n" +
            "Deseja continuar?",
            "Confirmar recuperação para cópia",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);

        if (confirm == MessageBoxResult.Yes)
            _ = RunRestoreCopyAsync(db);
    }

    private async Task RunRestoreCopyAsync(DatabaseInfo db)
    {
        if (_busy) return;
        SetBusy(true);
        ResultPanel.Visibility = Visibility.Collapsed;
        StatusText.Text = $"Recuperando \"{db.Name}\" para uma cópia...";
        try
        {
            var result = await _restoreService.RestoreAsCopyAsync(db.Name, copyName: null, _connectionString, _progress, _cts!.Token);
            // É uma cópia: o estado do banco original não muda. Mostra só o resultado.
            ResultPanel.Visibility = Visibility.Visible;
            ResultText.Text = result.Message;
            var ok = result.Status == RepairStatus.Success;
            ResultPanel.SetResourceReference(Border.BackgroundProperty, ok ? "OkBackBrush" : "ProblemBackBrush");
            ResultPanel.SetResourceReference(Border.BorderBrushProperty, ok ? "OkForeBrush" : "ProblemForeBrush");
            ResultText.SetResourceReference(TextBlock.ForegroundProperty, ok ? "OkForeBrush" : "ProblemForeBrush");
            StatusText.Text = result.Message;
            if (ok)
            {
                var copyName = db.Name + "_Recuperado";
                await LoadDatabasesAsync(); // a cópia aparece na lista (reseleciona e RESETA os painéis)
                // O reset acima anula _salvageCopyName — atribuir somente DEPOIS do reload.
                _salvageCopyName = copyName;
                SalvageHintText.Text = $"Cópia [{copyName}] criada. Selecione uma tabela e traga de volta ao banco vivo \"{db.Name}\" as linhas que faltam (por chave primária, sem duplicar).";
                // Lista as tabelas a partir da CÓPIA (íntegra) — o banco vivo pode estar inacessível.
                await PopulateSalvageTablesAsync(copyName);
                RestorePanel.Visibility = Visibility.Visible;
                SalvagePanel.Visibility = Visibility.Visible;
            }
        }
        catch (OperationCanceledException) { StatusText.Text = "Operação cancelada."; }
        catch (Exception ex)
        {
            StatusText.Text = "Falha ao recuperar para cópia.";
            ShowError("Não foi possível recuperar para uma cópia.", ex);
        }
        finally { SetBusy(false); }
    }

    private async Task PopulateSalvageTablesAsync(string db)
    {
        try
        {
            var tables = await _restoreService.ListUserTablesAsync(db, _connectionString, _cts!.Token);
            SalvageTableCombo.ItemsSource = tables;
            if (tables.Count > 0) SalvageTableCombo.SelectedIndex = 0;
        }
        catch { /* best-effort */ }
    }

    private void SalvageButton_Click(object sender, RoutedEventArgs e)
    {
        if (DatabasesGrid.SelectedItem is not DatabaseInfo db) return;
        if (_salvageCopyName is null) { ShowError("Crie a cópia recuperada primeiro.", new InvalidOperationException("Sem cópia.")); return; }
        var table = SalvageTableCombo.SelectedItem as string;
        if (string.IsNullOrWhiteSpace(table)) { ShowError("Selecione uma tabela.", new InvalidOperationException("Tabela não selecionada.")); return; }

        var confirm = MessageBox.Show(
            $"Copiar as linhas faltantes de '{table}' da cópia [{_salvageCopyName}] para o banco vivo \"{db.Name}\"?\n\n" +
            "Apenas linhas que ainda não existem (por chave primária) são inseridas — nada é duplicado.",
            "Confirmar salvamento de dados", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);

        if (confirm == MessageBoxResult.Yes)
            _ = RunSalvageAsync(db, table!);
    }

    private async Task RunSalvageAsync(DatabaseInfo db, string table)
    {
        if (_busy) return;
        SetBusy(true);
        StatusText.Text = $"Salvando dados de {table}...";
        try
        {
            var result = await _restoreService.SalvageTableAsync(_salvageCopyName!, db.Name, table, _connectionString, _progress, _cts!.Token);
            ResultPanel.Visibility = Visibility.Visible;
            ResultText.Text = result.Message;
            var ok = result.Status == RepairStatus.Success;
            ResultPanel.SetResourceReference(Border.BackgroundProperty, ok ? "OkBackBrush" : "ProblemBackBrush");
            ResultPanel.SetResourceReference(Border.BorderBrushProperty, ok ? "OkForeBrush" : "ProblemForeBrush");
            ResultText.SetResourceReference(TextBlock.ForegroundProperty, ok ? "OkForeBrush" : "ProblemForeBrush");
            StatusText.Text = result.Message;
        }
        catch (OperationCanceledException) { StatusText.Text = "Operação cancelada."; }
        catch (Exception ex) { StatusText.Text = "Falha ao salvar a tabela."; ShowError("Não foi possível salvar a tabela.", ex); }
        finally { SetBusy(false); }
    }

    // ------------------------------------------------------------------ Restauração de página

    private void RestorePagesButton_Click(object sender, RoutedEventArgs e)
    {
        if (DatabasesGrid.SelectedItem is not DatabaseInfo db) return;
        var pages = PagesBox.Text?.Trim() ?? string.Empty;
        if (pages.Length == 0)
        {
            ShowError("Informe as páginas no formato fileId:pageId (ex.: 1:57,1:58).", new InvalidOperationException("Páginas não informadas."));
            return;
        }

        var confirm = MessageBox.Show(
            $"Restaurar apenas as páginas [{pages}] de \"{db.Name}\"?\n\n" +
            "O restante do banco é preservado. Exige modelo FULL/BULK e a cadeia de backups (full + logs).\n\nDeseja continuar?",
            "Confirmar restauração de página", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);

        if (confirm == MessageBoxResult.Yes)
            _ = RunRestorePagesAsync(db, pages);
    }

    private async Task RunRestorePagesAsync(DatabaseInfo db, string pages)
    {
        if (_busy) return;
        SetBusy(true);
        ResultPanel.Visibility = Visibility.Collapsed;
        StatusText.Text = $"Restaurando páginas de \"{db.Name}\"...";
        try
        {
            var result = await _restoreService.RestorePagesFromHistoryAsync(db.Name, pages, _connectionString, _progress, _cts!.Token);
            HandleRepairResult(db, result);
            StatusText.Text = result.Message;
        }
        catch (OperationCanceledException) { StatusText.Text = "Operação cancelada."; }
        catch (Exception ex) { StatusText.Text = "Falha na restauração de página."; ShowError("Não foi possível restaurar as páginas.", ex); }
        finally { SetBusy(false); RefreshStateLabels(db); }
    }

    private void VerifyBackupsButton_Click(object sender, RoutedEventArgs e)
    {
        if (DatabasesGrid.SelectedItem is not DatabaseInfo db) return;
        _ = RunVerifyBackupsAsync(db);
    }

    private async Task RunVerifyBackupsAsync(DatabaseInfo db)
    {
        if (_busy) return;
        SetBusy(true);
        ResultPanel.Visibility = Visibility.Collapsed;
        StatusText.Text = $"Verificando a saúde dos backups de \"{db.Name}\"...";
        try
        {
            var items = await _restoreService.VerifyBackupHealthAsync(db.Name, _connectionString, _progress, _cts!.Token);
            var ok = items.Count(i => i.Verified);
            ResultPanel.Visibility = Visibility.Visible;
            ResultText.Text = items.Count == 0
                ? "Sem histórico de backups para verificar."
                : $"Saúde dos backups: {ok}/{items.Count} verificado(s) com sucesso (detalhes no registro abaixo).";
            var allOk = items.Count > 0 && ok == items.Count;
            ResultPanel.SetResourceReference(Border.BackgroundProperty, allOk ? "OkBackBrush" : "WarnBackBrush");
            ResultPanel.SetResourceReference(Border.BorderBrushProperty, allOk ? "OkForeBrush" : "WarnLineBrush");
            ResultText.SetResourceReference(TextBlock.ForegroundProperty, allOk ? "OkForeBrush" : "WarnForeBrush");
            StatusText.Text = ResultText.Text;
        }
        catch (OperationCanceledException) { StatusText.Text = "Operação cancelada."; }
        catch (Exception ex)
        {
            StatusText.Text = "Falha ao verificar os backups.";
            ShowError("Não foi possível verificar a saúde dos backups.", ex);
        }
        finally { SetBusy(false); }
    }

    // ------------------------------------------------------------------ STANDBY / finalizar recuperação

    private void RestoreStandbyButton_Click(object sender, RoutedEventArgs e)
    {
        if (DatabasesGrid.SelectedItem is not DatabaseInfo db) return;
        if (!TryGetPointInTimeTarget(out var standbyTarget)) return;

        var confirm = MessageBox.Show(
            $"Restaurar \"{db.Name}\" em modo STANDBY (somente leitura)?\n\n" +
            "O banco fica CONSULTÁVEL entre os logs, para você inspecionar os dados e achar o ponto certo. " +
            "O banco atual é SUBSTITUÍDO. Finalize depois com 'Finalizar recuperação'.\n\nDeseja continuar?",
            "Confirmar restauração em STANDBY",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);

        if (confirm == MessageBoxResult.Yes)
            _ = RunStandbyAsync(db, standbyTarget);
    }

    private async Task RunStandbyAsync(DatabaseInfo db, DateTime? target)
    {
        if (_busy) return;
        SetBusy(true);
        ResultPanel.Visibility = Visibility.Collapsed;
        StatusText.Text = $"Restaurando \"{db.Name}\" em STANDBY...";
        try
        {
            var result = await _restoreService.RestoreToStandbyAsync(db.Name, target, _connectionString, _progress, _cts!.Token);
            HandleRepairResult(db, result);
            StatusText.Text = result.Message;
        }
        catch (OperationCanceledException) { StatusText.Text = "Operação cancelada."; }
        catch (Exception ex)
        {
            StatusText.Text = "Falha na restauração STANDBY.";
            ShowError("Não foi possível restaurar em STANDBY.", ex);
        }
        finally { SetBusy(false); RefreshStateLabels(db); }
    }

    private void FinalizeRecoveryButton_Click(object sender, RoutedEventArgs e)
    {
        if (DatabasesGrid.SelectedItem is not DatabaseInfo db) return;
        _ = RunFinalizeRecoveryAsync(db);
    }

    private async Task RunFinalizeRecoveryAsync(DatabaseInfo db)
    {
        if (_busy) return;
        SetBusy(true);
        ResultPanel.Visibility = Visibility.Collapsed;
        StatusText.Text = $"Finalizando a recuperação de \"{db.Name}\"...";
        try
        {
            var result = await _restoreService.FinalizeRecoveryAsync(db.Name, _connectionString, _progress, _cts!.Token);
            HandleRepairResult(db, result);
            StatusText.Text = result.Message;
        }
        catch (OperationCanceledException) { StatusText.Text = "Operação cancelada."; }
        catch (Exception ex)
        {
            StatusText.Text = "Falha ao finalizar a recuperação.";
            ShowError("Não foi possível finalizar a recuperação.", ex);
        }
        finally { SetBusy(false); RefreshStateLabels(db); }
    }

    // ------------------------------------------------------------------ Simular reparo (pré-visualizar perda)

    private void SimulateRepairButton_Click(object sender, RoutedEventArgs e)
    {
        if (DatabasesGrid.SelectedItem is not DatabaseInfo db) return;
        _ = RunSimulateRepairAsync(db);
    }

    private async Task RunSimulateRepairAsync(DatabaseInfo db)
    {
        if (_busy) return;
        SetBusy(true);
        SimulateResultText.Visibility = Visibility.Collapsed;
        StatusText.Text = $"Simulando o reparo de \"{db.Name}\" numa cópia...";
        try
        {
            var sim = await _repairService.SimulateRepairAsync(db, _connectionString, _progress, _cts!.Token);
            SimulateResultText.Visibility = Visibility.Visible;
            SimulateResultText.Text = sim.Executed
                ? sim.Message + (sim.AffectedTables.Count > 0
                    ? "\nTabelas afetadas: " + string.Join("; ", sim.AffectedTables.Take(6).Select(t => t.Display))
                    : string.Empty)
                : sim.Message;
            StatusText.Text = sim.Message;
        }
        catch (OperationCanceledException) { StatusText.Text = "Operação cancelada."; }
        catch (Exception ex) { StatusText.Text = "Falha na simulação."; ShowError("Não foi possível simular o reparo.", ex); }
        finally { SetBusy(false); }
    }

    // ------------------------------------------------------------------ Runbook de banco de sistema

    private void RunbookButton_Click(object sender, RoutedEventArgs e)
    {
        if (DatabasesGrid.SelectedItem is not DatabaseInfo db) return;
        ShowTextDialog($"Procedimento manual — {db.Name}", RepairService.BuildSystemDbRunbook(db.Name));
    }

    // ------------------------------------------------------------------ Manutenção preventiva

    private void MaintainButton_Click(object sender, RoutedEventArgs e)
    {
        if (DatabasesGrid.SelectedItem is not DatabaseInfo db) return;
        var confirm = MessageBox.Show(
            $"Otimizar índices e atualizar estatísticas de \"{db.Name}\"?\n\n" +
            "Operação NÃO destrutiva (REORGANIZE/REBUILD de índices fragmentados + sp_updatestats). " +
            "Pode levar tempo em bancos grandes.\n\nContinuar?",
            "Confirmar manutenção", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);
        if (confirm == MessageBoxResult.Yes) _ = RunMaintenanceAsync(db);
    }

    private async Task RunMaintenanceAsync(DatabaseInfo db)
    {
        if (_busy) return;
        SetBusy(true);
        ResultPanel.Visibility = Visibility.Collapsed;
        StatusText.Text = $"Executando manutenção de \"{db.Name}\"...";
        try
        {
            var r = await _maintenanceService.RunMaintenanceAsync(db, _connectionString, _progress, _cts!.Token);
            HandleRepairResult(db, r);
            StatusText.Text = r.Message;
        }
        catch (OperationCanceledException) { StatusText.Text = "Operação cancelada."; }
        catch (Exception ex) { StatusText.Text = "Falha na manutenção."; ShowError("Não foi possível executar a manutenção.", ex); }
        finally { SetBusy(false); }
    }

    // ------------------------------------------------------------------ Verificação agendada (gerar comando)

    private void ScheduleButton_Click(object sender, RoutedEventArgs e)
    {
        if (DatabasesGrid.SelectedItem is not DatabaseInfo db) return;
        var cmd =
            "Para uma verificação diária automática, registre uma tarefa no Agendador do Windows usando o utilitário\n" +
            "RestoreDB.Probe (comando 'monitor'). Ele faz um diagnóstico somente-leitura e grava um alerta quando\n" +
            "aparece algo NOVO (código de saída 2). Exemplo (ajuste o caminho do .exe):\n\n" +
            $"schtasks /Create /SC DAILY /ST 02:00 /TN \"RestoreDB_Monitor_{db.Name}\" ^\n" +
            $"  /TR \"cmd /c set PROBE_SERVER={_serverName}&& \\\"C:\\Caminho\\RestoreDB.Probe.exe\\\" monitor {db.Name}\"\n\n" +
            "Alertas são gravados em %LOCALAPPDATA%\\RestoreDB\\Alerts. Use a ação 'Enviar e-mail' do Agendador\n" +
            "condicionada ao código de saída para notificar o suporte.";
        ShowTextDialog($"Verificação agendada — {db.Name}", cmd);
    }

    // ------------------------------------------------------------------ Forense de exclusão acidental

    private void ForensicButton_Click(object sender, RoutedEventArgs e)
    {
        if (DatabasesGrid.SelectedItem is not DatabaseInfo db) return;
        _ = RunForensicSearchAsync(db);
    }

    private async Task RunForensicSearchAsync(DatabaseInfo db)
    {
        if (_busy) return;
        SetBusy(true);
        StatusText.Text = $"Procurando exclusões recentes no log de \"{db.Name}\"...";
        try
        {
            _forensicTx = await _restoreService.ListRecentDestructiveTransactionsAsync(db.Name, _connectionString, _cts!.Token);
            ForensicList.ItemsSource = _forensicTx;
            ForensicList.Visibility = Visibility.Visible;
            ForensicRestoreButton.Visibility = _forensicTx.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            ForensicRecoverRowsButton.Visibility = _forensicTx.Any(t => t.CanRecoverRowsInPlace) ? Visibility.Visible : Visibility.Collapsed;
            StatusText.Text = _forensicTx.Count > 0
                ? $"{_forensicTx.Count} transação(ões) destrutiva(s) recente(s) no log ativo."
                : "Nenhuma exclusão recente encontrada no log ativo (pode já ter sido truncado).";
        }
        catch (OperationCanceledException) { StatusText.Text = "Operação cancelada."; }
        catch (Exception ex) { StatusText.Text = "Falha na busca forense."; ShowError("Não foi possível ler o log de transações.", ex); }
        finally { SetBusy(false); }
    }

    private void ForensicRestoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (DatabasesGrid.SelectedItem is not DatabaseInfo db) return;
        if (ForensicList.SelectedItem is not ForensicTransaction tx) { ShowError("Selecione uma transação na lista.", new InvalidOperationException("Nenhuma transação selecionada.")); return; }
        if (tx.BeginTime is not DateTime begin) { ShowError("Não foi possível determinar o instante da transação.", new InvalidOperationException("Sem horário.")); return; }

        var target = begin.AddSeconds(-1);
        var confirm = MessageBox.Show(
            $"Restaurar \"{db.Name}\" até {target:yyyy-MM-dd HH:mm:ss} (logo ANTES da {tx.Kind} de {begin:HH:mm:ss})?\n\n" +
            "O banco será SUBSTITUÍDO pela cadeia de backups até esse instante. Exige modelo FULL e a cadeia disponível.\n\nContinuar?",
            "Confirmar recuperação ponto-no-tempo", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (confirm == MessageBoxResult.Yes)
            _ = RunRestoreChainAsync(db, target);
    }

    private void ForensicRecoverRowsButton_Click(object sender, RoutedEventArgs e)
    {
        if (DatabasesGrid.SelectedItem is not DatabaseInfo db) return;
        if (ForensicList.SelectedItem is not ForensicTransaction tx) { ShowError("Selecione uma transação na lista.", new InvalidOperationException("Nenhuma transação selecionada.")); return; }
        if (!tx.CanRecoverRowsInPlace)
        {
            ShowError("Esta transação não é uma exclusão de linhas numa tabela identificada (DROP/TRUNCATE ou tabela desconhecida). " +
                      "Use 'Restaurar o banco inteiro até ANTES' ou 'Recuperar para cópia'.", new InvalidOperationException("Transação não suportada."));
            return;
        }
        if (tx.BeginTime is not DateTime begin) { ShowError("Não foi possível determinar o instante da transação.", new InvalidOperationException("Sem horário.")); return; }

        var target = begin.AddSeconds(-1);
        var confirm = MessageBox.Show(
            $"Devolver à tabela {tx.Table} de \"{db.Name}\" as linhas que existiam em {target:yyyy-MM-dd HH:mm:ss} (logo ANTES do DELETE de {begin:HH:mm:ss})?\n\n" +
            "O banco NÃO é sobrescrito: uma cópia temporária é restaurada até esse instante e só as linhas que faltam voltam " +
            "(por chave primária, sem duplicar). Tudo o que foi feito depois continua no banco.\n\n" +
            "Atenção: linhas dessa tabela excluídas DE PROPÓSITO depois desse instante também voltariam.\n\nContinuar?",
            "Confirmar recuperação das linhas excluídas", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);
        if (confirm != MessageBoxResult.Yes) return;

        _ = RunRecoverDeletedRowsAsync(db, target, tx.Table);
    }

    private async Task RunRecoverDeletedRowsAsync(DatabaseInfo db, DateTime target, string table)
    {
        if (_busy) return;
        SetBusy(true);
        ResultPanel.Visibility = Visibility.Collapsed;
        StatusText.Text = $"Recuperando as linhas excluídas de {table}...";
        try
        {
            var result = await _restoreService.RecoverDeletedRowsAsync(db.Name, target, new[] { table }, _connectionString, _progress, _cts!.Token);
            HandleRepairResult(db, result);
            StatusText.Text = result.Message;
            if (!string.IsNullOrEmpty(result.BackupFile))
            {
                // Cópia mantida (alguma tabela não pôde ser tratada automaticamente): oferece o assistente.
                await LoadDatabasesAsync();
                _salvageCopyName = result.BackupFile;
                SalvageHintText.Text = $"Cópia [{result.BackupFile}] (estado de antes da exclusão) mantida. Selecione uma tabela e traga de volta ao banco \"{db.Name}\" as linhas que faltam.";
                await PopulateSalvageTablesAsync(result.BackupFile);
                SalvagePanel.Visibility = Visibility.Visible;
            }
        }
        catch (OperationCanceledException) { StatusText.Text = "Operação cancelada."; }
        catch (Exception ex) { StatusText.Text = "Falha ao recuperar as linhas."; ShowError("Não foi possível recuperar as linhas excluídas.", ex); }
        finally { SetBusy(false); }
    }

    // ------------------------------------------------------------------ Anexar banco a partir de .mdf

    private async void AttachButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var dlg = new OpenFileDialog
        {
            Title = "Escolher o arquivo de dados (.mdf)",
            Filter = "Arquivo de dados SQL (*.mdf)|*.mdf|Todos os arquivos (*.*)|*.*"
        };
        if (dlg.ShowDialog(this) != true) return;

        var mdf = dlg.FileName;
        var name = Path.GetFileNameWithoutExtension(mdf);
        var dir = Path.GetDirectoryName(mdf) ?? string.Empty;

        // Procura o .ldf pelos padrões comuns; se não achar, oferece localizar manualmente antes
        // de recriar o log (recriar descarta transações não confirmadas).
        string? ldf = new[] { name + "_log.ldf", name + "_1.ldf", name + ".ldf" }
            .Select(n => Path.Combine(dir, n)).FirstOrDefault(File.Exists);
        if (ldf is null)
        {
            var find = MessageBox.Show(
                "O arquivo de log (.ldf) não foi encontrado ao lado do .mdf.\n\n" +
                "Deseja LOCALIZÁ-LO manualmente? (Se não, um log NOVO será recriado — transações não " +
                "confirmadas podem ser perdidas.)",
                "Arquivo de log não encontrado", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);
            if (find == MessageBoxResult.Yes)
            {
                var ldfDlg = new OpenFileDialog
                {
                    Title = "Escolher o arquivo de log (.ldf)",
                    InitialDirectory = dir,
                    Filter = "Arquivo de log SQL (*.ldf)|*.ldf|Todos os arquivos (*.*)|*.*"
                };
                if (ldfDlg.ShowDialog(this) == true) ldf = ldfDlg.FileName;
            }
        }
        var rebuild = ldf is null;

        var confirm = MessageBox.Show(
            $"Anexar como banco \"{name}\"?\n\nDados: {mdf}\nLog: {(ldf ?? "(não encontrado — será recriado)")}\n\nContinuar?",
            "Confirmar anexação", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);
        if (confirm == MessageBoxResult.Yes)
            await RunAttachAsync(name, mdf, ldf, rebuild);
    }

    private async Task RunAttachAsync(string name, string mdf, string? ldf, bool rebuild)
    {
        if (_busy) return;
        SetBusy(true);
        ResultPanel.Visibility = Visibility.Collapsed;
        StatusText.Text = $"Anexando \"{name}\"...";
        try
        {
            var r = await _restoreService.AttachDatabaseAsync(name, mdf, ldf, rebuild, _connectionString, _progress, _cts!.Token);
            ResultPanel.Visibility = Visibility.Visible;
            ResultText.Text = r.Message;
            var ok = r.Status == RepairStatus.Success;
            ResultPanel.SetResourceReference(Border.BackgroundProperty, ok ? "OkBackBrush" : "ProblemBackBrush");
            ResultPanel.SetResourceReference(Border.BorderBrushProperty, ok ? "OkForeBrush" : "ProblemForeBrush");
            ResultText.SetResourceReference(TextBlock.ForegroundProperty, ok ? "OkForeBrush" : "ProblemForeBrush");
            StatusText.Text = r.Message;
            if (ok) await LoadDatabasesAsync();
        }
        catch (OperationCanceledException) { StatusText.Text = "Operação cancelada."; }
        catch (Exception ex) { StatusText.Text = "Falha ao anexar."; ShowError("Não foi possível anexar o banco.", ex); }
        finally { SetBusy(false); }
    }

    /// <summary>Mostra um texto longo (runbook, comando, etc.) numa janela de leitura.</summary>
    private void ShowTextDialog(string title, string text)
    {
        var window = new Window
        {
            Title = title, Width = 760, Height = 460, Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (Brush)Application.Current.FindResource("WindowBackBrush"),
            Content = new TextBox
            {
                Text = text, IsReadOnly = true, FontFamily = new FontFamily("Consolas"), FontSize = 12,
                AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
                Background = (Brush)Application.Current.FindResource("CardBrush"),
                Foreground = (Brush)Application.Current.FindResource("TextBrush"),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(8)
            }
        };
        window.ShowDialog();
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
            "Esta operação pode descartar dados corrompidos. Antes de executar, a ferramenta tentará " +
            "preservar as transações mais recentes (tail-log) e criar um backup de segurança; ao final, " +
            "um backup FULL novo é gerado. Os arquivos ficam na pasta padrão de backups do servidor.\n\n" +
            (RecoverLostRowsCheck.IsChecked == true
                ? "Se a cadeia de backups chegar até o instante do reparo, as linhas que ele descartar serão " +
                  "DEVOLVIDAS automaticamente a partir dos backups.\n\n"
                : string.Empty) +
            "Se o reparo deixar chaves estrangeiras órfãs e houver um backup completo disponível, a " +
            "ferramenta tentará primeiro recuperar automaticamente os registros-pai a partir dele para " +
            "evitar excluir os registros filhos (melhor esforço — pode não encontrar tudo, ou não haver " +
            "backup disponível).\n\n" +
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
            var result = await _repairService.RepairAsync(db, _connectionString, allowDataLoss, _progress, _cts!.Token, BuildRecoveryOptions());
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

    /// <summary>
    /// Exibe a opção de último recurso (reconstruir o log). Só faz sentido quando o problema
    /// envolve o log/recuperação (SUSPECT/RECOVERY_PENDING/EMERGENCY) ou quando o diagnóstico a
    /// recomendou (<paramref name="force"/>) — oferecê-la após qualquer falha induziria um usuário
    /// de suporte a uma operação destrutiva inadequada (ex.: corrupção de dados num banco ONLINE,
    /// cujo caminho correto é restaurar de backup). Nunca em bancos de sistema.
    /// </summary>
    private void ShowLogRebuildOption(DatabaseInfo db, bool force = false)
    {
        var state = db.StateDesc.Trim().ToUpperInvariant();
        var applies = force || state is "SUSPECT" or "RECOVERY_PENDING" or "EMERGENCY";
        if (db.IsSystemDatabase || !applies)
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
                // O diagnóstico permanece disponível: recomenda-se re-verificar após o reparo.
                DiagnosePanel.Visibility = Visibility.Visible;
                DiagnosisResultPanel.Visibility = Visibility.Collapsed;
                RepairOptionsTitle.Visibility = Visibility.Collapsed;
                SafeRepairPanel.Visibility = Visibility.Collapsed;
                DataLossPanel.Visibility = Visibility.Collapsed;
                LogRebuildPanel.Visibility = Visibility.Collapsed;
                IndexRepairPanel.Visibility = Visibility.Collapsed;
                LogFullPanel.Visibility = Visibility.Collapsed;
                FileAccessPanel.Visibility = Visibility.Collapsed;
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
                // A alternativa que preserva 100% dos dados (restaurar de backup) fica visível
                // LADO A LADO com a confirmação de perda — o usuário decide vendo as duas.
                _ = ShowRestoreOptionAsync(db);
                // E o último recurso (reconstruir o log) quando o estado indicar problema de log.
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
        RestoreCopyButton.IsEnabled = !busy;
        VerifyBackupsButton.IsEnabled = !busy;
        RestoreStandbyButton.IsEnabled = !busy;
        FinalizeRecoveryButton.IsEnabled = !busy;
        RestorePagesButton.IsEnabled = !busy;
        SalvageButton.IsEnabled = !busy;
        CheckTableButton.IsEnabled = !busy;
        ReseedButton.IsEnabled = !busy;
        SimulateRepairButton.IsEnabled = !busy;
        RunbookButton.IsEnabled = !busy;
        MaintainButton.IsEnabled = !busy;
        ScheduleButton.IsEnabled = !busy;
        ForensicButton.IsEnabled = !busy;
        ForensicRestoreButton.IsEnabled = !busy;
        ForensicRecoverRowsButton.IsEnabled = !busy;
        ResolveLogFullButton.IsEnabled = !busy;
        LocateFileButton.IsEnabled = !busy;
        FixFileAccessButton.IsEnabled = !busy;
        AttachButton.IsEnabled = !busy;
        ViewScriptButton.IsEnabled = !busy;
        ChooseRecoveryBakFixButton.IsEnabled = !busy;
        ClearRecoveryBakFixButton.IsEnabled = !busy;
        ChooseRecoveryBakLossButton.IsEnabled = !busy;
        ClearRecoveryBakLossButton.IsEnabled = !busy;

        RepairBusyBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private static void ShowError(string title, Exception ex)
    {
        MessageBox.Show(title + "\n\nDetalhe: " + ex.Message, "RestoreDB",
            MessageBoxButton.OK, MessageBoxImage.Error);
    }
}

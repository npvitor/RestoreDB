using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
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
    private readonly AdjustmentService _adjustmentService;
    private readonly IncrementalCheckService _incrementalService;
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

    /// <summary>A última operação de correção terminou com sucesso — o fluxo pede o Passo 3 (re-diagnosticar).</summary>
    private bool _repairSucceeded;

    /// <summary>O reparo sem perda não bastou: o usuário escolhe entre restaurar de backup e o reparo com perda.</summary>
    private bool _needsDataLossDecision;

    /// <summary>O último diagnóstico não terminou (erro ou cancelamento): não há conclusão em que basear as opções.</summary>
    private bool _diagnosisFailed;

    /// <summary>Abas da área direita: as três do fluxo (liberadas em sequência) e as ferramentas adicionais.</summary>
    private enum FlowTab { Diagnose, Resolve, Confirm, Tools }

    /// <summary>
    /// Aba para onde o fluxo avança quando a operação em andamento terminar (diagnóstico → Resolver,
    /// correção → Conferir). Aplicada em <see cref="UpdateFlowState"/> assim que a tela sai do modo ocupado.
    /// Definida só por <see cref="RequestTab"/>.
    /// </summary>
    private FlowTab? _pendingTab;

    /// <summary>
    /// O avanço vale mesmo para quem está nas Ferramentas (por padrão, quem lançou uma operação de lá
    /// continua lá). Ex.: sobrou uma cópia de salvamento — o próximo passo é o assistente, na aba Resolver.
    /// </summary>
    private bool _pendingTabEvenFromTools;

    /// <summary>As abas na ordem de <see cref="FlowTab"/> (índice = valor do enum).</summary>
    private readonly RadioButton[] _flowTabs;

    /// <summary>Tooltips originais das abas (explicam quando cada uma libera); trocados durante uma operação.</summary>
    private readonly Dictionary<RadioButton, object> _flowTabTips;

    /// <summary>O último diagnóstico concluído foi a verificação rápida (só estrutura física).</summary>
    private bool _lastCheckPhysicalOnly;

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
        _adjustmentService = new AdjustmentService(_logService);
        _incrementalService = new IncrementalCheckService(_logService);
        _progress = new Progress<RepairLogEntry>(OnLogEntry);

        DatabasesGrid.ItemsSource = _databases;
        CollectionViewSource.GetDefaultView(_databases).Filter = o =>
            o is DatabaseInfo d && (DbFilterBox.Text.Length == 0 ||
                                    d.Name.Contains(DbFilterBox.Text.Trim(), StringComparison.OrdinalIgnoreCase));
        LogList.ItemsSource = _logEntries;
        BackupSetsList.ItemsSource = _backupSets;

        // Reflete o tema atual (persiste por toda a aplicação, mesmo ao trocar de conexão).
        SyncThemeToggle();

        var caps = _serverInfo.ProductVersion.Length > 0
            ? $"   •   {_serverInfo.Edition} {_serverInfo.ProductVersion}   •   {(_serverInfo.IsSysadmin ? "sysadmin" : "SEM sysadmin")}"
            : string.Empty;
        ServerInfoText.Text = $"Servidor: {serverName}   •   Catálogo: master{caps}";
        // Só o nome do arquivo na barra; o caminho completo no tooltip (clique abre a pasta).
        LogPathRun.Text = "Log: " + Path.GetFileName(_logService.LogFilePath);
        LogPathLink.ToolTip = _logService.LogFilePath + "\nClique (ou Enter) para abrir a pasta do log.";

        _flowTabs = new[] { TabDiagnose, TabResolve, TabConfirm, TabTools };
        _flowTabTips = _flowTabs.ToDictionary(t => t, t => t.ToolTip);

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
            // "Saudável" exige diagnóstico: um banco ONLINE pode ter corrupção que o estado não mostra.
            StatusText.Text = $"{list.Count} banco(s) encontrado(s)" +
                              (problemCount > 0
                                  ? $" — {problemCount} em estado problemático."
                                  : " — nenhum em estado problemático.");
            UpdateDbCountText(list);

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

    /// <summary>
    /// Atualiza a lista DURANTE uma operação (o <see cref="LoadDatabasesAsync"/> é ignorado enquanto
    /// _busy): acrescenta os bancos novos (cópia recuperada, banco anexado), atualiza o estado dos
    /// existentes e remove os que sumiram — sem limpar a lista, para não desmarcar o banco em uso nem
    /// resetar os painéis (diagnóstico, opções de reparo, resultado).
    /// </summary>
    private async Task MergeDatabaseListAsync()
    {
        try
        {
            var list = await _databaseService.ListDatabasesAsync(_connectionString);
            var names = new HashSet<string>(list.Select(d => d.Name), StringComparer.OrdinalIgnoreCase);
            foreach (var gone in _databases.Where(d => !names.Contains(d.Name)).ToList())
                _databases.Remove(gone);

            for (var i = 0; i < list.Count; i++)
            {
                var fresh = list[i];
                var existing = _databases.FirstOrDefault(d =>
                    string.Equals(d.Name, fresh.Name, StringComparison.OrdinalIgnoreCase));
                if (existing is null)
                    _databases.Insert(Math.Min(i, _databases.Count), fresh);
                else
                    existing.StateDesc = fresh.StateDesc;
            }
            UpdateDbCountText(list);
        }
        catch
        {
            // best-effort: a operação principal já terminou; "Atualizar lista" resolve.
        }
    }

    private void UpdateDbCountText(IReadOnlyCollection<DatabaseInfo> list)
    {
        var problemCount = list.Count(d => d.IsProblematic);
        DbCountText.Text = $"{list.Count} no servidor" +
                           (problemCount > 0 ? $"  •  {problemCount} precisa(m) de atenção" : "  •  nenhum em estado problemático");
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => _ = LoadDatabasesAsync();

    private void DbFilterBox_TextChanged(object sender, TextChangedEventArgs e)
        => CollectionViewSource.GetDefaultView(_databases).Refresh();

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
            NoSelectionPanel.Visibility = Visibility.Visible;
            DetailsPanel.Visibility = Visibility.Collapsed;
            DbHeaderPanel.Visibility = Visibility.Collapsed;
            _pendingTab = null;
            UpdateFlowState(); // sem banco: todas as abas bloqueadas
            return;
        }

        NoSelectionPanel.Visibility = Visibility.Collapsed;
        DetailsPanel.Visibility = Visibility.Visible;
        DbHeaderPanel.Visibility = Visibility.Visible;

        RefreshStateLabels(db);

        if (resetActions)
        {
            // Outro banco: o fluxo recomeça na aba 1 (diagnóstico e opções valem para um banco só).
            _pendingTab = null;
            _lastCheckPhysicalOnly = false;
            TabDiagnose.IsChecked = true;
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
            // Textos preenchidos pelo diagnóstico do banco ANTERIOR (contagem de órfãos, uso do log).
            ConstraintFixText.Text = DefaultConstraintFixText;
            LogFullText.Text = DefaultLogFullText;
            RestoreMoreExpander.IsExpanded = false;
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
            AccessPanel.Visibility = Visibility.Collapsed;
            AdjustmentsPanel.Visibility = Visibility.Collapsed;
            AdjustmentsList.ItemsSource = null;
            UpdateIncrementalStatus(db);
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
            SystemDbPanel.Visibility = db is { IsProblematic: true, IsSystemDatabase: true }
                ? Visibility.Visible : Visibility.Collapsed;
            // As opções de reparo só são liberadas DEPOIS do diagnóstico.
            RepairOptionsTitle.Visibility = Visibility.Collapsed;
            SafeRepairPanel.Visibility = Visibility.Collapsed;
            SafeRepairButton.IsEnabled = !_busy;
            _repairSucceeded = false;
            _needsDataLossDecision = false;
            _diagnosisFailed = false;
            RefreshStateLabels(db); // agora sem o diagnóstico do banco anterior
        }

        UpdateFlowState();
    }

    private const string DefaultConstraintFixText =
        "Há linhas cuja chave estrangeira aponta para um registro inexistente (o DBCC CHECKDB não detecta isso). A correção primeiro tenta " +
        "recuperar o registro-pai do backup; o que sobrar sai da tabela e fica guardado na QUARENTENA (banco RestoreDB_Quarentena), de onde " +
        "pode ser devolvido depois. Antes, a ferramenta tenta um backup de segurança.";

    private const string DefaultLogFullText =
        "Não é corrupção. A ferramenta identifica o que segura o log e libera espaço: backup de log, crescimento do arquivo ou um arquivo " +
        "de log extra em outro disco — e religa o banco. Ela não desconecta ninguém: se uma transação aberta segura o log, o registro mostra qual é.";

    private void RefreshStateLabels(DatabaseInfo db)
    {
        DbNameText.Text = db.Name + (db.IsSystemDatabase ? "  (sistema)" : string.Empty);
        StateText.Text = db.StateDisplay;
        // ONLINE, mas travado (acesso restrito / somente leitura): não é "saudável" para quem usa o sistema.
        var stuck = db.IsHealthy && db.HasAccessRestriction;
        var readOnlyOnly = db.IsReadOnly && !db.UserAccess.Equals("SINGLE_USER", StringComparison.OrdinalIgnoreCase)
                                         && !db.UserAccess.Equals("RESTRICTED_USER", StringComparison.OrdinalIgnoreCase);
        // ONLINE com problema encontrado pelo diagnóstico (corrupção, órfãos, log cheio): o estado sozinho
        // diria "disponível" — o rótulo precisa refletir a conclusão. Depois de uma correção, o diagnóstico
        // anterior não vale mais (Passo 3).
        var diag = _repairSucceeded ? null : _lastDiagnosis;
        var problemFound = db.IsHealthy && !stuck && diag is not null
                           && diag.RecommendedAction is not (RecommendedActionKind.NoAction or RecommendedActionKind.Unknown);

        if (stuck)
        {
            StateFriendlyText.Text = readOnlyOnly
                ? "Disponível, mas SOMENTE LEITURA: o sistema não consegue gravar."
                : $"Disponível, mas TRAVADO ({db.UserAccess}): o sistema não consegue usar o banco normalmente.";
        }
        else if (problemFound)
            StateFriendlyText.Text = "Disponível (no ar), mas o diagnóstico encontrou um problema.";
        else if (diag is { RecommendedAction: RecommendedActionKind.NoAction } && db.IsHealthy)
            StateFriendlyText.Text = "Disponível e íntegro na última verificação.";
        else
            StateFriendlyText.Text = db.StateFriendly;
        // O que fazer fica só no "Próximo passo" (UpdateFlowState): repetir a ação aqui poluía o cabeçalho.

        var (back, fore) = stuck || problemFound ? ("WarnBackBrush", "WarnForeBrush")
            : db.IsHealthy ? ("OkBackBrush", "OkForeBrush") : ("ProblemBackBrush", "ProblemForeBrush");
        StateBadge.SetResourceReference(Border.BackgroundProperty, back);
        StateText.SetResourceReference(TextBlock.ForegroundProperty, fore);
    }

    // ------------------------------------------------------------------ Diagnóstico (somente leitura)

    private void DiagnoseButton_Click(object sender, RoutedEventArgs e) => _ = RunDiagnoseAsync();

    private async Task RunDiagnoseAsync()
    {
        if (_busy) return;
        if (DatabasesGrid.SelectedItem is not DatabaseInfo db) return;

        SetBusy(true);
        DiagnosisResultPanel.Visibility = Visibility.Collapsed;
        // O diagnóstico novo substitui o resultado da operação anterior (que ficaria velho na tela).
        ResultPanel.Visibility = Visibility.Collapsed;
        _repairSucceeded = false;
        // Lançado da aba Conferir: se o banco estiver íntegro, a conferência termina ali mesmo.
        var fromConfirm = SelectedTab() == FlowTab.Confirm;
        // Sem conclusão (erro/cancelamento), o motivo aparece na aba 1; com conclusão, o caminho é decidido abaixo.
        RequestTab(FlowTab.Diagnose);
        StatusText.Text = $"Diagnosticando \"{db.Name}\"...";

        try
        {
            var physicalOnly = PhysicalOnlyCheck.IsChecked == true;
            var diag = await _repairService.DiagnoseAsync(db, _connectionString, _progress, _cts!.Token, physicalOnly);

            // O serviço não lança: erro (ou cancelamento) volta como diagnóstico SEM conclusão (Unknown). Sem
            // conclusão não há "ação recomendada" — mostrar opções aqui seria chutar (antes virava "reparo seguro").
            if (diag.RecommendedAction == RecommendedActionKind.Unknown)
            {
                var cancelled = _cts.IsCancellationRequested;
                _lastDiagnosis = null;
                _diagnosisFailed = true;
                HideOptionPanels();
                ShowDiagnosisPanel(
                    cancelled ? "Diagnóstico cancelado — nada foi alterado no banco." : diag.Summary,
                    cancelled ? "Rode o diagnóstico de novo quando puder." : "Sem um diagnóstico completo, a ferramenta não indica correções. Veja o motivo no registro abaixo e tente de novo.",
                    string.Empty, "WarnBackBrush", "WarnForeBrush");
                NoveltyPanel.Visibility = Visibility.Collapsed;
                PolicyHeader.Visibility = Visibility.Collapsed;
                FindingsList.ItemsSource = null;
                StatusText.Text = cancelled ? "Diagnóstico cancelado." : "Falha no diagnóstico.";
                return;
            }

            _lastDiagnosis = diag;
            _lastCheckPhysicalOnly = physicalOnly;
            _diagnosisFailed = false;
            _needsDataLossDecision = false;
            db.StateDesc = diag.StateDesc; // o estado lido agora (a lista pode estar desatualizada)

            // Novidades, causa-raiz e política de backup no painel (os ajustes ficam no passo 2).
            PopulateDiagnosisFindings(diag);

            // Cor conforme a severidade da conclusão.
            var (backKey, foreKey) = SeverityKeys(diag.RecommendedAction);
            // Banco de sistema: a recomendação genérica ("restaure / reparo com perda") não vale — a ferramenta não faz isso nele.
            var recommendation = db.IsSystemDatabase && diag.RecommendedAction != RecommendedActionKind.NoAction
                ? "Bancos de sistema não são restaurados nem reparados com perda por esta ferramenta. Clique em 'Ver procedimento manual' e acione o DBA/suporte."
                : diag.Recommendation;
            ShowDiagnosisPanel(diag.Summary, RecommendedActionLabel(diag, db), recommendation, backKey, foreKey);

            // Limita as opções exibidas ÀS aplicáveis para a situação diagnosticada.
            await ApplyDiagnosisGatingAsync(db, diag);

            // Avança para Resolver — exceto com aviso de topologia (precisa ser lido antes, na aba 1) e na
            // conferência que confirmou o banco íntegro (fica em Conferir; se sobrar ajuste, UpdateFlowState
            // cai para Resolver porque Conferir deixa de estar liberada).
            RequestTab(TopologyWarnPanel.Visibility == Visibility.Visible ? FlowTab.Diagnose
                : fromConfirm && diag.RecommendedAction == RecommendedActionKind.NoAction ? FlowTab.Confirm
                : FlowTab.Resolve);

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
            RefreshStateLabels(db);
        }
    }

    private void ShowDiagnosisPanel(string summary, string actionLabel, string recommendation, string backKey, string foreKey)
    {
        DiagnosisSummaryText.Text = summary;
        RecoActionNameText.Text = actionLabel;
        DiagnosisRecoText.Text = recommendation;
        DiagnosisRecoText.Visibility = string.IsNullOrEmpty(recommendation) ? Visibility.Collapsed : Visibility.Visible;
        DiagnosisResultPanel.SetResourceReference(Border.BackgroundProperty, backKey);
        DiagnosisResultPanel.SetResourceReference(Border.BorderBrushProperty, foreKey);
        DiagnosisSummaryText.SetResourceReference(TextBlock.ForegroundProperty, foreKey);
        RecoActionNameText.SetResourceReference(TextBlock.ForegroundProperty, foreKey);
        DiagnosisRecoText.SetResourceReference(TextBlock.ForegroundProperty, foreKey);
        DiagnosisResultPanel.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Aplica o resultado do diagnóstico à UI exibindo SOMENTE as opções de reparo apropriadas
    /// para a ação recomendada — guiando o usuário ao caminho correto e evitando ações inválidas.
    /// </summary>
    private async Task ApplyDiagnosisGatingAsync(DatabaseInfo db, DiagnosisResult diag)
    {
        // Estado inicial: tudo oculto; cada caso revela o que faz sentido.
        HideOptionPanels();
        RestoreMoreExpander.IsExpanded = false;
        TopologyWarnPanel.Visibility = Visibility.Collapsed;

        // Aviso de topologia: independente da ação, alerta quando há alta disponibilidade/replicação.
        if (diag.Topology is { IsRisky: true })
        {
            TopologyWarnText.Text = diag.Topology.Summary + " " + string.Join(" ", diag.Topology.Warnings);
            TopologyWarnPanel.Visibility = Visibility.Visible;
        }

        // Ajustes recomendados (configuração/metadados, sem perda): aparecem em qualquer situação — inclusive
        // num banco íntegro — como opção à parte da correção principal.
        ShowAdjustments(diag);

        // Banco de sistema: somente o reparo NÃO destrutivo (modo seguro) é permitido; o resto é manual
        // (procedimento no SystemDbPanel) — inclusive corrupção num banco de sistema ONLINE.
        if (db.IsSystemDatabase)
        {
            SystemDbPanel.Visibility = db.IsProblematic || diag.RecommendedAction != RecommendedActionKind.NoAction
                ? Visibility.Visible : Visibility.Collapsed;
            if (db.IsProblematic)
            {
                SafeRepairPanel.Visibility = Visibility.Visible;
                ViewScriptButton.Visibility = Visibility.Visible;
            }
            UpdateFlowState();
            return;
        }

        switch (diag.RecommendedAction)
        {
            // Corrupção restrita a índices não-clusterizados → reconstrução SEM perda.
            case RecommendedActionKind.RebuildIndexesOnly:
                IndexRepairPanel.Visibility = Visibility.Visible;
                break;

            // Corrupção LÓGICA (FK órfã) com físico íntegro → mover os órfãos para a quarentena. Só violação de
            // CHECK/catálogo não tem correção automática (FixConstraintViolationsAsync só trata FK): sem o painel.
            case RecommendedActionKind.FixConstraints:
                ConstraintFixConfirmCheck.IsChecked = false;
                ConstraintFixButton.IsEnabled = false;
                if (diag.ForeignKeyViolations.Count > 0)
                {
                    ConstraintFixPanel.Visibility = Visibility.Visible;
                    ConstraintFixText.Text =
                        $"Foram encontrados {diag.ForeignKeyViolations.Count} registro(s) órfão(s) (chave estrangeira apontando para um registro inexistente) — o DBCC CHECKDB não detecta isso. " +
                        "A correção primeiro tenta recuperar o registro-pai do backup; o que sobrar sai da tabela e fica guardado na QUARENTENA " +
                        "(banco RestoreDB_Quarentena), de onde pode ser devolvido. Antes, a ferramenta tenta um backup de segurança.";
                }
                // Restaurar de backup também é uma alternativa (desfaz o que veio depois do backup).
                await ShowRestoreOptionAsync(db);
                break;

            // Corrupção de DADOS → restaurar de backup é o caminho correto; o reparo (REPAIR_REBUILD →
            // e, com confirmação, REPAIR_ALLOW_DATA_LOSS) fica disponível como alternativa.
            case RecommendedActionKind.RestoreFromBackup:
            case RecommendedActionKind.PageRestore:
                await ShowRestoreOptionAsync(db);
                SafeRepairPanel.Visibility = Visibility.Visible;
                ViewScriptButton.Visibility = Visibility.Visible;
                // O suporte não tem como descobrir fileId:pageId sozinho — o diagnóstico já sabe. A opção
                // recomendada fica em "Mais opções": abre o grupo para ela não ficar escondida.
                if (diag.RecommendedAction == RecommendedActionKind.PageRestore)
                {
                    PagesBox.Text = string.Join(",", diag.SuspectPages
                        .Where(p => p.IsActiveCorruption)
                        .Select(p => $"{p.FileId}:{p.PageId}")
                        .Distinct()
                        .Take(100));
                    RestoreMoreExpander.IsExpanded = true;
                }
                break;

            // Problema de LOG → reparo seguro primeiro (se não bastar, ele oferece o reparo com perda, que
            // reconstrói o log no servidor) e, como alternativa explícita, a reconstrução do log (último recurso).
            case RecommendedActionKind.RebuildLog:
                SafeRepairPanel.Visibility = Visibility.Visible;
                ViewScriptButton.Visibility = Visibility.Visible;
                ShowLogRebuildOption(db, force: true);
                break;

            // Log de transações cheio (9002): resolução dedicada, sem perda de dados.
            case RecommendedActionKind.ResolveLogFull:
                LogFullPanel.Visibility = Visibility.Visible;
                LogFullText.Text = diag.LogSpace is { } ls
                    ? "Não é corrupção. " + ls.Summary + " A ferramenta libera o espaço (backup de log, crescimento do " +
                      "arquivo ou um arquivo de log extra em outro disco) e religa o banco. Ela não desconecta ninguém: " +
                      "se uma transação aberta segura o log, o registro mostra qual é."
                    : DefaultLogFullText;
                break;

            // Arquivo ausente / sem permissão / preso por outro processo: apontar, conceder acesso, religar.
            case RecommendedActionKind.FixFileAccess:
                FileAccessPanel.Visibility = Visibility.Visible;
                _fileRelocations.Clear();
                UpdateFileAccessText(diag);
                LocateFileButton.Visibility = diag.FileProblems.Any(f => f.Kind == FileProblemKind.Missing)
                    ? Visibility.Visible : Visibility.Collapsed;
                break;

            // Banco ONLINE travado em SINGLE_USER/RESTRICTED_USER: liberar o acesso (sem perda).
            case RecommendedActionKind.FixAccess:
                AccessPanel.Visibility = Visibility.Visible;
                AccessDisconnectCheck.IsChecked = true;
                AccessText.Text =
                    $"O banco está em {diag.Settings?.UserAccess ?? "acesso restrito"} — o sistema não consegue usá-lo. " +
                    "Isso costuma sobrar de uma operação interrompida (reparo, restauração ou manutenção).\n\n" +
                    (diag.AccessHolders.Count > 0
                        ? "Quem está segurando o acesso agora:\n" + string.Join("\n", diag.AccessHolders.Select(h => "• " + h.Display))
                        : "Nenhuma sessão está usando o banco agora; basta devolver o modo normal.");
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
                break;

            // Reparo seguro (OFFLINE/RESTORING/RECOVERY genéricos) e fallback.
            case RecommendedActionKind.SafeRepair:
            default:
                SafeRepairPanel.Visibility = Visibility.Visible;
                ViewScriptButton.Visibility = Visibility.Visible;
                break;
        }

        // A legenda de risco e o aviso do Passo 2 acompanham os painéis visíveis.
        UpdateFlowState();
    }

    private static string RecommendedActionLabel(DiagnosisResult diag, DatabaseInfo db)
    {
        // Banco de sistema: fora do NoAction, a ferramenta só oferece religar (modo seguro) — o resto é manual.
        if (db.IsSystemDatabase && diag.RecommendedAction != RecommendedActionKind.NoAction)
            return db.IsProblematic
                ? "→ Banco de sistema: tente o reparo no modo seguro; se não bastar, siga o procedimento manual com o DBA."
                : "→ Banco de sistema: sem correção automática — siga o procedimento manual com o DBA.";

        return diag.RecommendedAction switch
        {
            RecommendedActionKind.NoAction => "✔ Nenhuma ação necessária — banco íntegro.",
            RecommendedActionKind.RebuildIndexesOnly => "→ Ação recomendada: reparar índices (sem perda de dados).",
            RecommendedActionKind.SafeRepair when db.StateDesc.Trim().Equals("RECOVERING", StringComparison.OrdinalIgnoreCase)
                => "→ Ação recomendada: aguardar a recuperação automática terminar.",
            RecommendedActionKind.SafeRepair => "→ Ação recomendada: reparo no modo seguro (sem perda de dados).",
            RecommendedActionKind.RestoreFromBackup => "→ Ação recomendada: restaurar de backup.",
            RecommendedActionKind.PageRestore => "→ Ação recomendada: restaurar só as páginas afetadas (em 'Mais opções de restauração').",
            RecommendedActionKind.RebuildLog => "→ Ação recomendada: recuperar o log de transações — comece pelo reparo no modo seguro.",
            RecommendedActionKind.FixConstraints when diag.ForeignKeyViolations.Count == 0
                => "→ Sem correção automática: revise os dados com o fornecedor/DBA ou restaure de backup.",
            RecommendedActionKind.FixConstraints => "→ Ação recomendada: corrigir a integridade (registros órfãos vão para a quarentena).",
            RecommendedActionKind.FreeDiskSpace => "→ Ação recomendada: liberar espaço em disco e religar o banco.",
            RecommendedActionKind.ResolveLogFull => "→ Ação recomendada: resolver o log de transações cheio (sem perda de dados).",
            RecommendedActionKind.FixFileAccess => "→ Ação recomendada: corrigir o acesso aos arquivos do banco e religar (sem perda de dados).",
            RecommendedActionKind.FixAccess => "→ Ação recomendada: liberar o acesso ao banco (sem perda de dados).",
            _ => "→ Ação recomendada: reparo no modo seguro."
        };
    }

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
        RecommendedActionKind.FixAccess => ("WarnBackBrush", "WarnForeBrush"),
        _ => ("WarnBackBrush", "WarnForeBrush")
    };

    /// <summary>Mostra no painel de diagnóstico as novidades desde a última verificação e os achados de causa-raiz e de política de backup.</summary>
    private void PopulateDiagnosisFindings(DiagnosisResult diag)
    {
        NoveltyList.ItemsSource = diag.NewSinceLastRun;
        NoveltyPanel.Visibility = diag.NewSinceLastRun.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        var findings = new List<PolicyFinding>();
        findings.AddRange(diag.RootCauseFindings);          // causa-raiz primeiro (mais crítico)
        if (diag.BackupPolicy is not null) findings.AddRange(diag.BackupPolicy.Findings);
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

    /// <summary>
    /// Executa uma ação de serviço NÃO destrutiva com o mesmo ciclo das demais (busy, resultado, estado).
    /// <paramref name="onResult"/> decide o que o resultado faz com o fluxo (padrão: correção do Passo 2).
    /// </summary>
    private async Task RunSimpleActionAsync(DatabaseInfo db, string status, Func<CancellationToken, Task<RepairResult>> action, string errorTitle,
        Action<RepairResult>? onResult = null)
    {
        if (_busy) return;
        SetBusy(true);
        ResultPanel.Visibility = Visibility.Collapsed;
        StatusText.Text = status;
        try
        {
            var result = await action(_cts!.Token);
            (onResult ?? (r => HandleRepairResult(db, r)))(result);
            StatusText.Text = result.Message;
        }
        catch (OperationCanceledException) { StatusText.Text = "Operação cancelada."; }
        catch (Exception ex) { StatusText.Text = errorTitle; ShowError(errorTitle, ex); }
        finally { SetBusy(false); RefreshStateLabels(db); }
    }

    // ------------------------------------------------------------------ Acesso travado — sem perda

    private void ReleaseAccessButton_Click(object sender, RoutedEventArgs e)
    {
        if (DatabasesGrid.SelectedItem is not DatabaseInfo db) return;
        var disconnect = AccessDisconnectCheck.IsChecked == true;
        if (disconnect)
        {
            var holders = _lastDiagnosis?.AccessHolders ?? new List<DbSessionInfo>();
            if (holders.Count > 0)
            {
                var confirm = MessageBox.Show(
                    $"Liberar o acesso a \"{db.Name}\" desconectando quem está preso a ele?\n\n" +
                    string.Join("\n", holders.Take(8).Select(h => "• " + h.Display)) +
                    "\n\nO que essas sessões não tinham gravado é desfeito pelo SQL Server; os dados já gravados ficam. Continuar?",
                    "Confirmar liberação do acesso", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);
                if (confirm != MessageBoxResult.Yes) return;
            }
        }
        _ = RunSimpleActionAsync(db, $"Liberando o acesso a \"{db.Name}\"...",
            ct => _adjustmentService.ReleaseAccessAsync(db, disconnect, _connectionString, _progress, ct),
            "Não foi possível liberar o acesso.");
    }

    // ------------------------------------------------------------------ Ajustes recomendados — sem perda

    /// <summary>Linha da lista de ajustes (o checkbox decide o que será aplicado).</summary>
    public sealed class AdjustmentRow
    {
        public AdjustmentRow(HealthIssue issue)
        {
            Issue = issue;
            // ReadOnly pode ser proposital (banco de arquivo): fica desmarcado até o usuário decidir.
            IsSelected = issue.CanFix && issue.Kind != HealthIssueKind.ReadOnly;
        }

        public HealthIssue Issue { get; }
        public bool IsSelected { get; set; }
        public bool CanFix => Issue.CanFix;
        public bool IsInfoOnly => !Issue.CanFix;
        public string Title => Issue.Title;
        public string Detail => Issue.Detail;
        public bool HasItems => Issue.Items.Count > 0;
        public string ItemsText => string.Join("\n", Issue.Items.Take(8)) + (Issue.Items.Count > 8 ? $"\n… e mais {Issue.Items.Count - 8}" : string.Empty);
        public string FixText => Issue.CanFix ? "→ Correção: " + Issue.FixLabel : "Apenas aviso — depende do fornecedor do sistema ou do DBA.";
    }

    private void ShowAdjustments(DiagnosisResult diag)
    {
        if (diag.Adjustments.Count == 0)
        {
            AdjustmentsPanel.Visibility = Visibility.Collapsed;
            AdjustmentsList.ItemsSource = null;
            return;
        }
        AdjustmentsList.ItemsSource = diag.Adjustments.Select(a => new AdjustmentRow(a)).ToList();
        ApplyAdjustmentsButton.Visibility = diag.Adjustments.Any(a => a.CanFix) ? Visibility.Visible : Visibility.Collapsed;
        AdjustmentsPanel.Visibility = Visibility.Visible;
    }

    private void ApplyAdjustmentsButton_Click(object sender, RoutedEventArgs e)
    {
        if (DatabasesGrid.SelectedItem is not DatabaseInfo db) return;
        var rows = (AdjustmentsList.ItemsSource as IEnumerable<AdjustmentRow>)?.Where(r => r.CanFix && r.IsSelected).ToList() ?? new();
        if (rows.Count == 0)
        {
            ShowError("Marque pelo menos um ajuste para aplicar.", new InvalidOperationException("Nenhum ajuste selecionado."));
            return;
        }
        var confirm = MessageBox.Show(
            $"Aplicar {rows.Count} ajuste(s) em \"{db.Name}\"?\n\n" + string.Join("\n", rows.Select(r => "• " + r.Issue.FixLabel)) +
            "\n\nNenhum ajuste altera dados. Alguns (reconstruir índices, reorganizar o log) podem demorar em bancos grandes.",
            "Confirmar ajustes", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);
        if (confirm != MessageBoxResult.Yes) return;

        var kinds = rows.Select(r => r.Issue.Kind).ToList();
        _ = RunSimpleActionAsync(db, $"Aplicando ajustes em \"{db.Name}\"...",
            ct => _adjustmentService.ApplyAsync(db, kinds, _connectionString, _progress, ct),
            "Não foi possível aplicar os ajustes.",
            r =>
            {
                // Ajustes não corrigem corrupção/órfãos/log: se há outra correção na tela, ela continua
                // pendente — só os ajustes saem. Sozinhos, eles concluem o Passo 2 como qualquer correção.
                var otherFix = new UIElement[]
                {
                    AccessPanel, IndexRepairPanel, LogFullPanel, FileAccessPanel, SafeRepairPanel,
                    ConstraintFixPanel, RestorePanel, DataLossPanel, LogRebuildPanel
                }.Any(p => p.Visibility == Visibility.Visible);
                if (otherFix && r.Status == RepairStatus.Success)
                {
                    ShowResult(r.Message + " A correção principal continua pendente no Passo 2.", ResultTone.Ok);
                    AdjustmentsPanel.Visibility = Visibility.Collapsed;
                }
                else
                {
                    HandleRepairResult(db, r);
                }
            });
    }

    // ------------------------------------------------------------------ Contadores de espaço / verificação em partes

    private void UpdateUsageButton_Click(object sender, RoutedEventArgs e)
    {
        if (DatabasesGrid.SelectedItem is not DatabaseInfo db) return;
        _ = RunSimpleActionAsync(db, $"Corrigindo os contadores de espaço de \"{db.Name}\"...",
            ct => _maintenanceService.UpdateUsageAsync(db, _connectionString, _progress, ct),
            "Não foi possível corrigir os contadores de espaço.", ShowToolResult);
    }

    private void UpdateIncrementalStatus(DatabaseInfo db)
    {
        var st = _incrementalService.Load(db.Name);
        IncrementalStatusText.Text = st.LastRunUtc is null
            ? "Ainda não verificado em partes."
            : (st.NextIndex == 0
                ? $"Último ciclo completo em {st.LastCycleCompletedUtc?.ToLocalTime():dd/MM/yyyy HH:mm}" +
                  (st.Errors.Count > 0 ? $" — {st.Errors.Count} problema(s)." : " — sem erros.") + " A próxima rodada inicia um novo ciclo."
                : $"Ciclo em andamento: próxima é a tabela {st.NextIndex + 1} (última rodada em {st.LastRunUtc?.ToLocalTime():dd/MM HH:mm})" +
                  (st.Errors.Count > 0 ? $" — {st.Errors.Count} problema(s) até agora." : "."));
    }

    private void IncrementalCheckButton_Click(object sender, RoutedEventArgs e)
    {
        if (DatabasesGrid.SelectedItem is not DatabaseInfo db) return;
        var minutes = (IncrementalBudgetCombo.SelectedItem as ComboBoxItem)?.Tag is string tag && int.TryParse(tag, out var m) ? m : 30;
        _ = RunIncrementalCheckAsync(db, TimeSpan.FromMinutes(minutes));
    }

    private async Task RunIncrementalCheckAsync(DatabaseInfo db, TimeSpan budget)
    {
        if (_busy) return;
        SetBusy(true);
        StatusText.Text = $"Verificando \"{db.Name}\" em partes (até {budget.TotalMinutes:0} min)...";
        try
        {
            var o = await _incrementalService.RunAsync(db, _connectionString, budget, _progress, _cts!.Token);
            ShowResult(o.Message, o.Errors.Count == 0 ? ResultTone.Ok : ResultTone.Warn);
            StatusText.Text = o.Message;
        }
        catch (OperationCanceledException) { StatusText.Text = "Verificação em partes interrompida — o progresso foi guardado."; }
        catch (Exception ex) { StatusText.Text = "Falha na verificação em partes."; ShowError("Não foi possível verificar em partes.", ex); }
        finally { SetBusy(false); UpdateIncrementalStatus(db); }
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
            var ok = rep.Executed && !rep.HasErrors;
            ShowResult(!rep.Executed
                    ? $"Não foi possível verificar {table} (banco/objeto inacessível)."
                    : ok ? $"{table}: sem erros de consistência." : $"{table}: {rep.ErrorCount} erro(s) — veja o registro e rode o diagnóstico completo (Passo 1).",
                ok ? ResultTone.Ok : ResultTone.Warn);
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
            ShowResult($"{n} tabela(s) com IDENTITY reajustada(s).", ResultTone.Ok);
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
            // Índices não bastaram: o próximo degrau é o modo seguro (sem perda) — nunca direto o reparo com perda.
            if (result.Status != RepairStatus.Success && !db.IsSystemDatabase)
            {
                SafeRepairPanel.Visibility = Visibility.Visible;
                ViewScriptButton.Visibility = Visibility.Visible;
            }
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
                "Não = prosseguir: os órfãos remanescentes saem da tabela e vão para a quarentena (RestoreDB_Quarentena).";
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

        // Fiel a ReconcileForeignKeyOrphansAsync: recupera o pai do backup e MOVE o resto para a quarentena;
        // só exclui de vez se a quarentena não puder ser usada naquela tabela.
        var confirm = MessageBox.Show(
            $"Corrigir a integridade do banco \"{db.Name}\" retirando os registros órfãos?\n\n" +
            "1) Se houver um backup completo, a ferramenta tenta primeiro recuperar dele os registros-pai que " +
            "faltam (melhor esforço — pode não encontrar todos).\n" +
            "2) Os órfãos que sobrarem SAEM da tabela e ficam guardados na QUARENTENA (banco RestoreDB_Quarentena), " +
            "de onde podem ser devolvidos depois. Se a quarentena não puder ser usada numa tabela, as linhas dela " +
            "são excluídas (o registro avisa).\n\n" +
            "Antes, a ferramenta tenta um backup de segurança. Quem estiver usando o banco é desconectado durante a correção.\n\n" +
            "Restaurar de backup é a alternativa, mas desfaz o que foi gravado depois do backup. Deseja continuar?",
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
        if (DatabasesGrid.SelectedItem is not DatabaseInfo) return;

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

        // RestoreDatabaseAsync com UMA peça: sem tail-log — o que veio depois desse backup não volta.
        var confirm = MessageBox.Show(
            $"Restaurar o banco \"{db.Name}\" a partir do backup selecionado?\n\n" +
            $"{set.Display}\n\n" +
            (RestoreReplaceCheck.IsChecked == true
                ? "O banco atual será SUBSTITUÍDO (REPLACE) pelos dados do backup. Tudo o que foi gravado DEPOIS desse " +
                  "backup será PERDIDO (este modo não salva as transações recentes). Para preservá-las, use " +
                  "'Restaurar cadeia completa'. Quem estiver usando o banco é desconectado."
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
            ShowResult(result.Message, result.Status == RepairStatus.Success ? ResultTone.Ok : ResultTone.Warn);
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
    {
        var on = PointInTimeCheck.IsChecked == true;
        PointInTimeRow.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        // A marcação fica dentro de "Mais opções", mas muda o botão principal: o rótulo não pode dizer "mais recente".
        RestoreChainButton.Content = on
            ? "Restaurar cadeia completa (até o ponto escolhido em 'Mais opções')"
            : "Restaurar cadeia completa (mais recente)";
    }

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
        var alvo = target.HasValue ? $"até {target.Value:dd/MM/yyyy HH:mm:ss}" : "até o ponto mais recente";
        // RestoreToPointInTimeAsync: tail-log é tentativa (SIMPLE/log ilegível = sem ele) e, com alvo, o que veio
        // depois dele sai do banco (fica só no arquivo de tail-log).
        var confirm = MessageBox.Show(
            $"Restaurar o banco \"{db.Name}\" pela CADEIA COMPLETA {alvo}?\n\n" +
            "A ferramenta monta automaticamente backup completo → diferencial → backups de log do histórico (msdb) e " +
            "confere que a sequência não tem buracos ANTES de mexer no banco. Em seguida tenta salvar as transações " +
            "mais recentes (tail-log) e SUBSTITUI o banco atual. Quem estiver usando o banco é desconectado.\n\n" +
            (target.HasValue
                ? "Tudo o que foi gravado depois desse instante deixa de existir no banco (fica guardado só no arquivo de tail-log).\n\n"
                : "Se o tail-log não puder ser salvo (modelo SIMPLE ou log danificado), o que foi gravado depois do último backup se perde.\n\n") +
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
            var ok = result.Status == RepairStatus.Success;
            ShowResult(result.Message, ok ? ResultTone.Ok : ResultTone.Problem);
            StatusText.Text = result.Message;
            if (ok)
            {
                var copyName = db.Name + "_Recuperado";
                await MergeDatabaseListAsync(); // a cópia aparece na lista sem desmarcar o banco atual
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
            ShowResult(result.Message, result.Status == RepairStatus.Success ? ResultTone.Ok : ResultTone.Problem);
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
            var allOk = items.Count > 0 && ok == items.Count;
            ShowResult(items.Count == 0
                    ? "Sem histórico de backups para verificar."
                    : $"Saúde dos backups: {ok}/{items.Count} verificado(s) com sucesso (detalhes no registro abaixo).",
                allOk ? ResultTone.Ok : ResultTone.Warn);
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

        // RestoreToStandbyAsync não captura tail-log: avisar sem rodeios o que se perde.
        var confirm = MessageBox.Show(
            $"Restaurar \"{db.Name}\" em modo STANDBY (somente leitura) " +
            (standbyTarget.HasValue ? $"até {standbyTarget.Value:dd/MM/yyyy HH:mm:ss}" : "até o backup de log mais recente") + "?\n\n" +
            "O banco fica CONSULTÁVEL (só leitura) para você inspecionar os dados e achar o ponto certo; o sistema " +
            "não consegue gravar nele até 'Finalizar recuperação'.\n\n" +
            "ATENÇÃO: o banco atual é SUBSTITUÍDO pelo conteúdo dos backups e este modo NÃO salva as transações " +
            "recentes (tail-log): o que foi gravado depois do último backup de log é PERDIDO. Para inspecionar sem " +
            "perder nada, prefira 'Recuperar para cópia'.\n\nDeseja continuar?",
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
            ShowToolResult(r); // ferramenta: não conclui o fluxo de correção
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
            $"Restaurar \"{db.Name}\" até {target:dd/MM/yyyy HH:mm:ss} (logo ANTES da {tx.Kind} de {begin:HH:mm:ss})?\n\n" +
            "O banco será SUBSTITUÍDO pela cadeia de backups até esse instante: TUDO o que foi gravado depois dele " +
            "deixa de existir no banco (fica guardado só no arquivo de tail-log). Exige modelo FULL e a cadeia disponível. " +
            "Para devolver só as linhas excluídas, sem desfazer o resto, use 'Devolver só as linhas excluídas'.\n\nContinuar?",
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
            $"Devolver à tabela {tx.Table} de \"{db.Name}\" as linhas que existiam em {target:dd/MM/yyyy HH:mm:ss} (logo ANTES do DELETE de {begin:HH:mm:ss})?\n\n" +
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
                await MergeDatabaseListAsync();
                _salvageCopyName = result.BackupFile;
                SalvageHintText.Text = $"Cópia [{result.BackupFile}] (estado de antes da exclusão) mantida. Selecione uma tabela e traga de volta ao banco \"{db.Name}\" as linhas que faltam.";
                await PopulateSalvageTablesAsync(result.BackupFile);
                SalvagePanel.Visibility = Visibility.Visible;
                ShowSalvageAssistant();
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
            var ok = r.Status == RepairStatus.Success;
            ShowResult(r.Message, ok ? ResultTone.Ok : ResultTone.Problem);
            StatusText.Text = r.Message;
            if (ok) await MergeDatabaseListAsync(); // o banco anexado aparece na lista
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
            "preservar as transações mais recentes (tail-log) e criar um backup de segurança; se o reparo " +
            "terminar bem, um backup FULL novo é gerado. Os arquivos ficam na pasta padrão de backups do servidor. " +
            "Quem estiver usando o banco é desconectado.\n\n" +
            (RecoverLostRowsCheck.IsChecked == true
                ? "Antes do reparo, as linhas ainda legíveis das tabelas danificadas são copiadas para um banco de " +
                  "salvamento e, depois, as que o reparo descartar são DEVOLVIDAS (também a partir dos backups, se a " +
                  "cadeia chegar até o instante do reparo). O que não puder ser lido nem estiver em backup fica perdido.\n\n"
                : "Você DESMARCOU 'Salvar e devolver as linhas': o que o reparo descartar não será devolvido.\n\n") +
            "Se o reparo deixar registros órfãos (chave estrangeira sem o registro-pai), a ferramenta tenta " +
            "recuperar o pai do backup e move o que sobrar para a quarentena (RestoreDB_Quarentena).\n\n" +
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
            if (!string.IsNullOrEmpty(result.SalvageDatabase))
            {
                // Linhas guardadas antes do reparo que não voltaram sozinhas: oferece o assistente de salvamento.
                await MergeDatabaseListAsync();
                _salvageCopyName = result.SalvageDatabase;
                SalvageHintText.Text = $"Cópia de salvamento [{result.SalvageDatabase}] (linhas legíveis de antes do reparo) mantida. " +
                                       $"Selecione uma tabela e traga de volta ao banco \"{db.Name}\" as linhas que faltam. " +
                                       "Tabelas sem chave primária e as linhas parciais (tabela__parcial) precisam de revisão do DBA.";
                await PopulateSalvageTablesAsync(result.SalvageDatabase);
                SalvagePanel.Visibility = Visibility.Visible;
                ShowSalvageAssistant();
            }
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Operação cancelada.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Falha no reparo.";
            // Mesmo em falha inesperada, oferecer o último recurso (reconstruir o log) p/ bancos de usuário.
            ShowResult("Ocorreu uma falha durante o reparo: " + ex.Message, ResultTone.Problem);
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
        // Se o diagnóstico culpou só os DADOS (log íntegro), recriar o log não resolve — e destrói o log bom.
        var onlyDataBlamed = _lastDiagnosis is { DataCorruptionSuspected: true, LogCorruptionSuspected: false };
        var applies = force || (state is "SUSPECT" or "RECOVERY_PENDING" or "EMERGENCY" && !onlyDataBlamed);
        if (db.IsSystemDatabase || !applies)
        {
            LogRebuildPanel.Visibility = Visibility.Collapsed;
            return;
        }
        LogRebuildPanel.Visibility = Visibility.Visible;
        LogRebuildConfirmCheck.IsChecked = false;
        LogRebuildButton.IsEnabled = false;
    }

    private enum ResultTone { Ok, Warn, Problem }

    /// <summary>Mostra o "Resultado da última operação" com a cor do desfecho (sem mexer no fluxo).</summary>
    private void ShowResult(string text, ResultTone tone)
    {
        var (back, line, fore, icon) = tone switch
        {
            ResultTone.Ok => ("OkBackBrush", "OkForeBrush", "OkForeBrush", "✔"),
            ResultTone.Warn => ("WarnBackBrush", "WarnLineBrush", "WarnForeBrush", "⚠"),
            _ => ("ProblemBackBrush", "ProblemForeBrush", "ProblemForeBrush", "✖")
        };
        ResultPanel.Visibility = Visibility.Visible;
        ResultText.Text = text;
        ResultIcon.Text = icon;
        ResultPanel.SetResourceReference(Border.BackgroundProperty, back);
        ResultPanel.SetResourceReference(Border.BorderBrushProperty, line);
        ResultText.SetResourceReference(TextBlock.ForegroundProperty, fore);
        ResultIcon.SetResourceReference(TextBlock.ForegroundProperty, fore);
    }

    private void ResultCloseButton_Click(object sender, RoutedEventArgs e) => ResultPanel.Visibility = Visibility.Collapsed;

    private static ResultTone ToneOf(RepairResult r) => r.Status switch
    {
        RepairStatus.Success or RepairStatus.NoActionNeeded => ResultTone.Ok,
        RepairStatus.NeedsDataLossConfirmation => ResultTone.Warn,
        _ => ResultTone.Problem
    };

    /// <summary>
    /// Resultado de uma FERRAMENTA ADICIONAL (manutenção, contadores...): só informa. Não conclui os
    /// passos nem esconde as opções de correção — ela não resolve o problema que o diagnóstico encontrou.
    /// </summary>
    private void ShowToolResult(RepairResult result) => ShowResult(result.Message, ToneOf(result));

    /// <summary>Resultado de uma correção do fluxo (Passo 2): atualiza as opções e o indicador de passos.</summary>
    private void HandleRepairResult(DatabaseInfo db, RepairResult result)
    {
        ShowResult(result.Message, ToneOf(result));

        switch (result.Status)
        {
            case RepairStatus.Success:
            case RepairStatus.NoActionNeeded:
                // O diagnóstico permanece disponível: recomenda-se re-verificar após o reparo (Passo 3).
                _repairSucceeded = true;
                _needsDataLossDecision = false;
                RequestTab(FlowTab.Confirm);
                DiagnosePanel.Visibility = Visibility.Visible;
                DiagnosisResultPanel.Visibility = Visibility.Collapsed;
                HideOptionPanels();
                TopologyWarnPanel.Visibility = Visibility.Collapsed;
                break;

            case RepairStatus.NeedsDataLossConfirmation:
                _needsDataLossDecision = true;
                // Oferece o reparo destrutivo, exigindo confirmação explícita.
                DataLossPanel.Visibility = Visibility.Visible;
                DataLossConfirmCheck.IsChecked = false;
                DataLossRepairButton.IsEnabled = false;
                // A alternativa sem descartar dados corrompidos (restaurar de backup) fica visível
                // LADO A LADO com a confirmação de perda — o usuário decide vendo as duas.
                _ = ShowRestoreOptionAsync(db);
                // E o último recurso (reconstruir o log) quando o estado indicar problema de log.
                ShowLogRebuildOption(db);
                break;

            default: // Failed
                // Irreparável: oferecer o último recurso (reconstruir o log), quando se aplica.
                ShowLogRebuildOption(db);
                break;
        }
    }

    /// <summary>
    /// Esconde as opções de correção do Passo 2 (o indicador de passos é recalculado em UpdateFlowState).
    /// O assistente de salvamento fica: a cópia recuperada continua útil depois de um reparo.
    /// </summary>
    private void HideOptionPanels()
    {
        foreach (var p in new UIElement[]
                 {
                     AccessPanel, IndexRepairPanel, LogFullPanel, FileAccessPanel, SafeRepairPanel, AdjustmentsPanel,
                     ConstraintFixPanel, RestorePanel, DataLossPanel, LogRebuildPanel
                 })
            p.Visibility = Visibility.Collapsed;
        ViewScriptButton.Visibility = Visibility.Collapsed;
        RepairOptionsTitle.Visibility = Visibility.Collapsed;
    }

    // ------------------------------------------------------------------ Último recurso: reconstruir log

    private void LogRebuildConfirmCheck_Changed(object sender, RoutedEventArgs e)
        => LogRebuildButton.IsEnabled = LogRebuildConfirmCheck.IsChecked == true && !_busy;

    private void LogRebuildButton_Click(object sender, RoutedEventArgs e)
    {
        if (DatabasesGrid.SelectedItem is not DatabaseInfo db) return;

        var confirm = MessageBox.Show(
            $"ÚLTIMO RECURSO para o banco \"{db.Name}\".\n\n" +
            "O banco será desanexado, o arquivo de log (.ldf) sai do lugar (fica guardado com a extensão " +
            ".corrupt_DATA) e um log novo é criado para forçar o retorno a ONLINE. Antes, a ferramenta tenta " +
            "copiar os arquivos para o lado.\n\n" +
            "Pode haver PERDA DE DADOS (o que estava só no log) e inconsistências. Exige executar a ferramenta " +
            "como Administrador no próprio servidor — sem isso, nada é alterado. Continuar?",
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

    /// <summary>Abre o Explorer na pasta do log, com o arquivo do dia selecionado (se já existir).</summary>
    private void LogPathLink_Click(object sender, RoutedEventArgs e)
    {
        var path = _logService.LogFilePath;
        try
        {
            var args = File.Exists(path) ? $"/select,\"{path}\"" : $"\"{Path.GetDirectoryName(path)}\"";
            System.Diagnostics.Process.Start("explorer.exe", args);
        }
        catch (Exception ex)
        {
            ShowError("Não foi possível abrir a pasta do log.", ex);
        }
    }

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
        // O filtro poderia esconder (e desmarcar) o banco em operação, resetando os painéis no meio dela.
        DbFilterBox.IsEnabled = !busy;
        CancelButton.IsEnabled = busy;
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;

        var db = DatabasesGrid.SelectedItem as DatabaseInfo;
        // Qualquer banco selecionado pode ser diagnosticado (mesmo ONLINE aparentemente saudável).
        DiagnoseButton.IsEnabled = !busy && db is not null;
        ConfirmDiagnoseButton.IsEnabled = !busy && db is not null;
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
        ReleaseAccessButton.IsEnabled = !busy;
        ApplyAdjustmentsButton.IsEnabled = !busy;
        UpdateUsageButton.IsEnabled = !busy;
        IncrementalCheckButton.IsEnabled = !busy;

        RepairBusyBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;

        UpdateFlowState();
    }

    // ------------------------------------------------------------------ Fluxo guiado (1 Diagnosticar → 2 Resolver → 3 Conferir)

    private enum StepState { Pending, Active, Done }

    /// <summary>
    /// Deriva do estado dos painéis em que passo o usuário está e atualiza o indicador de passos,
    /// a dica "Próximo passo" e o aviso do Passo 2 quando ainda não há opções. Não muda a lógica de
    /// liberação das ações — só a reflete. Chamado ao trocar de banco e ao fim de cada operação (SetBusy).
    /// </summary>
    private void UpdateFlowState()
    {
        if (DatabasesGrid.SelectedItem is not DatabaseInfo db)
        {
            // Sem banco: nada a diagnosticar nem ferramenta em que aplicar — tudo bloqueado.
            foreach (var tab in _flowTabs)
            {
                tab.IsEnabled = false;
                tab.ToolTip = _flowTabTips[tab];
            }
            TabDiagnose.IsChecked = true;
            RenderStep(Step1Circle, Step1Num, Step1Label, 1, StepState.Pending);
            RenderStep(Step2Circle, Step2Num, Step2Label, 2, StepState.Pending);
            RenderStep(Step3Circle, Step3Num, Step3Label, 3, StepState.Pending);
            RenderConnector(StepLine1, done: false);
            RenderConnector(StepLine2, done: false);
            return;
        }

        var anyOption = new UIElement[]
        {
            AccessPanel, IndexRepairPanel, LogFullPanel, FileAccessPanel, SafeRepairPanel, AdjustmentsPanel,
            ConstraintFixPanel, RestorePanel, SalvagePanel, DataLossPanel, LogRebuildPanel
        }.Any(p => p.Visibility == Visibility.Visible);
        var onlyAdjustments = AdjustmentsPanel.Visibility == Visibility.Visible && new UIElement[]
        {
            AccessPanel, IndexRepairPanel, LogFullPanel, FileAccessPanel, SafeRepairPanel,
            ConstraintFixPanel, RestorePanel, SalvagePanel, DataLossPanel, LogRebuildPanel
        }.All(p => p.Visibility != Visibility.Visible);
        var diag = _lastDiagnosis;
        var diagnosed = diag is not null;

        RepairOptionsTitle.Visibility = anyOption ? Visibility.Visible : Visibility.Collapsed;
        Step2LockedPanel.Visibility = anyOption ? Visibility.Collapsed : Visibility.Visible;
        DiagnoseButton.Content = diagnosed || _repairSucceeded || _diagnosisFailed
            ? "🔍  Diagnosticar novamente (sem alterar o banco)"
            : "🔍  Diagnosticar (sem alterar o banco)";

        StepState s1, s2, s3;
        string hint;
        if (_repairSucceeded)
        {
            (s1, s2, s3) = (StepState.Done, StepState.Done, StepState.Active);
            hint = "Operação concluída. Na aba \"Conferir\", rode o diagnóstico de novo para confirmar que o banco ficou íntegro.";
            Step2LockedText.Text = "✔  Operação concluída. Confira o resultado na aba \"Conferir\".";
        }
        else if (_diagnosisFailed && !diagnosed)
        {
            (s1, s2, s3) = (StepState.Active, StepState.Pending, StepState.Pending);
            hint = "O diagnóstico não terminou — veja o motivo no registro abaixo e clique em \"Diagnosticar novamente\".";
            Step2LockedText.Text = "🔒  Sem um diagnóstico completo, a ferramenta não indica correções.";
        }
        else if (!diagnosed)
        {
            (s1, s2, s3) = (StepState.Active, StepState.Pending, StepState.Pending);
            // Antes do diagnóstico, o que ele vai resolver depende do estado (o cabeçalho já diz qual é).
            hint = db.IsHealthy && db.HasAccessRestriction
                ? "Clique em \"Diagnosticar\" na aba 1: ele mostra a causa do travamento e a correção sem perda de dados. É somente leitura."
                : db.IsProblematic
                    ? $"Clique em \"Diagnosticar\" na aba 1 (somente leitura). Caminho provável: {char.ToLower(db.RecommendedAction[0])}{db.RecommendedAction[1..]}"
                    : "Clique em \"Diagnosticar\" na aba 1. É somente leitura — nada é alterado no banco.";
            Step2LockedText.Text = "🔒  As opções de correção aparecem aqui depois do diagnóstico — só as que se aplicam ao problema encontrado.";
        }
        else if (anyOption)
        {
            (s1, s2, s3) = (StepState.Done, StepState.Active, StepState.Pending);
            hint = onlyAdjustments && diag!.RecommendedAction == RecommendedActionKind.NoAction
                ? "Nenhum problema grave. Há ajustes recomendados na aba \"Resolver\" — opcionais e sem perda de dados."
                : NextStepHint(diag!, db);
        }
        else if (diag!.RecommendedAction == RecommendedActionKind.NoAction)
        {
            (s1, s2, s3) = (StepState.Done, StepState.Done, StepState.Done);
            hint = "Nenhuma ação necessária: o diagnóstico não encontrou nada a corrigir.";
            Step2LockedText.Text = "✔  Nada a corrigir neste banco.";
        }
        else
        {
            // Problema encontrado, mas sem correção automática nesta ferramenta (banco de sistema, só
            // violação de CHECK...): o Passo 2 NÃO está concluído — depende de alguém.
            (s1, s2, s3) = (StepState.Done, StepState.Active, StepState.Pending);
            hint = db.IsSystemDatabase
                ? "Banco de sistema com problema: a correção é manual. Clique em \"Ver procedimento manual\" e acione o DBA/suporte."
                : "O diagnóstico encontrou um problema sem correção automática nesta ferramenta. Leia o resultado na aba \"Diagnosticar\" e acione o DBA/suporte.";
            Step2LockedText.Text = "⚠  Sem correção automática para este caso — veja o resultado do diagnóstico e acione o DBA/suporte.";
        }

        if (_busy)
            hint = "Operação em andamento — acompanhe no registro abaixo. Para interromper, use \"Cancelar operação\" na barra inferior.";

        FlowHintText.Text = hint;
        RenderStep(Step1Circle, Step1Num, Step1Label, 1, s1);
        RenderStep(Step2Circle, Step2Num, Step2Label, 2, s2);
        RenderStep(Step3Circle, Step3Num, Step3Label, 3, s3);
        RenderConnector(StepLine1, s1 == StepState.Done);
        RenderConnector(StepLine2, s2 == StepState.Done);
        UpdateConfirmPanel();

        // Abas: 1 sempre; 2 depois do diagnóstico; 3 depois de uma correção (ou quando não há nada a
        // corrigir); Ferramentas sempre. Durante uma operação nenhuma troca de aba (nem de banco: SetBusy).
        var unlockResolve = s1 == StepState.Done;
        var unlockConfirm = s3 != StepState.Pending;
        TabDiagnose.IsEnabled = !_busy;
        TabResolve.IsEnabled = !_busy && unlockResolve;
        TabConfirm.IsEnabled = !_busy && unlockConfirm;
        TabTools.IsEnabled = !_busy;
        // Bloqueadas pela operação em andamento, o tooltip diz isso — não a regra de liberação.
        foreach (var tab in _flowTabs)
            tab.ToolTip = _busy
                ? "Aguarde a operação em andamento terminar (ou use \"Cancelar operação\" na barra inferior)."
                : _flowTabTips[tab];

        if (_busy) return; // o avanço automático espera a operação terminar

        var target = SelectedTab();
        if (_pendingTab is { } pending)
        {
            _pendingTab = null;
            // Operação lançada das Ferramentas: o usuário continua lá (os passos se atualizam mesmo assim),
            // a não ser que o pedido valha também para quem está lá.
            if (target != FlowTab.Tools || _pendingTabEvenFromTools)
                target = pending;
        }
        // Aba que deixou de estar liberada (ex.: a conferência encontrou problema): volta para a última liberada.
        if (target == FlowTab.Confirm && !unlockConfirm) target = FlowTab.Resolve;
        if (target == FlowTab.Resolve && !unlockResolve) target = FlowTab.Diagnose;
        SelectTab(target);
    }

    /// <summary>Aba Conferir: o que falta (conferir a correção) ou a confirmação de que está tudo certo.</summary>
    private void UpdateConfirmPanel()
    {
        var (text, back, line, fore) = _repairSucceeded
            ? ("A correção terminou. Falta conferir: rode o diagnóstico de novo para confirmar que o banco ficou íntegro.",
               "SelectionBrush", "PrimaryBrush", "PrimaryDarkBrush")
            : _lastCheckPhysicalOnly
                // PHYSICAL_ONLY pula as checagens lógicas do CHECKDB e o DATA_PURITY (a checagem de chaves
                // estrangeiras roda à parte e continua valendo): não dá para chamar de "conferido".
                ? ("✔  Estrutura física íntegra — mas foi a verificação rápida (PHYSICAL_ONLY), que não faz as checagens lógicas do CHECKDB nem confere os valores das colunas. Diagnostique de novo sem ela para a conferência completa.",
                   "WarnBackBrush", "WarnLineBrush", "WarnForeBrush")
                : ("✔  Conferido: o diagnóstico não encontrou nada a corrigir neste banco. Os detalhes estão na aba \"Diagnosticar\".",
                   "OkBackBrush", "OkForeBrush", "OkForeBrush");
        ConfirmStatusText.Text = text;
        ConfirmStatusPanel.SetResourceReference(Border.BackgroundProperty, back);
        ConfirmStatusPanel.SetResourceReference(Border.BorderBrushProperty, line);
        ConfirmStatusText.SetResourceReference(TextBlock.ForegroundProperty, fore);
        ConfirmSalvageText.Visibility = SalvagePanel.Visibility;
    }

    /// <summary>
    /// Ficou uma cópia com linhas a devolver: o próximo passo é o assistente de salvamento (aba Resolver),
    /// não a conferência — mesmo que a operação tenha sido lançada das Ferramentas.
    /// </summary>
    private void ShowSalvageAssistant() => RequestTab(FlowTab.Resolve, evenFromTools: true);

    /// <summary>
    /// Pede que o fluxo vá para <paramref name="tab"/> quando a operação atual terminar. Por padrão quem está
    /// nas Ferramentas continua lá; <paramref name="evenFromTools"/> faz o pedido valer também para essa aba.
    /// </summary>
    private void RequestTab(FlowTab tab, bool evenFromTools = false)
    {
        _pendingTab = tab;
        _pendingTabEvenFromTools = evenFromTools;
    }

    private FlowTab SelectedTab()
    {
        var i = Array.FindIndex(_flowTabs, t => t.IsChecked == true);
        return i < 0 ? FlowTab.Diagnose : (FlowTab)i;
    }

    private void SelectTab(FlowTab tab) => _flowTabs[(int)tab].IsChecked = true;

    /// <summary>Trocar de aba mostra o conteúdo novo desde o início.</summary>
    private void FlowTab_Checked(object sender, RoutedEventArgs e) => TabContentScroll?.ScrollToTop();

    /// <summary>Ligação entre os passos: verde quando o passo anterior está concluído.</summary>
    private static void RenderConnector(System.Windows.Shapes.Rectangle line, bool done)
        => line.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, done ? "OkForeBrush" : "LineBrush");

    /// <summary>
    /// "Próximo passo" com o NOME da opção recomendada: com vários cartões na tela (inclusive ajustes
    /// opcionais), "comece pela primeira" levava ao cartão errado.
    /// </summary>
    private string NextStepHint(DiagnosisResult diag, DatabaseInfo db)
    {
        const string riskNote = " As opções estão em ordem de risco; as que podem perder dados pedem confirmação.";
        if (_needsDataLossDecision)
            return "O reparo sem perda não bastou. Se houver backup, prefira \"Restaurar de backup\". Sem backup, use " +
                   "\"Simular antes\" e depois o \"Reparo com possível perda de dados\" (pede confirmação).";
        // O modo seguro só aparece junto da reconstrução de índices depois que ela falhou (RunRebuildIndexesAsync).
        if (SafeRepairPanel.Visibility == Visibility.Visible && diag.RecommendedAction == RecommendedActionKind.RebuildIndexesOnly)
            return "A reconstrução de índices não bastou: o próximo passo é \"Reparo no modo seguro\" (sem perda de dados).";

        return diag.RecommendedAction switch
        {
            RecommendedActionKind.FixAccess => "Comece por \"Acesso ao banco travado\": libera o acesso sem perda de dados.",
            RecommendedActionKind.RebuildIndexesOnly => "Comece por \"Reconstruir índices\" (sem perda de dados)." + riskNote,
            RecommendedActionKind.ResolveLogFull => "Comece por \"Log de transações cheio\" (sem perda de dados)." + riskNote,
            RecommendedActionKind.FixFileAccess => "Comece por \"Arquivos do banco inacessíveis\" (sem perda de dados)." + riskNote,
            RecommendedActionKind.FixConstraints when diag.ForeignKeyViolations.Count > 0
                => "Recomendado: \"Integridade lógica: registros órfãos\" (os órfãos vão para a quarentena). Restaurar de backup é a alternativa.",
            RecommendedActionKind.FixConstraints
                => "Não há correção automática para violação de CHECK: revise os dados com o fornecedor/DBA ou use \"Restaurar de backup\".",
            RecommendedActionKind.RestoreFromBackup
                => "Recomendado: \"Restaurar de backup\". \"Reparo no modo seguro\" pode ser tentado antes (sem perda), mas raramente resolve corrupção de dados." + riskNote,
            RecommendedActionKind.PageRestore
                => "Recomendado: \"Restaurar de backup\" → \"Mais opções\" → \"Só as páginas afetadas\" (as páginas já vêm preenchidas)." + riskNote,
            RecommendedActionKind.RebuildLog
                => "Comece por \"Reparo no modo seguro\". \"Reconstruir o log\" é o último recurso." + riskNote,
            RecommendedActionKind.FreeDiskSpace
                => "Primeiro libere espaço no disco do banco (arquivos antigos, backups velhos); depois use \"Reparo no modo seguro\".",
            RecommendedActionKind.SafeRepair when db.StateDesc.Trim().Equals("RECOVERING", StringComparison.OrdinalIgnoreCase)
                => "O SQL Server está recuperando o banco sozinho: aguarde e diagnostique de novo. Use o reparo só se o andamento parar.",
            _ when db.IsSystemDatabase
                => "Banco de sistema: tente \"Reparo no modo seguro\"; se não bastar, siga o procedimento manual com o DBA.",
            _ => "Comece por \"Reparo no modo seguro\" (sem perda de dados)." + riskNote
        };
    }

    private static void RenderStep(Border circle, TextBlock number, TextBlock label, int n, StepState state)
    {
        circle.SetResourceReference(Border.BackgroundProperty, state switch
        {
            StepState.Done => "OkForeBrush",
            StepState.Active => "PrimaryBrush",
            _ => "LineBrush"
        });
        number.Text = state == StepState.Done ? "✓" : n.ToString();
        number.SetResourceReference(TextBlock.ForegroundProperty, state switch
        {
            StepState.Pending => "MutedTextBrush",
            StepState.Active => "OnPrimaryBrush", // branco sobre o azul (contraste nos dois temas)
            _ => "CardBrush"                       // sobre o verde OkForeBrush
        });
        label.SetResourceReference(TextBlock.ForegroundProperty, state == StepState.Pending ? "MutedTextBrush" : "TextBrush");
        label.FontWeight = state == StepState.Active ? FontWeights.Bold : FontWeights.Normal;
    }

    /// <summary>
    /// Abas numa linha só quando cabem; senão "Ferramentas adicionais" desce para uma segunda linha e as
    /// três do fluxo continuam ligadas na primeira. A largura necessária vem da medida real das abas
    /// (colunas Auto medem sem limite, então DesiredSize é a largura natural), não de um número fixo.
    /// </summary>
    private void TabStrip_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        static double Outer(FrameworkElement el, double width) => width + el.Margin.Left + el.Margin.Right;
        var needed = _flowTabs.Sum(t => t.DesiredSize.Width)
                     + Outer(StepLine1, StepLine1.MinWidth) + Outer(StepLine2, StepLine2.MinWidth)
                     + Outer(TabsSeparator, TabsSeparator.Width);
        var narrow = e.NewSize.Width < needed;
        TabsSeparator.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetRow(TabTools, narrow ? 1 : 0);
        Grid.SetColumn(TabTools, narrow ? 0 : 6);
        Grid.SetColumnSpan(TabTools, narrow ? 7 : 1);
        TabTools.HorizontalAlignment = narrow ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
    }

    /// <summary>Ferramentas: lista e detalhe lado a lado; em painel estreito o detalhe desce para baixo da lista.</summary>
    private void ToolsGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var w = e.NewSize.Width;
        var narrow = w < 620 || (w <= 660 && Grid.GetRow(ToolDetailHost) == 1);
        Grid.SetColumnSpan(ToolsList, narrow ? 3 : 1);
        ToolsSeparator.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetRow(ToolDetailHost, narrow ? 1 : 0);
        Grid.SetColumn(ToolDetailHost, narrow ? 0 : 2);
        Grid.SetColumnSpan(ToolDetailHost, narrow ? 3 : 1);
        ToolDetailHost.Margin = new Thickness(0, narrow ? 14 : 0, 0, 0);
    }

    private static void ShowError(string title, Exception ex)
    {
        MessageBox.Show(title + "\n\nDetalhe: " + ex.Message, "RestoreDB",
            MessageBoxButton.OK, MessageBoxImage.Error);
    }
}

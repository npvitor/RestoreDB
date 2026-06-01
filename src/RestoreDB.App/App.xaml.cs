using System.Linq;
using System.Windows;
using System.Windows.Threading;
using RestoreDB.App.Views;

namespace RestoreDB.App;

public enum AppTheme { Light, Dark }

public partial class App : Application
{
    /// <summary>Tema atualmente aplicado (claro por padrão). Persiste por toda a aplicação.</summary>
    public static AppTheme CurrentTheme { get; private set; } = AppTheme.Light;

    public App()
    {
        // Última barreira contra exceções não tratadas: nunca mostrar stack trace cru.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
    }

    /// <summary>
    /// Troca o tema em runtime substituindo o 1º dicionário mesclado (o tema ativo).
    /// Todas as referências DynamicResource e SetResourceReference são reavaliadas.
    /// </summary>
    public static void ApplyTheme(AppTheme theme)
    {
        var dict = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/Themes/{theme}.xaml", UriKind.Absolute)
        };
        Current.Resources.MergedDictionaries[0] = dict;
        CurrentTheme = theme;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Modo de autoteste de XAML (diagnóstico): constrói as duas janelas e força um
        // passo de layout em AMBOS os temas, validando a resolução de todos os recursos/estilos
        // sem precisar de conexão. Encerra com código 0 (ok) ou 2 (falha). Não exibe janelas.
        if (e.Args.Contains("--xaml-selftest"))
        {
            RunXamlSelfTest();
            return;
        }

        // Início normal: tela de conexão.
        new ConnectionWindow().Show();
    }

    private void RunXamlSelfTest()
    {
        try
        {
            var connection = new ConnectionWindow();
            connection.Measure(new Size(800, 600));
            connection.Arrange(new Rect(0, 0, 800, 600));

            var main = new MainWindow("Server=localhost;Database=master;", "localhost");
            main.Measure(new Size(1100, 720));
            main.Arrange(new Rect(0, 0, 1100, 720));

            // Valida que AMBOS os temas resolvem todos os recursos (claro já está ativo).
            ApplyTheme(AppTheme.Dark);
            main.Measure(new Size(1100, 720));
            main.Arrange(new Rect(0, 0, 1100, 720));
            connection.Measure(new Size(800, 600));
            connection.Arrange(new Rect(0, 0, 800, 600));
            ApplyTheme(AppTheme.Light);

            Console.Out.WriteLine("SELFTEST-OK");
            Shutdown(0);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("SELFTEST-FAIL: " + ex.Message);
            Shutdown(2);
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            "Ocorreu um erro inesperado:\n\n" + e.Exception.Message,
            "RestoreDB", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}

using System.Linq;
using System.Windows;
using System.Windows.Threading;
using RestoreDB.App.Views;

namespace RestoreDB.App;

public partial class App : Application
{
    public App()
    {
        // Última barreira contra exceções não tratadas: nunca mostrar stack trace cru.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Modo de autoteste de XAML (diagnóstico): constrói as duas janelas e força um
        // passo de layout, validando a resolução de todos os recursos/estilos sem precisar
        // de conexão. Encerra com código 0 (ok) ou 2 (falha). Não exibe janelas.
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

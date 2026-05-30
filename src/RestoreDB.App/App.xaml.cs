using System.Windows;
using System.Windows.Threading;

namespace RestoreDB.App;

public partial class App : Application
{
    public App()
    {
        // Última barreira contra exceções não tratadas: nunca mostrar stack trace cru.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            "Ocorreu um erro inesperado:\n\n" + e.Exception.Message,
            "RestoreDB", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}

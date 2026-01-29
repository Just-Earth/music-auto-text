using System.Configuration;
using System.Data;
using System.Windows;

namespace WpfApp1
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Global exception handlers to capture silent crashes
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            this.DispatcherUnhandledException += App_DispatcherUnhandledException;
            TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
        }

        private void LogException(object? sender, Exception ex)
        {
            try
            {
                var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "WpfApp1_error.txt");
                var text = $"{System.DateTime.Now:o} - Exception: {ex}\n\n";
                System.IO.File.AppendAllText(path, text);
            }
            catch { }
        }

        private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            try { if (e.ExceptionObject is Exception ex) LogException(sender, ex); } catch { }
        }

        private void App_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            try { LogException(sender, e.Exception); } catch { }
            // allow default handling (app may still exit)
        }

        private void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            try { LogException(sender, e.Exception); } catch { }
        }
    }

}

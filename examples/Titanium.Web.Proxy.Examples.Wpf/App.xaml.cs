using System;
using System.Windows;

namespace Titanium.Web.Proxy.Examples.Wpf
{
    /// <summary>
    ///     Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        protected override void OnExit(ExitEventArgs e)
        {
            // Safety net if the main window Closed handler did not run.
            if (MainWindow is MainWindow window)
                window.EnsureProxyShutdown();

            base.OnExit(e);
        }
    }
}

using System;
using System.Threading.Tasks;
using System.Windows;

namespace Spy.UI;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var splash = new SplashWindow();
        splash.Show();

        await Task.Delay(600); // имитация загрузки

        try
        {
            var main = new MainWindow();
            MainWindow = main;
            main.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.ToString(), "Startup error");
        }
        finally
        {
            splash.Close();
        }
    }
}


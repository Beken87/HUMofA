using System.Windows;

namespace HUMofA.Desktop.Native;

/// <summary>
/// Launches the Pure C# WPF Native Desktop Application on an STA thread.
/// </summary>
public static class NativeDesktopLauncher
{
    public static Task RunNativeDesktopWindowAsync(AppCore core, string? settingsFilePath = null)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var staThread = new Thread(() =>
        {
            try
            {
                var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
                var mainWindow = new NativeMainWindow(core, settingsFilePath);
                app.MainWindow = mainWindow;
                mainWindow.Closed += (_, _) => tcs.TrySetResult();
                mainWindow.ShowDialog();
                tcs.TrySetResult();
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });

        staThread.SetApartmentState(ApartmentState.STA);
        staThread.IsBackground = false;
        staThread.Name = "HOFA-WPF-UI-Thread";
        staThread.Start();

        return tcs.Task;
    }
}


using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace OdriveWheel.SimHubPlugin
{
    /// <summary>
    /// File dialogs for the configuration window. A modal dialog on the window's thread would disable the window and
    /// its stop button (tests/plugin/WindowTests.cs), so each runs on a thread of its own. Its owner is a hidden window
    /// of that thread: without one WPF would pick the application's main window, which belongs to SimHub.
    /// </summary>
    internal static class OffThreadFileDialog
    {
        public static void Save(Dispatcher dispatcher, string title, string folder, string fileName, string filter, Action<string> picked, Action<string> log)
            => Run(dispatcher, picked, log, () => new Microsoft.Win32.SaveFileDialog
            {
                Title            = title,
                InitialDirectory = ExistingOrNull(folder),
                FileName         = fileName,
                Filter           = filter,
                OverwritePrompt  = false,   // the export refuses to replace anything and says so
                AddExtension     = true,
            });

        public static void Open(Dispatcher dispatcher, string title, string folder, string filter, Action<string> picked, Action<string> log)
            => Run(dispatcher, picked, log, () => new Microsoft.Win32.OpenFileDialog
            {
                Title            = title,
                InitialDirectory = ExistingOrNull(folder),
                Filter           = filter,
                CheckFileExists  = true,
            });

        private static string ExistingOrNull(string folder)
            => !string.IsNullOrEmpty(folder) && Directory.Exists(folder) ? folder : null;

        private static void Run(Dispatcher dispatcher, Action<string> picked, Action<string> log, Func<Microsoft.Win32.FileDialog> create)
        {
            var thread = new Thread(() =>
            {
                string result = null;
                Window owner = null;
                try
                {
                    owner = new Window { ShowInTaskbar = false, WindowStyle = WindowStyle.None, Width = 0, Height = 0 };
                    new WindowInteropHelper(owner).EnsureHandle();
                    var dialog = create();
                    if (dialog.ShowDialog(owner) == true) result = dialog.FileName;
                }
                catch (Exception ex)
                {
                    log("file dialog: " + ex.Message);
                }
                finally
                {
                    owner?.Close();
                    Dispatcher.CurrentDispatcher.InvokeShutdown();
                }
                if (result != null && !dispatcher.HasShutdownStarted)
                    dispatcher.BeginInvoke(new Action(() => picked(result)));
            })
            {
                IsBackground = true,
                Name = "OdriveFileDialog",
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }
    }
}

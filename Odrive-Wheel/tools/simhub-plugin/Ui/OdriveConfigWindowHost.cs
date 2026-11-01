using System;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace OdriveWheel.SimHubPlugin
{
    /// <summary>
    /// Runs the configuration window on a UI thread of its own. Modal dialogs disable all
    /// windows of their thread and a busy SimHub UI blocks its thread, and neither may
    /// take the software stop button with it.
    /// </summary>
    public sealed class OdriveConfigWindowHost
    {
        private readonly Func<OdriveConfigWindow> _createWindow;
        private readonly Action<string> _log;
        private readonly object _gate = new object();
        private readonly ManualResetEventSlim _shown = new ManualResetEventSlim(false);

        private Thread _thread;
        private volatile Dispatcher _dispatcher;
        private volatile OdriveConfigWindow _window;

        public OdriveConfigWindowHost(Func<OdriveConfigWindow> createWindow, Action<string> log)
        {
            _createWindow = createWindow ?? throw new ArgumentNullException(nameof(createWindow));
            _log          = log ?? (_ => { });
        }

        /// <summary>Opens the window, or brings it to the front if it is already open.
        /// Returns at once, the caller is usually SimHub's UI thread.</summary>
        public void Show()
        {
            lock (_gate)
            {
                if (_thread != null && _thread.IsAlive)
                {
                    _dispatcher?.BeginInvoke(new Action(BringToFront));
                    return;
                }

                _shown.Reset();
                _thread = new Thread(RunWindow)
                {
                    IsBackground = true,
                    Name         = "OdriveConfigWindow",
                };
                _thread.SetApartmentState(ApartmentState.STA);
                _thread.Start();
            }
        }

        /// <summary>True while the window's thread runs.</summary>
        public bool IsOpen
        {
            get
            {
                lock (_gate) return _thread != null && _thread.IsAlive;
            }
        }

        /// <summary>Waits until the window is up, for tests.</summary>
        internal bool WaitUntilShown(int timeoutMs) => _shown.Wait(timeoutMs);

        /// <summary>Runs an action on the window's thread and waits for it, for tests.</summary>
        internal void Invoke(Action<OdriveConfigWindow> action)
        {
            _dispatcher?.Invoke(() => action(_window));
        }

        /// <summary>Closes the window and ends its thread, waiting at most timeoutMs.</summary>
        public void Close(int timeoutMs)
        {
            Thread thread;
            lock (_gate) thread = _thread;
            if (thread == null || !thread.IsAlive) return;

            _dispatcher?.BeginInvokeShutdown(DispatcherPriority.Send);
            if (!thread.Join(timeoutMs)) _log("config window thread did not stop within " + timeoutMs + " ms");
        }

        private void RunWindow()
        {
            // An exception leaving this thread would end SimHub, its handlers don't see it.
            try
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                dispatcher.UnhandledException += (s, e) =>
                {
                    _log("config window error: " + e.Exception);
                    e.Handled = true;
                };
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));

                var window = _createWindow();
                window.Closed += (s, e) => dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                window.Show();

                _window = window;
                _dispatcher = dispatcher;
                _shown.Set();

                Dispatcher.Run();
            }
            catch (Exception ex)
            {
                _log("config window failed: " + ex);
            }
            finally
            {
                _dispatcher = null;
                _window = null;
            }
        }

        private void BringToFront()
        {
            var window = _window;
            if (window == null) return;
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Activate();
        }
    }
}

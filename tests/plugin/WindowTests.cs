using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;

namespace OdriveWheel.SimHubPlugin.Tests
{
    public static class WindowTests
    {
        [DllImport("user32.dll")]
        private static extern bool IsWindowEnabled(IntPtr hWnd);

        // Only detects a hang, opening a window has no time requirement. The first window of a
        // process loads all of WPF, which took over 5 s on a CI machine.
        private const int WindowStartLimitMs = 20000;

        internal sealed class WindowRig : IDisposable
        {
            public Rig Rig { get; }
            public OdriveConfigWindowHost Host { get; }
            /// <summary>Null unless asked for: most tests are about the window on its own.</summary>
            public OdriveCapabilityDetector Capabilities { get; }
            public int Created;

            public WindowRig(params Func<IFeaturePanel>[] panels) : this(null, null, panels) { }

            public WindowRig(Action<FakeBoard> prepare, IPowerSupplyStore powerSupplyStore, params Func<IFeaturePanel>[] panels)
                : this(prepare, powerSupplyStore, false, panels) { }

            public WindowRig(Action<FakeBoard> prepare, bool detectCapabilities) : this(prepare, null, detectCapabilities, new Func<IFeaturePanel>[0]) { }

            private WindowRig(Action<FakeBoard> prepare, IPowerSupplyStore powerSupplyStore, bool detectCapabilities, Func<IFeaturePanel>[] panels)
            {
                Rig = new Rig(prepare: prepare);
                if (detectCapabilities)
                {
                    Capabilities = new OdriveCapabilityDetector(Rig.Service, Rig.Parameters);
                    Capabilities.Start();
                }
                Host = new OdriveConfigWindowHost(() =>
                {
                    Interlocked.Increment(ref Created);
                    return new OdriveConfigWindow(Rig.Service, Rig.Parameters, OdriveParameterCatalog.LoadEmbedded(),
                                                  () => Rig.HidState, m => Console.WriteLine("window: " + m),
                                                  panels.Length > 0 ? panels : null, powerSupplyStore, Capabilities);
                }, m => Console.WriteLine("host: " + m));
                var watch = System.Diagnostics.Stopwatch.StartNew();
                Host.Show();
                Check.True(Host.WaitUntilShown(WindowStartLimitMs), "the window should open");
                if (watch.ElapsedMilliseconds > 2000) Console.WriteLine("note: the window took " + watch.ElapsedMilliseconds + " ms to open");
            }

            public T On<T>(Func<OdriveConfigWindow, T> read)
            {
                T result = default;
                Host.Invoke(w => result = read(w));
                return result;
            }

            public void Dispose()
            {
                Host.Close(2000);
                Capabilities?.Stop();
                Rig.Dispose();
            }
        }

        internal static void Click(Button button)
        {
            var peer = new ButtonAutomationPeer(button);
            ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
        }

        public static void Test_window_opens_and_reads_the_status_values()
        {
            using (var rig = new WindowRig())
            {
                Check.True(Wait.Until(() => rig.On(w => w.DisplayedValue("vbus_voltage")) == "48.000 V"), "vbus_voltage");
                Check.True(Wait.Until(() => rig.On(w => w.DisplayedValue("axis0.motor.error")) == "0 (no error)"), "motor error");
                Check.True(Wait.Until(() => rig.On(w => w.DisplayedValue("sys.build")) == "abc branch=test"), "build id");
                Check.True(rig.On(w => w.ConnectionText.Text).Contains("COM99"), "connection text");
            }
        }

        public static void Test_window_shows_a_refused_read_as_not_readable()
        {
            using (var rig = new WindowRig())
            {
                rig.Rig.Board.ReplyOverride = line => line == "r axis0.motor.error" ? "not implemented" : null;
                Check.True(Wait.Until(() => rig.On(w => w.DisplayedValue("axis0.motor.error")) == "not readable (not implemented)"), "motor error");
            }
        }

        public static void Test_stop_button_sends_the_stop_and_shows_the_result()
        {
            using (var rig = new WindowRig())
            {
                rig.Rig.HidState = 8;
                rig.Host.Invoke(w => Click(w.StopButton));

                Check.True(Wait.Until(() => rig.Rig.Board.Value("axis0.current_state") == "1"), "the board goes to IDLE");
                Check.True(Wait.Until(() => rig.Rig.Service.GetStatus().LastStop.IsFinished), "the stop should finish");
                Check.True(!rig.On(w => w.StopStatusText.Text).Contains("IDLE confirmed"), "not while HID still says closed loop");

                rig.Rig.HidState = 1;
                Check.True(Wait.Until(() => rig.On(w => w.StopStatusText.Text).Contains("IDLE confirmed")), "result text");
                Check.Equal(Visibility.Visible, rig.On(w => w.ClearStopButton.Visibility), "clear button appears");

                rig.Host.Invoke(w => Click(w.ClearStopButton));
                Check.True(Wait.Until(() => !rig.Rig.Service.GetStatus().StopLatched), "latch cleared");
            }
        }

        public static void Test_stop_button_warns_when_the_board_is_gone()
        {
            using (var rig = new WindowRig())
            {
                rig.Rig.Board.Present = false;
                Check.True(Wait.Until(() => !rig.Rig.Service.GetStatus().IsConnected), "disconnected");

                rig.Host.Invoke(w => Click(w.StopButton));

                Check.True(Wait.Until(() => rig.On(w => w.StopStatusText.Text).Contains("NOT DELIVERED")), "warning text");
                Check.True(rig.On(w => w.StopDisclaimerText.Text).Contains("not a hardware E-stop"), "limits are stated");
            }
        }

        public static void Test_port_button_releases_and_takes_over()
        {
            using (var rig = new WindowRig())
            {
                rig.Host.Invoke(w => Click(w.PortButton));
                Check.True(Wait.Until(() => rig.Rig.Service.GetStatus().State == SerialConnectionState.Released), "released");
                Check.True(Wait.Until(() => (string)rig.On(w => w.PortButton.Content) == "Take over port"), "button text");

                rig.Host.Invoke(w => Click(w.PortButton));
                rig.Rig.WaitConnected();
                Check.True(Wait.Until(() => (string)rig.On(w => w.PortButton.Content) == "Release port"), "button text");
            }
        }

        // Why the window has its own thread, and the proof that a modal dialog on the
        // SimHub thread cannot reach it. The second half is the negative control.
        public static void Test_modal_dialog_disables_only_the_windows_of_its_own_thread()
        {
            using (var rig = new WindowRig())
            {
                var handle = rig.On(w => new WindowInteropHelper(w).Handle);
                Check.True(IsWindowEnabled(handle), "enabled to begin with");

                // A modal dialog on another thread, like one in SimHub.
                var dialogReady = new ManualResetEventSlim(false);
                Window otherDialog = null;
                var otherThread = new Thread(() =>
                {
                    var owner = new Window { Width = 100, Height = 100, ShowInTaskbar = false };
                    owner.Show();
                    otherDialog = new Window { Width = 100, Height = 100, ShowInTaskbar = false, Owner = owner };
                    otherDialog.Loaded += (s, e) => dialogReady.Set();
                    otherDialog.ShowDialog();
                    owner.Close();
                    Dispatcher.CurrentDispatcher.InvokeShutdown();
                });
                otherThread.SetApartmentState(ApartmentState.STA);
                otherThread.IsBackground = true;
                otherThread.Start();
                Check.True(dialogReady.Wait(5000), "the other dialog should open");

                Check.True(IsWindowEnabled(handle), "a dialog on another thread leaves the window enabled");
                rig.Host.Invoke(w => Click(w.StopButton));
                Check.True(Wait.Until(() => rig.Rig.Board.Value("axis0.current_state") == "1"), "the stop button works during it");

                otherDialog.Dispatcher.BeginInvoke(new Action(() => otherDialog.Close()));
                Check.True(otherThread.Join(2000), "the other dialog should close");

                // A modal dialog on the window's own thread does disable it.
                Window ownDialog = null;
                var ownDispatcher = rig.On(w => w.Dispatcher);
                ownDispatcher.BeginInvoke(new Action(() =>
                {
                    ownDialog = new Window { Width = 100, Height = 100, ShowInTaskbar = false };
                    ownDialog.ShowDialog();
                }));
                Check.True(Wait.Until(() => !IsWindowEnabled(handle)), "a dialog on the same thread disables the window");

                ownDispatcher.BeginInvoke(new Action(() => ownDialog.Close()));
                Check.True(Wait.Until(() => IsWindowEnabled(handle)), "enabled again afterwards");
            }
        }

        public static void Test_window_keeps_working_while_the_calling_thread_is_blocked()
        {
            using (var rig = new WindowRig())
            {
                // This test thread stands in for a frozen SimHub UI: it does nothing but wait
                // for the window, which runs and reacts on its own.
                var pressed = new ManualResetEventSlim(false);
                rig.Host.Invoke(w => w.Dispatcher.BeginInvoke(new Action(() =>
                {
                    Click(w.StopButton);
                    pressed.Set();
                })));
                Check.True(pressed.Wait(2000), "the window's thread processed the click");
                Check.True(Wait.Until(() => rig.Rig.Board.Value("axis0.current_state") == "1"), "stop went out");
            }
        }

        public static void Test_second_show_keeps_one_window_and_reopen_works()
        {
            using (var rig = new WindowRig())
            {
                rig.Host.Show();
                Thread.Sleep(100);
                Check.Equal(1, rig.Created, "one window");

                rig.Host.Invoke(w => w.Close());
                Check.True(Wait.Until(() => !rig.Host.IsOpen), "the thread ends with the window");

                rig.Host.Show();
                Check.True(Wait.Until(() => rig.Created == 2), "a new window after closing");
                Check.True(rig.Host.WaitUntilShown(WindowStartLimitMs), "shown again");
            }
        }

        public static void Test_closing_ends_the_thread_and_leaves_the_service_running()
        {
            using (var rig = new WindowRig())
            {
                rig.Host.Close(2000);
                Check.True(!rig.Host.IsOpen, "thread ended");
                Check.Equal(SerialOutcome.Reply, rig.Rig.Send("r vbus_voltage").Result.Outcome, "service still works");
            }
        }

        private static OdriveConfigWindowHost HostWithHeldFactory(Rig rig, ManualResetEventSlim inFactory, ManualResetEventSlim release,
                                                                  Action<OdriveConfigWindow> created)
        {
            return new OdriveConfigWindowHost(() =>
            {
                inFactory.Set();
                release.Wait();
                var window = new OdriveConfigWindow(rig.Service, rig.Parameters, OdriveParameterCatalog.LoadEmbedded(),
                                                    () => rig.HidState, _ => { });
                created(window);
                return window;
            }, m => Console.WriteLine("host: " + m));
        }

        public static void Test_close_during_window_creation_is_not_lost()
        {
            using (var rig = new Rig())
            {
                var inFactory = new ManualResetEventSlim(false);
                var release = new ManualResetEventSlim(false);
                OdriveConfigWindow window = null;
                int loaded = 0;
                var host = HostWithHeldFactory(rig, inFactory, release, w =>
                {
                    window = w;
                    w.Loaded += (s, e) => Interlocked.Increment(ref loaded);
                });

                host.Show();
                Check.True(inFactory.Wait(WindowStartLimitMs), "the factory should be running");

                // The window doesn't exist yet, so all Close can do is leave a note.
                host.Close(30);
                release.Set();

                Check.True(Wait.Until(() => !host.IsOpen, WindowStartLimitMs), "the thread should end by itself");
                Check.True(window != null, "the factory did finish");
                Check.True(window != null && window.Dispatcher.HasShutdownFinished, "no dispatcher keeps running");
                Check.Equal(0, Volatile.Read(ref loaded), "the window never showed");
                host.Close(2000);
            }
        }

        public static void Test_close_right_after_show_leaves_nothing_running()
        {
            using (var rig = new Rig())
            {
                for (int i = 0; i < 10; i++)
                {
                    var host = new OdriveConfigWindowHost(
                        () => new OdriveConfigWindow(rig.Service, rig.Parameters, OdriveParameterCatalog.LoadEmbedded(), () => rig.HidState, _ => { }),
                        m => Console.WriteLine("host: " + m));
                    host.Show();
                    host.Close(WindowStartLimitMs);
                    Check.True(Wait.Until(() => !host.IsOpen, WindowStartLimitMs), "round " + i + ": the thread should end");
                    host.Close(2000);
                }
            }
        }

        public static void Test_show_after_close_opens_again()
        {
            using (var rig = new WindowRig())
            {
                rig.Host.Close(3000);
                Check.True(!rig.Host.IsOpen, "closed");

                rig.Host.Show();
                Check.True(Wait.Until(() => rig.Created == 2), "a new window");
                Check.True(rig.Host.WaitUntilShown(WindowStartLimitMs), "shown again");
                Check.True(rig.Host.IsOpen, "open");
            }
        }

        public static void Test_extensions_tab_is_empty_without_packages()
        {
            using (var rig = new WindowRig())
            {
                Check.Equal(0, FeaturePanels.All.Count, "no package has registered yet");
                Check.Equal(1, rig.On(w => w.ExtensionsStack.Children.Count), "only the placeholder");
                Check.Equal("No extensions installed.", rig.On(w => ((TextBlock)w.ExtensionsStack.Children[0]).Text), "placeholder");
            }
        }

        private sealed class SamplePanel : IFeaturePanel
        {
            public string Title => "Sample package";
            public FrameworkElement CreateView(IOdriveParameters parameters) => new TextBlock { Text = "sample view" };
        }

        public static void Test_a_package_adds_its_panel_with_one_factory()
        {
            using (var rig = new WindowRig(() => new SamplePanel()))
            {
                Check.Equal(2, rig.On(w => w.ExtensionsStack.Children.Count), "title and view");
                Check.Equal("Sample package", rig.On(w => ((TextBlock)w.ExtensionsStack.Children[0]).Text), "title");
                Check.Equal("sample view", rig.On(w => ((TextBlock)w.ExtensionsStack.Children[1]).Text), "view");
            }
        }

        public static void Test_panel_error_is_contained_by_the_window_thread()
        {
            var host = new OdriveConfigWindowHost(() => throw new InvalidOperationException("broken window"), _ => { });
            host.Show();
            Check.True(Wait.Until(() => !host.IsOpen), "the thread ends without taking the process down");
        }
    }
}

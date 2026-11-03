using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace OdriveWheel.SimHubPlugin
{
    /// <summary>
    /// The configuration window. It has no SimHub dependency and blocks on nothing: it sends
    /// through the async API and polls immutable status snapshots with its timers.
    /// </summary>
    public partial class OdriveConfigWindow : Window
    {
        private static readonly Brush BrushOk      = Frozen(0x2A, 0x9D, 0x8F);
        private static readonly Brush BrushWarn    = Frozen(0xE9, 0xC4, 0x6A);
        private static readonly Brush BrushDanger  = Frozen(0xE7, 0x6F, 0x51);
        private static readonly Brush BrushNeutral = Frozen(0xEE, 0xEE, 0xEE);
        private static readonly Brush BrushOff     = Frozen(0x55, 0x55, 0x55);

        private readonly IOdriveSerialService _serial;
        private readonly IOdriveParameters _parameters;
        private readonly OdriveCapabilityDetector _capabilities;
        private readonly Func<int?> _hidAxisState;
        private readonly Action<string> _log;

        private readonly List<ParameterDefinition> _polledParameters;
        private readonly DispatcherTimer _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        private readonly DispatcherTimer _pollTimer   = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        private readonly CancellationTokenSource _closing = new CancellationTokenSource();

        private bool _polling;
        private bool _closed;

        public OdriveConfigWindow(IOdriveSerialService serial, IOdriveParameters parameters,
                                  OdriveParameterCatalog catalog, Func<int?> hidAxisState, Action<string> log,
                                  IReadOnlyList<Func<IFeaturePanel>> featurePanels = null,
                                  IPowerSupplyStore powerSupplyStore = null,
                                  OdriveCapabilityDetector capabilities = null)
        {
            InitializeComponent();

            _serial       = serial;
            _parameters   = parameters;
            _capabilities = capabilities;
            _hidAxisState = hidAxisState ?? (() => null);
            _log          = log ?? (_ => { });
            _catalog      = catalog;
            _powerSupplyStore = powerSupplyStore ?? new InMemoryPowerSupplyStore();
            _ramApplier   = new OdriveRamApplier(_parameters, _serial.GetStatus, _hidAxisState, _log);
            _reader       = new OdriveParameterReader(
                (parameter, priority, cancellation) => _parameters.ReadAsync(parameter.Path, parameter.Protocol, priority, cancellation),
                _serial.GetStatus,
                capabilities != null ? capabilities.For : (Func<int, FirmwareCapabilities>)null);
            _polledParameters = catalog.Parameters.Where(p => p.Polled).ToList();

            StopDisclaimerText.Text = OdriveStatusText.StopDisclaimer;
            BuildParameterTabs(catalog);
            BuildSnapshotTab(catalog);
            BuildExtensionsTab(featurePanels ?? FeaturePanels.All);
            UpdateStatus();

            _statusTimer.Tick += (s, e) => UpdateStatus();
            _pollTimer.Tick   += (s, e) => PollStatusValues();
            Loaded += (s, e) =>
            {
                _statusTimer.Start();
                _pollTimer.Start();
                PollStatusValues();
            };
            Closed += (s, e) => EndWork();
            // The host can end the thread without closing the window first.
            Dispatcher.ShutdownStarted += (s, e) => EndWork();
        }

        private void EndWork()
        {
            _closed = true;
            _closing.Cancel();
            _statusTimer.Stop();
            _pollTimer.Stop();
        }

        // ---- Buttons ---------------------------------------------------------

        private void OnStop(object sender, RoutedEventArgs e)
        {
            _serial.RequestStop();
            UpdateStatus();
        }

        private void OnClearStop(object sender, RoutedEventArgs e)
        {
            _serial.ClearStopLatch();
            UpdateStatus();
        }

        private void OnTogglePort(object sender, RoutedEventArgs e)
        {
            if (_serial.GetStatus().State == SerialConnectionState.Released)
                _serial.AcquirePort();
            else
                _serial.ReleasePort();
            UpdateStatus();
        }

        private void OnCheckCapabilities(object sender, RoutedEventArgs e)
        {
            _capabilities?.CheckAgain();
            UpdateStatus();
        }

        // ---- Status ----------------------------------------------------------

        private void UpdateStatus()
        {
            var status = _serial.GetStatus();

            var connection = OdriveStatusText.Connection(status);
            ConnectionText.Text = connection.Text;
            ConnectionLed.Fill  = LedBrush(connection.Severity);
            PortButton.Content  = status.State == SerialConnectionState.Released ? "Take over port" : "Release port";

            int? hidState = _hidAxisState();
            AxisStateText.Text = hidState.HasValue
                ? "Axis state (HID): " + OdriveErrorDecoder.DecodeAxisState(hidState.Value)
                : "Axis state (HID): no reports";

            var stop = OdriveStatusText.Stop(status, hidState);
            StopStatusText.Text       = stop.Text;
            StopStatusText.Foreground = TextBrush(stop.Severity);
            ClearStopButton.Visibility = status.StopLatched ? Visibility.Visible : Visibility.Collapsed;

            UpdateCapabilityStatus(status);
            UpdateParameterRows(status);
            DropReviewIfOutdated(status);
            UpdateSnapshotAge(status);
        }

        private void UpdateCapabilityStatus(SerialStatus status)
        {
            if (_capabilities == null)
            {
                CapabilityText.Visibility = Visibility.Collapsed;
                return;
            }

            var line = OdriveStatusText.Capabilities(_capabilities.Status, status.IsConnected);
            CapabilityText.Text       = line.Text;
            CapabilityText.Foreground = TextBrush(line.Severity);
            CheckCapabilitiesButton.Visibility = _capabilities.CanCheckAgain ? Visibility.Visible : Visibility.Collapsed;
        }

        // Only the fixed list of status values, everything else is read on request.
        private async void PollStatusValues()
        {
            if (_polling || _closed) return;
            if (!_serial.GetStatus().IsConnected) return;

            _polling = true;
            try
            {
                await _reader.RefreshAsync(_polledParameters, CommandPriority.Background, _closing.Token);
            }
            catch (Exception ex)
            {
                _log("status poll: " + ex.Message);
            }
            finally
            {
                _polling = false;
            }
        }

        // ---- Building the tabs -----------------------------------------------

        private void BuildExtensionsTab(IReadOnlyList<Func<IFeaturePanel>> featurePanels)
        {
            if (featurePanels.Count == 0)
            {
                ExtensionsStack.Children.Add(new TextBlock
                {
                    Text  = "No extensions installed.",
                    Style = (Style)FindResource("Muted"),
                });
                return;
            }

            foreach (var createPanel in featurePanels)
            {
                var panel = createPanel();
                ExtensionsStack.Children.Add(new TextBlock
                {
                    Text  = panel.Title,
                    Style = (Style)FindResource("H2"),
                });
                ExtensionsStack.Children.Add(panel.CreateView(_parameters));
            }
        }

        // ---- Helpers ---------------------------------------------------------

        private static Brush LedBrush(StatusSeverity severity)
        {
            switch (severity)
            {
                case StatusSeverity.Ok:      return BrushOk;
                case StatusSeverity.Warning: return BrushWarn;
                case StatusSeverity.Danger:  return BrushDanger;
                default:                     return BrushOff;
            }
        }

        private static Brush TextBrush(StatusSeverity severity)
        {
            switch (severity)
            {
                case StatusSeverity.Ok:      return BrushOk;
                case StatusSeverity.Warning: return BrushWarn;
                case StatusSeverity.Danger:  return BrushDanger;
                default:                     return BrushNeutral;
            }
        }

        private static Brush Frozen(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
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
        private const string StatusTab = "status";

        private static readonly Brush BrushOk      = Frozen(0x2A, 0x9D, 0x8F);
        private static readonly Brush BrushWarn    = Frozen(0xE9, 0xC4, 0x6A);
        private static readonly Brush BrushDanger  = Frozen(0xE7, 0x6F, 0x51);
        private static readonly Brush BrushNeutral = Frozen(0xEE, 0xEE, 0xEE);
        private static readonly Brush BrushOff     = Frozen(0x55, 0x55, 0x55);

        private readonly IOdriveSerialService _serial;
        private readonly IOdriveParameters _parameters;
        private readonly Func<int?> _hidAxisState;
        private readonly Action<string> _log;

        private readonly List<ParameterDefinition> _statusParameters;
        private readonly Dictionary<string, TextBlock> _statusValues = new Dictionary<string, TextBlock>();
        private readonly DispatcherTimer _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        private readonly DispatcherTimer _pollTimer   = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };

        private bool _polling;
        private bool _closed;

        public OdriveConfigWindow(IOdriveSerialService serial, IOdriveParameters parameters,
                                  OdriveParameterCatalog catalog, Func<int?> hidAxisState, Action<string> log,
                                  IReadOnlyList<Func<IFeaturePanel>> featurePanels = null)
        {
            InitializeComponent();

            _serial       = serial;
            _parameters   = parameters;
            _hidAxisState = hidAxisState ?? (() => null);
            _log          = log ?? (_ => { });
            _statusParameters = catalog.InTab(StatusTab).ToList();

            StopDisclaimerText.Text = OdriveStatusText.StopDisclaimer;
            BuildStatusTab();
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
            Closed += (s, e) =>
            {
                _closed = true;
                _statusTimer.Stop();
                _pollTimer.Stop();
            };
        }

        /// <summary>The text shown for a status parameter, for tests.</summary>
        internal string DisplayedValue(string path) => _statusValues[path].Text;

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
        }

        private async void PollStatusValues()
        {
            if (_polling || _closed) return;
            if (!_serial.GetStatus().IsConnected)
            {
                foreach (var value in _statusValues.Values) value.Text = "—";
                return;
            }

            _polling = true;
            try
            {
                foreach (var parameter in _statusParameters)
                {
                    var result = await _parameters.ReadAsync(parameter.Path, parameter.Protocol, CommandPriority.Background);
                    if (_closed) return;
                    _statusValues[parameter.Path].Text = OdriveStatusText.ReadValue(parameter, result);
                }
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

        private void BuildStatusTab()
        {
            foreach (var group in _statusParameters.GroupBy(p => p.Group))
            {
                StatusStack.Children.Add(new TextBlock
                {
                    Text  = group.Key,
                    Style = (Style)FindResource("H2"),
                });

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                int row = 0;
                foreach (var parameter in group)
                {
                    grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                    var label = new TextBlock
                    {
                        Text       = parameter.Path,
                        FontFamily = new FontFamily("Consolas"),
                        Margin     = new Thickness(0, 3, 8, 3),
                        ToolTip    = parameter.Description,
                    };
                    Grid.SetRow(label, row);
                    grid.Children.Add(label);

                    var value = new TextBlock
                    {
                        Text   = "—",
                        Style  = (Style)FindResource("LiveValue"),
                        Margin = new Thickness(0, 3, 0, 3),
                    };
                    Grid.SetRow(value, row);
                    Grid.SetColumn(value, 1);
                    grid.Children.Add(value);
                    _statusValues[parameter.Path] = value;

                    row++;
                }
                StatusStack.Children.Add(grid);
            }
        }

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

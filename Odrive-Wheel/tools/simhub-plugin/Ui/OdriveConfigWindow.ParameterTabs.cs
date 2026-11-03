using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace OdriveWheel.SimHubPlugin
{
    // The parameter lists of the window, all built from the catalog. They only read: the only way to the
    // board from here is the read delegate of OdriveParameterReader. Writing lives in the PowerBrake part.
    public partial class OdriveConfigWindow
    {
        private const string StatusTab = "status";

        private sealed class EditorTab
        {
            public EditorTab(string id, string title, string commonPrefix)
            {
                Id           = id;
                Title        = title;
                CommonPrefix = commonPrefix;
            }

            public string Id { get; }
            public string Title { get; }
            /// <summary>Left out of the labels, the full path is in the tooltip.</summary>
            public string CommonPrefix { get; }
        }

        private static readonly EditorTab[] EditorTabs =
        {
            new EditorTab("odrv",       "PSU / Brake", "config."),
            new EditorTab("axis0",      "Axis",        "axis0.config."),
            new EditorTab("motor",      "Motor",       "axis0.motor."),
            new EditorTab("encoder",    "Encoder",     "axis0.encoder.config."),
            new EditorTab("controller", "Controller",  "axis0.controller.config."),
        };

        private sealed class ParameterRow
        {
            public ParameterDefinition Parameter;
            public TextBlock Value;
            public TextBlock ReadAt;
        }

        private readonly OdriveParameterReader _reader;
        private readonly List<ParameterRow> _rows = new List<ParameterRow>();
        private readonly Dictionary<string, ParameterRow> _rowsByPath = new Dictionary<string, ParameterRow>();
        private readonly Dictionary<string, TextBlock> _refreshSummaries = new Dictionary<string, TextBlock>();
        private readonly Dictionary<string, Button> _refreshButtons = new Dictionary<string, Button>();

        private (int Version, int CapabilityVersion, int Generation, bool Connected) _shownState = (-1, -1, -1, false);
        private bool _refreshing;

        /// <summary>The text shown for a parameter, for tests.</summary>
        internal string DisplayedValue(string path) => _rowsByPath[path].Value.Text;

        /// <summary>The refresh button of a whole tab, for tests.</summary>
        internal Button RefreshButton(string tab) => _refreshButtons[tab];

        /// <summary>The refresh button of one group, for tests.</summary>
        internal Button RefreshButton(string tab, string group) => _refreshButtons[tab + "/" + group];

        /// <summary>The line above a tab's list, for tests.</summary>
        internal string RefreshSummary(string tab) => _refreshSummaries[tab].Text;

        internal IEnumerable<string> ButtonKeys => _refreshButtons.Keys;

        // ---- Refreshing ------------------------------------------------------

        private async void RefreshParameters(string tab, IReadOnlyList<ParameterDefinition> parameters)
        {
            if (_refreshing || _applying || _capturing || _closed) return;

            _refreshing = true;
            SetRefreshButtonsEnabled(false);
            UpdateSnapshotButtons();
            var summary = _refreshSummaries[tab];
            summary.Text = "Reading " + parameters.Count + " values…";
            try
            {
                var result = await _reader.RefreshAsync(parameters, CommandPriority.Batch, _closing.Token);
                if (!_closed) summary.Text = OdriveStatusText.Refresh(result, DateTime.Now);
            }
            catch (Exception ex)
            {
                _log("parameter refresh: " + ex.Message);
                if (!_closed) summary.Text = "Refresh failed: " + ex.Message;
            }
            finally
            {
                _refreshing = false;
                if (!_closed)
                {
                    SetRefreshButtonsEnabled(true);
                    UpdateSnapshotButtons();
                }
            }
        }

        private void SetRefreshButtonsEnabled(bool enabled)
        {
            foreach (var button in _refreshButtons.Values) button.IsEnabled = enabled;
        }

        // A lost connection or a declaration of the firmware changes what the rows must say without any new read.
        private void UpdateParameterRows(SerialStatus status)
        {
            var state = (_reader.Version, _capabilities?.Version ?? 0, status.ConnectGeneration, status.IsConnected);
            if (state == _shownState) return;
            _shownState = state;

            foreach (var row in _rows)
            {
                var reading = _reader.Get(row.Parameter);
                row.Value.Text       = reading.Text;
                row.Value.Foreground = TextBrush(reading.Severity);
                row.ReadAt.Text      = reading.ReadAt?.ToString("HH:mm:ss") ?? "";
            }
            UpdatePowerBrakeCurrentValues();
        }

        // ---- Building --------------------------------------------------------

        private void BuildParameterTabs(OdriveParameterCatalog catalog)
        {
            BuildParameterList(StatusStack, StatusTab, "", catalog.InTab(StatusTab).ToList());

            // Before Extensions, which stays the last tab.
            int insertAt = MainTabs.Items.Count - 1;
            foreach (var tab in EditorTabs)
            {
                var stack = new StackPanel { Margin = new Thickness(16, 4, 16, 16) };
                if (tab.Id == PowerBrakeTab)
                {
                    BuildPowerSupplySection(stack);
                    BuildPowerBrakeEditor(stack);
                }
                BuildParameterList(stack, tab.Id, tab.CommonPrefix, catalog.InTab(tab.Id).ToList());
                MainTabs.Items.Insert(insertAt++, new TabItem
                {
                    Header  = tab.Title,
                    Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = stack },
                });
            }
        }

        private void BuildParameterList(StackPanel stack, string tab, string commonPrefix, List<ParameterDefinition> parameters)
        {
            var bar = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
            var refreshTab = NewRefreshButton(tab, tab, "Refresh tab", parameters);
            DockPanel.SetDock(refreshTab, Dock.Left);
            bar.Children.Add(refreshTab);
            var summary = new TextBlock { Text = "Not read yet.", Style = (Style)FindResource("Muted"), VerticalAlignment = VerticalAlignment.Center };
            bar.Children.Add(summary);
            _refreshSummaries[tab] = summary;
            stack.Children.Add(bar);

            stack.Children.Add(new TextBlock
            {
                Text   = tab == StatusTab
                    ? "The errors, the axis state, the bus voltage and the build id update every second, the rest when you press Refresh. Editing follows later."
                    : tab == PowerBrakeTab
                        ? "This list only reads, when you press Refresh. The Power / Brake values are edited in the section above, the others stay read-only."
                        : "Read-only: this tab reads when you press Refresh and changes nothing on the board. Editing follows later.",
                Style  = (Style)FindResource("Muted"),
                Margin = new Thickness(0, 6, 0, 0),
            });

            foreach (var group in parameters.GroupBy(p => p.Group))
            {
                var heading = new DockPanel();
                var groupParameters = group.ToList();
                var refreshGroup = NewRefreshButton(tab + "/" + group.Key, tab, "Refresh", groupParameters);
                refreshGroup.Padding = new Thickness(8, 1, 8, 1);
                refreshGroup.Margin  = new Thickness(0, 10, 0, 4);
                DockPanel.SetDock(refreshGroup, Dock.Right);
                heading.Children.Add(refreshGroup);
                heading.Children.Add(new TextBlock { Text = group.Key, Style = (Style)FindResource("H2") });
                stack.Children.Add(heading);
                stack.Children.Add(BuildRows(groupParameters, commonPrefix));
            }
        }

        private Button NewRefreshButton(string key, string tab, string text, IReadOnlyList<ParameterDefinition> parameters)
        {
            var button = new Button { Content = text, Style = (Style)FindResource("SecondaryButton") };
            button.Click += (s, e) => RefreshParameters(tab, parameters);
            _refreshButtons[key] = button;
            return button;
        }

        private Grid BuildRows(List<ParameterDefinition> parameters, string commonPrefix)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(330) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(62) });

            for (int index = 0; index < parameters.Count; index++)
            {
                var parameter = parameters[index];
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                bool shortened = commonPrefix.Length > 0 && parameter.Path.StartsWith(commonPrefix, StringComparison.Ordinal);
                AddCell(grid, index, 0, new TextBlock
                {
                    Text       = shortened ? parameter.Path.Substring(commonPrefix.Length) : parameter.Path,
                    FontFamily = new FontFamily("Consolas"),
                    Margin     = new Thickness(0, 3, 8, 3),
                    ToolTip    = parameter.Path + "\n" + parameter.Description,
                });

                var value = new TextBlock
                {
                    Text   = "—",
                    Style  = (Style)FindResource("LiveValue"),
                    Margin = new Thickness(0, 3, 8, 3),
                };
                AddCell(grid, index, 1, value);

                AddCell(grid, index, 2, new TextBlock
                {
                    Text    = AccessText(parameter),
                    Style   = (Style)FindResource("Muted"),
                    Margin  = new Thickness(0, 4, 8, 3),
                    ToolTip = AccessToolTip(parameter),
                });

                var readAt = new TextBlock { Style = (Style)FindResource("Muted"), Margin = new Thickness(0, 4, 0, 3) };
                AddCell(grid, index, 3, readAt);

                var row = new ParameterRow { Parameter = parameter, Value = value, ReadAt = readAt };
                _rows.Add(row);
                _rowsByPath[parameter.Path] = row;
            }
            return grid;
        }

        private static void AddCell(Grid grid, int row, int column, UIElement element)
        {
            Grid.SetRow(element, row);
            Grid.SetColumn(element, column);
            grid.Children.Add(element);
        }

        private static string AccessText(ParameterDefinition parameter)
        {
            if (parameter.Calibrated) return "calibration";
            return parameter.Access == ParameterAccess.Read ? "read-only" : "writable";
        }

        private static string AccessToolTip(ParameterDefinition parameter)
        {
            if (parameter.Calibrated) return "Set by a calibration. The firmware would accept a write, the web client refuses it.";
            if (parameter.Editable) return "The firmware allows writing this. Edit it under Edit Power / Brake above.";
            return parameter.Access == ParameterAccess.Read
                ? "The firmware does not allow writing this."
                : "The firmware allows writing this. This window only reads, editing is planned.";
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace OdriveWheel.SimHubPlugin
{
    // The PSU / Brake tab above its read-only list: the power supply data, which never leaves this PC, and
    // the first editor that writes to the board. No dialog here: a modal one would block the stop button.
    public partial class OdriveConfigWindow
    {
        private const string PowerBrakeTab = "odrv";
        private const string PowerBrakeRefreshKey = PowerBrakeTab + "/editor";

        private readonly OdriveParameterCatalog _catalog;
        private readonly IPowerSupplyStore _powerSupplyStore;
        private readonly OdriveRamApplier _ramApplier;

        // ---- Power supply data -----------------------------------------------

        internal TextBox   PsuVoltageBox    { get; private set; }
        internal TextBox   PsuContinuousBox { get; private set; }
        internal TextBox   PsuHeadroomBox   { get; private set; }
        internal CheckBox  PsuOverloadBox   { get; private set; }
        internal TextBox   PsuShortTimeBox  { get; private set; }
        internal TextBox   PsuDurationBox   { get; private set; }
        internal TextBlock PsuUsableText    { get; private set; }
        internal TextBlock PsuStatusText    { get; private set; }
        internal Button    PsuSaveButton    { get; private set; }

        private void BuildPowerSupplySection(StackPanel stack)
        {
            stack.Children.Add(new TextBlock { Text = "Power supply data", Style = (Style)FindResource("H2") });
            stack.Children.Add(new TextBlock
            {
                Text   = "Stored in the plugin settings on this PC, not on the board. Nothing here is sent to the board. This is DC supply "
                       + "current, not motor phase current, and it sets no limit: no board value is taken from it. The short-time current is "
                       + "not a continuous limit. A time-dependent current limit does not exist yet, this data is the basis for later features.",
                Style  = (Style)FindResource("Muted"),
                Margin = new Thickness(0, 0, 0, 6),
            });

            var data = _powerSupplyStore.Load();
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            int row = 0;
            PsuVoltageBox    = AddPowerSupplyRow(grid, ref row, "DC Output Voltage", "V", data.DcOutputVoltage);
            PsuContinuousBox = AddPowerSupplyRow(grid, ref row, "Continuous Output Current", "A", data.ContinuousCurrent);
            PsuHeadroomBox   = AddPowerSupplyRow(grid, ref row, "Current Headroom", "%", data.CurrentHeadroomPercent);

            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            PsuOverloadBox = new CheckBox
            {
                Content   = "Short-time overload supported",
                IsChecked = data.ShortTimeOverloadSupported,
                Margin    = new Thickness(0, 6, 0, 3),
            };
            Grid.SetRow(PsuOverloadBox, row++);
            Grid.SetColumnSpan(PsuOverloadBox, 3);
            grid.Children.Add(PsuOverloadBox);

            PsuShortTimeBox = AddPowerSupplyRow(grid, ref row, "Short-Time Current", "A", data.ShortTimeCurrent);
            PsuDurationBox  = AddPowerSupplyRow(grid, ref row, "Maximum Duration", "ms", data.MaximumDurationMs);
            stack.Children.Add(grid);

            PsuUsableText = new TextBlock { Margin = new Thickness(0, 6, 0, 0) };
            stack.Children.Add(PsuUsableText);

            var bar = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
            PsuSaveButton = new Button { Content = "Save to plugin settings", Style = (Style)FindResource("SecondaryButton") };
            PsuSaveButton.Click += (s, e) => SavePowerSupply();
            DockPanel.SetDock(PsuSaveButton, Dock.Left);
            bar.Children.Add(PsuSaveButton);
            PsuStatusText = new TextBlock { Style = (Style)FindResource("Muted"), VerticalAlignment = VerticalAlignment.Center };
            bar.Children.Add(PsuStatusText);
            stack.Children.Add(bar);

            foreach (var box in new[] { PsuVoltageBox, PsuContinuousBox, PsuHeadroomBox, PsuShortTimeBox, PsuDurationBox })
                box.TextChanged += (s, e) => UpdatePowerSupplyPreview(edited: true);
            PsuOverloadBox.Checked   += (s, e) => UpdatePowerSupplyPreview(edited: true);
            PsuOverloadBox.Unchecked += (s, e) => UpdatePowerSupplyPreview(edited: true);
            UpdatePowerSupplyPreview(edited: false);
        }

        private TextBox AddPowerSupplyRow(Grid grid, ref int row, string label, string unit, double? value)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            AddCell(grid, row, 0, new TextBlock { Text = label, Margin = new Thickness(0, 3, 8, 3), VerticalAlignment = VerticalAlignment.Center });
            var box = new TextBox { Text = PowerSupplyValidation.ToInputText(value), Margin = new Thickness(0, 2, 8, 2) };
            AddCell(grid, row, 1, box);
            AddCell(grid, row, 2, new TextBlock { Text = unit, Style = (Style)FindResource("Muted"), VerticalAlignment = VerticalAlignment.Center });
            row++;
            return box;
        }

        private PowerSupplyInput ReadPowerSupplyInput() => new PowerSupplyInput
        {
            DcOutputVoltage        = PsuVoltageBox.Text,
            ContinuousCurrent      = PsuContinuousBox.Text,
            CurrentHeadroomPercent = PsuHeadroomBox.Text,
            ShortTimeOverloadSupported = PsuOverloadBox.IsChecked == true,
            ShortTimeCurrent       = PsuShortTimeBox.Text,
            MaximumDurationMs      = PsuDurationBox.Text,
        };

        private void UpdatePowerSupplyPreview(bool edited)
        {
            bool overload = PsuOverloadBox.IsChecked == true;
            PsuShortTimeBox.IsEnabled = overload;
            PsuDurationBox.IsEnabled  = overload;

            var data = PowerSupplyValidation.Parse(ReadPowerSupplyInput(), out var errors);
            var usable = data.UsableContinuousCurrent();
            PsuUsableText.Text = "Usable continuous current: "
                + (usable.HasValue ? usable.Value.ToString("0.###", CultureInfo.InvariantCulture) + " A" : "— (needs current and headroom)");

            if (errors.Count > 0)
            {
                PsuStatusText.Text       = string.Join(" ", errors);
                PsuStatusText.Foreground = BrushWarn;
            }
            else
            {
                PsuStatusText.Text       = edited ? "Not saved yet." : "";
                PsuStatusText.Foreground = BrushNeutral;
            }
        }

        private void SavePowerSupply()
        {
            var data = PowerSupplyValidation.Parse(ReadPowerSupplyInput(), out var errors);
            if (errors.Count > 0)
            {
                PsuStatusText.Text       = "Not saved. " + string.Join(" ", errors);
                PsuStatusText.Foreground = BrushWarn;
                return;
            }

            try
            {
                _powerSupplyStore.Save(data);
                PsuStatusText.Text       = "Saved in the plugin settings on this PC. Nothing was sent to the board, its configuration is unchanged.";
                PsuStatusText.Foreground = BrushOk;
            }
            catch (Exception ex)
            {
                _log("power supply data: " + ex.Message);
                PsuStatusText.Text       = "Could not save: " + ex.Message;
                PsuStatusText.Foreground = BrushDanger;
            }
        }

        // ---- Editing Power / Brake -------------------------------------------

        private sealed class PowerBrakeField
        {
            public ParameterDefinition Parameter;
            public CheckBox Select;
            public TextBox TextInput;
            public ComboBox BooleanInput;
            public TextBlock Current;

            public string Input => BooleanInput != null
                ? (BooleanInput.SelectedIndex >= 0 ? BooleanInput.SelectedIndex.ToString(CultureInfo.InvariantCulture) : "")
                : TextInput.Text;
        }

        private readonly List<PowerBrakeField> _powerBrakeFields = new List<PowerBrakeField>();
        private PowerBrakeReview _review;
        private bool _applying;

        internal Button    ReviewButton   { get; private set; }
        internal Button    ApplyButton    { get; private set; }
        internal TextBlock ReviewText     { get; private set; }
        internal TextBlock ApplyText      { get; private set; }
        internal bool      IsApplying => _applying;

        /// <summary>The paths with an input in the window, for tests.</summary>
        internal IEnumerable<string> PowerBrakeFieldPaths => _powerBrakeFields.Select(f => f.Parameter.Path);

        /// <summary>Types a value, for tests. Selecting the field is a step of its own.</summary>
        internal void TypePowerBrakeValue(string path, string text)
        {
            var field = _powerBrakeFields.First(f => f.Parameter.Path == path);
            if (field.BooleanInput != null) field.BooleanInput.SelectedIndex = text == "" ? -1 : int.Parse(text, CultureInfo.InvariantCulture);
            else field.TextInput.Text = text;
        }

        internal void SelectPowerBrakeField(string path, bool selected)
            => _powerBrakeFields.First(f => f.Parameter.Path == path).Select.IsChecked = selected;

        private void BuildPowerBrakeEditor(StackPanel stack)
        {
            stack.Children.Add(new TextBlock { Text = "Edit Power / Brake (RAM only)", Style = (Style)FindResource("H2") });
            stack.Children.Add(new TextBlock
            {
                Text   = "Typing sends nothing. Tick the fields to change, review them, then apply. Applied values are in the RAM of the board: "
                       + "a reboot restores the saved values, and this section never saves to the board. The plugin checks that both axes are IDLE, "
                       + "not armed and without a pending request right before each write. That is no lock: another command source can still change "
                       + "the state in between.",
                Style  = (Style)FindResource("Muted"),
                Margin = new Thickness(0, 0, 0, 6),
            });

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) });

            var parameters = OdrivePowerBrakeEditor.EditableParameters(_catalog);
            for (int row = 0; row < parameters.Count; row++)
            {
                var parameter = parameters[row];
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                var field = new PowerBrakeField
                {
                    Parameter = parameter,
                    Select    = new CheckBox { VerticalAlignment = VerticalAlignment.Center },
                    Current   = new TextBlock { Text = "—", Style = (Style)FindResource("LiveValue"), Margin = new Thickness(0, 3, 8, 3), VerticalAlignment = VerticalAlignment.Center },
                };
                field.Select.Checked   += (s, e) => DropReview();
                field.Select.Unchecked += (s, e) => DropReview();
                AddCell(grid, row, 0, field.Select);

                var path = parameter.Path.StartsWith("config.", StringComparison.Ordinal) ? parameter.Path.Substring("config.".Length) : parameter.Path;
                AddCell(grid, row, 1, new TextBlock
                {
                    Text = path,
                    FontFamily = new FontFamily("Consolas"),
                    Margin = new Thickness(0, 3, 8, 3),
                    VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = parameter.Path + "\n" + parameter.Description,
                });
                AddCell(grid, row, 2, field.Current);

                if (parameter.Type == ParameterType.Boolean)
                {
                    field.BooleanInput = new ComboBox { Margin = new Thickness(0, 2, 8, 2) };
                    field.BooleanInput.Items.Add("0 (false)");
                    field.BooleanInput.Items.Add("1 (true)");
                    field.BooleanInput.SelectionChanged += (s, e) => DropReview();
                    AddCell(grid, row, 3, field.BooleanInput);
                }
                else
                {
                    field.TextInput = new TextBox { Margin = new Thickness(0, 2, 8, 2) };
                    field.TextInput.TextChanged += (s, e) => DropReview();
                    AddCell(grid, row, 3, field.TextInput);
                }
                AddCell(grid, row, 4, new TextBlock { Text = parameter.Unit, Style = (Style)FindResource("Muted"), VerticalAlignment = VerticalAlignment.Center });
                _powerBrakeFields.Add(field);
            }
            stack.Children.Add(grid);

            var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            var refresh = NewRefreshButton(PowerBrakeRefreshKey, PowerBrakeTab, "Refresh these values", parameters);
            bar.Children.Add(refresh);
            ReviewButton = new Button { Content = "Review selected changes", Style = (Style)FindResource("SecondaryButton") };
            ReviewButton.Click += (s, e) => ReviewPowerBrake();
            bar.Children.Add(ReviewButton);
            ApplyButton = new Button { Content = "Apply to RAM", Style = (Style)FindResource("SecondaryButton"), IsEnabled = false };
            ApplyButton.Click += (s, e) => ApplyPowerBrake();
            bar.Children.Add(ApplyButton);
            stack.Children.Add(bar);

            ReviewText = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
            stack.Children.Add(ReviewText);
            ApplyText = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
            stack.Children.Add(ApplyText);
        }

        private void UpdatePowerBrakeCurrentValues()
        {
            foreach (var field in _powerBrakeFields)
            {
                var reading = _reader.Get(field.Parameter);
                field.Current.Text       = reading.Text;
                field.Current.Foreground = TextBrush(reading.Severity);
            }
        }

        private void ReviewPowerBrake()
        {
            if (_applying) return;
            var selected = _powerBrakeFields.Where(f => f.Select.IsChecked == true)
                                            .ToDictionary(f => f.Parameter.Path, f => f.Input, StringComparer.Ordinal);
            var status = _serial.GetStatus();
            _review = OdrivePowerBrakeEditor.Review(_catalog, _reader.Get, status, selected, _capabilities?.For(status));
            ReviewText.Text        = OdriveStatusText.RamReview(_review);
            ReviewText.Foreground  = _review.CanApply ? BrushNeutral : BrushWarn;
            ApplyButton.IsEnabled  = _review.CanApply;
        }

        // Whatever was reviewed is no longer what would be written.
        private void DropReview()
        {
            if (_applying) return;
            _review = null;
            if (ApplyButton != null) ApplyButton.IsEnabled = false;
            if (ReviewText != null) ReviewText.Text = "";
        }

        private void DropReviewIfOutdated(SerialStatus status)
        {
            if (_review == null || _applying) return;
            bool sameOperation = status.IsConnected && status.ConnectGeneration == _review.ConnectGeneration && status.LastStop == _review.LastStop;
            bool sameDeclaration = _review.Capabilities == _capabilities?.For(status);
            if (sameOperation && sameDeclaration) return;
            DropReview();
            ReviewText.Text = sameOperation
                ? "The firmware has declared what its replies guarantee, review again."
                : "The connection or the stop state changed, review again.";
            ReviewText.Foreground = BrushWarn;
        }

        private async void ApplyPowerBrake()
        {
            var review = _review;
            if (review == null || !review.CanApply || _applying || _refreshing || _closed) return;

            _applying = true;
            SetPowerBrakeEditorEnabled(false);
            ApplyText.Text = "Applying to RAM…";
            ApplyText.Foreground = BrushNeutral;

            RamApplyResult result = null;
            try
            {
                result = await _ramApplier.ApplyAsync(review, _closing.Token);
            }
            catch (Exception ex)
            {
                _log("apply to RAM: " + ex.Message);
                if (!_closed)
                {
                    ApplyText.Text = "Failed: " + ex.Message + ". Check the values, some may have reached the board.";
                    ApplyText.Foreground = BrushDanger;
                }
            }
            finally
            {
                // What was read before says nothing about a value that may have been written.
                foreach (var change in review.Changes) _reader.Invalidate(change.Parameter);
                _review = null;
                _applying = false;
                if (!_closed) SetPowerBrakeEditorEnabled(true);
            }

            if (result == null || _closed) return;

            ReviewText.Text = "";
            ApplyText.Text = OdriveStatusText.RamApply(result);
            ApplyText.Foreground = result.AllVerified ? BrushOk : BrushWarn;
            foreach (var write in result.Writes.Where(w => w.Outcome == RamWriteOutcome.VerifiedByReadback))
            {
                var field = _powerBrakeFields.First(f => f.Parameter == write.Change.Parameter);
                field.Select.IsChecked = false;
                if (field.BooleanInput != null) field.BooleanInput.SelectedIndex = -1;
                else field.TextInput.Text = "";
            }
            ApplyButton.IsEnabled = false;

            // Show what the board holds now, unless the board is the reason this ended.
            if (result.AbortReason == null)
                RefreshParameters(PowerBrakeTab, review.Group);
        }

        private void SetPowerBrakeEditorEnabled(bool enabled)
        {
            foreach (var field in _powerBrakeFields)
            {
                field.Select.IsEnabled = enabled;
                if (field.TextInput != null) field.TextInput.IsEnabled = enabled;
                if (field.BooleanInput != null) field.BooleanInput.IsEnabled = enabled;
            }
            ReviewButton.IsEnabled = enabled;
            ApplyButton.IsEnabled  = false;
            SetRefreshButtonsEnabled(enabled);
        }
    }
}

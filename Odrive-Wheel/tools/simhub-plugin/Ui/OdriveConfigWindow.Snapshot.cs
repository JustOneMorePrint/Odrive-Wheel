using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace OdriveWheel.SimHubPlugin
{
    // The Snapshot tab: a read-only catalog snapshot on request, its files, comparisons and the diagnostic report.
    // It reads through OdriveSnapshotCapture only, and comparing two files does not touch the serial service at all.
    // File dialogs run on a thread of their own, and so do file access, parsing and comparing: the window's thread only
    // starts them with values that do not change and shows what comes back, so the stop button and the port button stay
    // usable. A run number tells a late result from a current one, and nothing is shown once the window is closed.
    public partial class OdriveConfigWindow
    {
        private const string JsonFilter   = "Snapshot values (*.json)|*.json|All files (*.*)|*.*";
        private const string ReportFilter = "Report (*.md)|*.md|All files (*.*)|*.*";

        private OdriveSnapshotCapture _snapshotCapture;
        private CancellationTokenSource _captureCancel;
        private CatalogSnapshot _snapshot;
        private SnapshotComparison _comparison;
        private CatalogSnapshot _comparedSnapshot;
        private bool _capturing;
        private int _captureRun;
        private int _fileRun;
        private int _compareRun;
        private string _lastFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        internal Button    CaptureButton        { get; private set; }
        internal Button    CancelCaptureButton  { get; private set; }
        internal Button    ExportButton         { get; private set; }
        internal Button    ReportButton         { get; private set; }
        internal Button    CompareBoardButton   { get; private set; }
        internal Button    CompareFilesButton   { get; private set; }
        internal TextBox   CompareLeftBox       { get; private set; }
        internal TextBox   CompareRightBox      { get; private set; }
        internal TextBlock SnapshotProgressText { get; private set; }
        internal TextBlock SnapshotSummaryText  { get; private set; }
        internal TextBlock SnapshotAgeText      { get; private set; }
        internal TextBlock SnapshotFileText     { get; private set; }
        internal TextBox   ComparisonBox        { get; private set; }
        internal bool      IsCapturing => _capturing;
        internal CatalogSnapshot CurrentSnapshot => _snapshot;
        internal SnapshotComparison CurrentComparison => _comparison;

        private static string PluginVersion => typeof(OdriveConfigWindow).Assembly.GetName().Version.ToString();

        private void BuildSnapshotTab(OdriveParameterCatalog catalog)
        {
            _snapshotCapture = new OdriveSnapshotCapture(_parameters, _serial.GetStatus, catalog,
                                                         _capabilities != null ? _capabilities.StatusFor : (Func<SerialStatus, FirmwareCapabilityStatus>)null);

            var stack = new StackPanel { Margin = new Thickness(16, 4, 16, 16) };
            stack.Children.Add(new TextBlock { Text = "Catalog snapshot", Style = (Style)FindResource("H2") });
            stack.Children.Add(Muted(OdriveSnapshotText.WhatItIs + " " + OdriveSnapshotText.TimingNote + " It reads only when you press a button."));
            stack.Children.Add(Muted(OdriveSnapshotText.Scope(catalog)));

            var bar = Bar();
            CaptureButton = NewButton("Capture snapshot", (s, e) => CaptureSnapshot(compareWith: null));
            CancelCaptureButton = NewButton("Cancel", (s, e) => _captureCancel?.Cancel());
            CancelCaptureButton.IsEnabled = false;
            bar.Children.Add(CaptureButton);
            bar.Children.Add(CancelCaptureButton);
            SnapshotProgressText = new TextBlock { Style = (Style)FindResource("Muted"), VerticalAlignment = VerticalAlignment.Center };
            bar.Children.Add(SnapshotProgressText);
            stack.Children.Add(bar);

            SnapshotSummaryText = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), Text = "No snapshot yet." };
            stack.Children.Add(SnapshotSummaryText);
            SnapshotAgeText = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = BrushWarn, Visibility = Visibility.Collapsed };
            stack.Children.Add(SnapshotAgeText);

            var files = Bar();
            ExportButton = NewButton("Export snapshot…", (s, e) => PickExportFile());
            ReportButton = NewButton("Write diagnostic report…", (s, e) => PickReportFile());
            files.Children.Add(ExportButton);
            files.Children.Add(ReportButton);
            stack.Children.Add(files);
            SnapshotFileText = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
            stack.Children.Add(SnapshotFileText);

            stack.Children.Add(new TextBlock { Text = "Compare", Style = (Style)FindResource("H2") });
            stack.Children.Add(Muted("Board against a file reads a new snapshot first, through the catalog paths only: the keys of a file are never sent "
                                   + "to the board. Two files are compared offline, without the board. Settings and status values are shown apart."));
            CompareLeftBox  = AddFileRow(stack, "File");
            CompareRightBox = AddFileRow(stack, "Second file");

            var compare = Bar();
            CompareBoardButton = NewButton("Compare board with file", (s, e) => CompareBoardWithFile());
            CompareFilesButton = NewButton("Compare the two files", (s, e) => CompareFiles());
            compare.Children.Add(CompareBoardButton);
            compare.Children.Add(CompareFilesButton);
            stack.Children.Add(compare);

            ComparisonBox = new TextBox
            {
                IsReadOnly   = true,
                TextWrapping = TextWrapping.Wrap,
                FontFamily   = new FontFamily("Consolas"),
                MinHeight    = 120,
                Margin       = new Thickness(0, 8, 0, 0),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            };
            stack.Children.Add(ComparisonBox);

            UpdateSnapshotButtons();
            MainTabs.Items.Insert(MainTabs.Items.Count - 1, new TabItem
            {
                Header  = "Snapshot",
                Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = stack },
            });
        }

        private TextBlock Muted(string text)
            => new TextBlock { Text = text, Style = (Style)FindResource("Muted"), Margin = new Thickness(0, 0, 0, 6) };

        private static StackPanel Bar() => new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };

        private Button NewButton(string text, RoutedEventHandler click)
        {
            var button = new Button { Content = text, Style = (Style)FindResource("SecondaryButton") };
            button.Click += click;
            return button;
        }

        private TextBox AddFileRow(StackPanel stack, string label)
        {
            var row = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
            var title = new TextBlock { Text = label, Width = 90, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(title, Dock.Left);
            row.Children.Add(title);
            var box = new TextBox { Margin = new Thickness(0, 0, 8, 0) };
            var browse = NewButton("Browse…", (s, e) => OffThreadFileDialog.Open(Dispatcher, "Choose a snapshot file", _lastFolder, JsonFilter,
                                                                               path => { box.Text = path; _lastFolder = Path.GetDirectoryName(path); }, _log));
            browse.Margin = new Thickness(0);
            DockPanel.SetDock(browse, Dock.Right);
            row.Children.Add(browse);
            row.Children.Add(box);
            stack.Children.Add(row);
            return box;
        }

        private void UpdateSnapshotButtons()
        {
            if (CaptureButton == null) return;
            bool busy = _capturing || _applying || _refreshing;
            CaptureButton.IsEnabled       = !busy;
            CompareBoardButton.IsEnabled  = !busy;
            CancelCaptureButton.IsEnabled = _capturing;
            ExportButton.IsEnabled        = _snapshot != null;
            ReportButton.IsEnabled        = _snapshot != null;
        }

        // Called with every status update: a finished snapshot stays valid as a record, but its connection may be gone.
        private void UpdateSnapshotAge(SerialStatus status)
        {
            if (SnapshotAgeText == null) return;
            var snapshot = _snapshot;
            bool gone = snapshot != null && (!status.IsConnected || status.ConnectGeneration != snapshot.ConnectGeneration);
            SnapshotAgeText.Visibility = gone ? Visibility.Visible : Visibility.Collapsed;
            if (gone)
                SnapshotAgeText.Text = "The connection of this snapshot has ended. It stays a record of that time and can still be exported, "
                                     + "but it says nothing about the board now.";
        }

        // ---- Work off the window's thread ---------------------------------------

        /// <summary>Runs at the start of every file job, for tests that hold one there.</summary>
        internal Action FileWorkHook { get; set; }

        private Task<T> FileWork<T>(Func<T> work)
        {
            var hook = FileWorkHook;
            return Task.Run(() =>
            {
                hook?.Invoke();
                return work();
            });
        }

        // A new attempt makes whatever the area showed before history, also for the report.
        private int BeginComparison(string text)
        {
            _comparison = null;
            _comparedSnapshot = null;
            ComparisonBox.Text = text;
            return ++_compareRun;
        }

        private bool IsCurrentComparison(int run) => !_closed && run == _compareRun;

        // ---- Capture -----------------------------------------------------------

        private async void CaptureSnapshot(LoadedSnapshotFile compareWith, int compareRun = 0)
        {
            if (_closed) return;
            var why = _capturing || _applying || _refreshing ? "another read or an apply is running" : _snapshotCapture.WhyNotStart();
            if (why != null)
            {
                SnapshotProgressText.Text = "Cannot capture: " + why + ".";
                if (compareWith != null && IsCurrentComparison(compareRun))
                    ComparisonBox.Text = "Nothing was compared: cannot capture the board, " + why + ".";
                return;
            }

            _capturing = true;
            int run = ++_captureRun;
            _captureCancel = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
            UpdateSnapshotButtons();
            SetRefreshButtonsEnabled(false);
            SnapshotProgressText.Text = "Reading…";

            // Progress comes through the dispatcher and can arrive after the end, the run number tells.
            var progress = new Progress<SnapshotProgress>(p =>
            {
                if (!_closed && _capturing && run == _captureRun)
                    SnapshotProgressText.Text = "Reading " + p.Done + " of " + p.Total + ": " + p.Path;
            });

            CatalogSnapshot snapshot = null;
            try
            {
                snapshot = await _snapshotCapture.CaptureAsync(progress, _captureCancel.Token);
            }
            catch (InvalidOperationException ex)
            {
                if (!_closed) SnapshotProgressText.Text = "Cannot capture: " + ex.Message + ".";
            }
            catch (Exception ex)
            {
                _log("snapshot: " + ex);
                if (!_closed) SnapshotProgressText.Text = "Capture failed: " + ex.Message;
            }
            finally
            {
                _capturing = false;
                _captureCancel.Dispose();
                _captureCancel = null;
                if (!_closed)
                {
                    SetRefreshButtonsEnabled(true);
                    UpdateSnapshotButtons();
                }
            }
            if (_closed) return;
            if (snapshot == null)
            {
                if (compareWith != null && IsCurrentComparison(compareRun)) ComparisonBox.Text = "Nothing was compared: " + SnapshotProgressText.Text;
                return;
            }

            _snapshot = snapshot;
            _comparison = null;
            _comparedSnapshot = null;
            SnapshotProgressText.Text = snapshot.Completeness == SnapshotCompleteness.Partial ? "Ended early." : "Done.";
            SnapshotSummaryText.Text = OdriveSnapshotText.Summary(snapshot);
            SnapshotSummaryText.Foreground = snapshot.Completeness == SnapshotCompleteness.Complete && snapshot.Capabilities.FloatsAreExact ? BrushOk : BrushWarn;
            SnapshotFileText.Text = "";
            UpdateSnapshotButtons();
            UpdateSnapshotAge(_serial.GetStatus());

            if (compareWith == null || !IsCurrentComparison(compareRun)) return;
            var catalog = _catalog;
            var shown = await Compute(() => Compared(compareWith.ToSide(Path.GetFileName(compareWith.ValuesPath)),
                                                     SnapshotSide.FromSnapshot(snapshot, "the board, snapshot of " + OdriveSnapshotText.Time(snapshot.StartedUtc)),
                                                     catalog, compareWith));
            if (!IsCurrentComparison(compareRun)) return;
            ShowComparison(shown, snapshot);
        }

        // ---- Comparing ---------------------------------------------------------

        private sealed class ComparisonShown
        {
            public SnapshotComparison Comparison;
            public string Text;
        }

        private static ComparisonShown Compared(SnapshotSide left, SnapshotSide right, OdriveParameterCatalog catalog, params LoadedSnapshotFile[] files)
        {
            var comparison = SnapshotComparison.Compare(left, right, catalog);
            var text = new StringBuilder();
            foreach (var file in files) text.AppendLine(OdriveSnapshotText.LoadedFile(file));
            text.Append(OdriveSnapshotText.Comparison(comparison));
            return new ComparisonShown { Comparison = comparison, Text = text.ToString() };
        }

        // Comparing is no file job, but it runs off the window's thread as well. A failure becomes text.
        private static async Task<ComparisonShown> Compute(Func<ComparisonShown> work)
        {
            try
            {
                return await Task.Run(work);
            }
            catch (Exception ex)
            {
                return new ComparisonShown { Text = "Nothing was compared: " + ex.Message };
            }
        }

        private async void CompareBoardWithFile()
        {
            if (_closed) return;
            int run = BeginComparison("Reading the file…");
            var path = CompareLeftBox.Text;
            LoadedSnapshotFile file;
            try
            {
                file = await FileWork(() => LoadForComparison(path));
            }
            catch (Exception ex)
            {
                file = new LoadedSnapshotFile(path, ex.Message);
            }
            if (!IsCurrentComparison(run)) return;
            if (!file.Loaded)
            {
                ComparisonBox.Text = OdriveSnapshotText.LoadedFile(file) + " Nothing was read from the board.";
                return;
            }
            // An earlier result must not stand there as if it were the answer to this one.
            ComparisonBox.Text = "Reading the board for the comparison…";
            CaptureSnapshot(file, run);
        }

        /// <summary>Offline: two files, no serial access of any kind.</summary>
        internal async void CompareFiles()
        {
            if (_closed) return;
            int run = BeginComparison("Comparing the files…");
            string leftPath = CompareLeftBox.Text, rightPath = CompareRightBox.Text;
            var catalog = _catalog;
            ComparisonShown shown;
            try
            {
                shown = await FileWork(() =>
                {
                    var left = LoadForComparison(leftPath);
                    var right = LoadForComparison(rightPath);
                    if (!left.Loaded || !right.Loaded)
                        return new ComparisonShown
                        {
                            Text = string.Join(Environment.NewLine, new[] { left, right }.Where(f => !f.Loaded).Select(OdriveSnapshotText.LoadedFile))
                                 + Environment.NewLine + "Nothing was compared.",
                        };
                    return Compared(left.ToSide(Path.GetFileName(left.ValuesPath)), right.ToSide(Path.GetFileName(right.ValuesPath)), catalog, left, right);
                });
            }
            catch (Exception ex)
            {
                shown = new ComparisonShown { Text = "Nothing was compared: " + ex.Message };
            }
            if (!IsCurrentComparison(run)) return;
            ShowComparison(shown, null);
        }

        private static LoadedSnapshotFile LoadForComparison(string text)
            => OdriveSnapshotFile.Load((text ?? "").Trim().Trim('"'));

        private void ShowComparison(ComparisonShown shown, CatalogSnapshot snapshot)
        {
            _comparison = shown.Comparison;
            _comparedSnapshot = shown.Comparison != null ? snapshot : null;
            ComparisonBox.Text = shown.Text;
        }

        // ---- Files -------------------------------------------------------------

        private void PickExportFile()
        {
            var snapshot = _snapshot;
            if (snapshot == null) return;
            OffThreadFileDialog.Save(Dispatcher, "Export the catalog snapshot", _lastFolder,
                                     OdriveSnapshotFile.FileName(snapshot.StartedUtc, snapshot.Completeness), JsonFilter,
                                     path => OnExportPicked(snapshot, path), _log);
        }

        /// <summary>What the export dialog calls with the chosen path.</summary>
        internal void OnExportPicked(CatalogSnapshot snapshot, string path) => _ = ExportSnapshotToAsync(snapshot, path);

        /// <summary>Writes the values file and its metadata off the window's thread, and says exactly what came of it.</summary>
        internal async Task<SnapshotExportResult> ExportSnapshotToAsync(CatalogSnapshot snapshot, string path)
        {
            int run = ++_fileRun;
            SnapshotFileText.Text = "Writing " + path + "…";
            SnapshotFileText.Foreground = BrushNeutral;
            var version = PluginVersion;
            SnapshotExportResult result;
            try
            {
                result = await FileWork(() => OdriveSnapshotFile.Export(snapshot, path, version));
            }
            catch (Exception ex)
            {
                result = new SnapshotExportResult(path, null, null, 0, ex.Message);
            }
            if (_closed || run != _fileRun) return result;

            if (result.Succeeded)
            {
                _lastFolder = Path.GetDirectoryName(result.ValuesPath);
                SnapshotFileText.Text = "Exported and read back: " + result.ValuesPath + " (" + result.ValueCount + " settings) and "
                                      + Path.GetFileName(result.MetadataPath) + ". "
                                      + (snapshot.Completeness == SnapshotCompleteness.Partial ? "PARTIAL snapshot, marked as such in both names and the metadata. " : "")
                                      + (snapshot.UncertainCount > 0 ? snapshot.UncertainCount + " values are not exact, the metadata names each with its reason. " : "")
                                      + "A catalog snapshot, no restore backup.";
                SnapshotFileText.Foreground = snapshot.Completeness == SnapshotCompleteness.Complete && snapshot.Capabilities.FloatsAreExact ? BrushOk : BrushWarn;
            }
            else
            {
                SnapshotFileText.Text = "Export FAILED, no snapshot file was written: " + result.Error;
                SnapshotFileText.Foreground = BrushDanger;
            }
            return result;
        }

        private void PickReportFile()
        {
            var snapshot = _snapshot;
            if (snapshot == null) return;
            OffThreadFileDialog.Save(Dispatcher, "Write the diagnostic report", _lastFolder, OdriveDiagnosticReport.FileName(DateTime.UtcNow), ReportFilter,
                                     path => OnReportPicked(snapshot, path), _log);
        }

        /// <summary>What the report dialog calls with the chosen path.</summary>
        internal void OnReportPicked(CatalogSnapshot snapshot, string path) => _ = WriteReportToAsync(snapshot, path);

        /// <summary>Writes the report of this snapshot, with the comparison that belongs to it, off the window's thread.
        /// Null on success, else why not.</summary>
        internal async Task<string> WriteReportToAsync(CatalogSnapshot snapshot, string path)
        {
            int run = ++_fileRun;
            // Taken now: what the window holds may change while the report is written.
            var comparison = _comparedSnapshot == snapshot ? _comparison : null;
            var version = PluginVersion;
            var now = DateTime.UtcNow;
            SnapshotFileText.Text = "Writing " + path + "…";
            SnapshotFileText.Foreground = BrushNeutral;

            string error, written = path;
            try
            {
                (error, written) = await FileWork(() => WriteReport(snapshot, comparison, version, now, path));
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
            if (_closed || run != _fileRun) return error;

            SnapshotFileText.Text = error == null ? "Report written and read back: " + written + "." : "Report FAILED: " + error;
            SnapshotFileText.Foreground = error == null ? BrushNeutral : BrushDanger;
            if (error == null) _lastFolder = Path.GetDirectoryName(written);
            return error;
        }

        private static (string Error, string Path) WriteReport(CatalogSnapshot snapshot, SnapshotComparison comparison, string version, DateTime now, string path)
        {
            try
            {
                path = Path.GetFullPath(path);
                var bytes = new UTF8Encoding(false).GetBytes(OdriveDiagnosticReport.Build(snapshot, comparison, version, now));
                using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                if (!File.ReadAllBytes(path).SequenceEqual(bytes))
                {
                    File.Delete(path);
                    return ("the report read back differently and was removed", path);
                }
                return (null, path);
            }
            catch (Exception ex)
            {
                return (ex.Message, path);
            }
        }
    }
}

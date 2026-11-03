using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace OdriveWheel.SimHubPlugin.Tests
{
    public static class EditorWindowTests
    {
        private const string StopLine = "w axis0.requested_state 1";

        private static readonly OdriveParameterCatalog Catalog = OdriveParameterCatalog.LoadEmbedded();
        private static readonly HashSet<string> PolledLines = new HashSet<string>(
            Catalog.Parameters.Where(p => p.Polled).Select(ReadLine));

        private static string ReadLine(ParameterDefinition p)
            => p.Protocol == FieldProtocol.Odrive ? OdriveProtocol.OdriveRead(p.Path) : OdriveProtocol.OffbRead(p.Path);

        private static void ClickRefresh(WindowTests.WindowRig rig, string tab)
        {
            Check.True(Wait.Until(() => rig.On(w => w.RefreshButton(tab).IsEnabled)), tab + " can be pressed");
            rig.Host.Invoke(w => WindowTests.Click(w.RefreshButton(tab)));
        }

        private static bool Finished(WindowTests.WindowRig rig, string tab)
            => Wait.Until(() => rig.On(w => w.RefreshSummary(tab).StartsWith("Read at", StringComparison.Ordinal)
                                         || w.RefreshSummary(tab).StartsWith("Aborted", StringComparison.Ordinal)), 8000);

        public static void Test_the_read_only_tabs_sit_between_status_and_extensions()
        {
            using (var rig = new WindowTests.WindowRig())
            {
                var headers = rig.On(w => w.MainTabs.Items.Cast<System.Windows.Controls.TabItem>().Select(t => (string)t.Header).ToArray());
                Check.Lines(new[] { "Status", "PSU / Brake", "Axis", "Motor", "Encoder", "Controller", "Snapshot", "Extensions" }, headers, "tabs");
            }
        }

        public static void Test_refresh_reads_the_tab_and_shows_the_values()
        {
            using (var rig = new WindowTests.WindowRig())
            {
                rig.Rig.Board.SetValue("axis0.motor.config.motor_type", "2");
                rig.Rig.Board.SetValue("axis0.motor.config.pre_calibrated", "1");
                rig.Rig.Board.SetValue("axis0.motor.config.current_lim", "25.000000");
                rig.Rig.Board.ClearLines();

                Check.Equal("—", rig.On(w => w.DisplayedValue("axis0.motor.config.current_lim")), "nothing before the refresh");
                ClickRefresh(rig, "motor");
                Check.True(Finished(rig, "motor"), "the refresh should finish");

                Check.True(rig.On(w => w.RefreshSummary("motor")).StartsWith("Read at", StringComparison.Ordinal), rig.On(w => w.RefreshSummary("motor")));
                Check.True(Wait.Until(() => rig.On(w => w.DisplayedValue("axis0.motor.config.current_lim")) == "25.000000 A"), "float");
                Check.Equal("2 — GIMBAL", rig.On(w => w.DisplayedValue("axis0.motor.config.motor_type")), "enum");
                Check.Equal("1 (true)", rig.On(w => w.DisplayedValue("axis0.motor.config.pre_calibrated")), "boolean");

                var expected = Catalog.InTab("motor").Select(ReadLine).OrderBy(l => l, StringComparer.Ordinal).ToArray();
                var actual = rig.Rig.Board.Lines.Where(l => !PolledLines.Contains(l)).OrderBy(l => l, StringComparer.Ordinal).ToArray();
                Check.Lines(expected, actual, "exactly this tab, each once");
            }
        }

        public static void Test_group_refresh_reads_only_that_group()
        {
            using (var rig = new WindowTests.WindowRig())
            {
                rig.Rig.Board.ClearLines();
                rig.Host.Invoke(w => WindowTests.Click(w.RefreshButton("encoder", "Hall")));
                Check.True(Finished(rig, "encoder"), "the refresh should finish");

                var actual = rig.Rig.Board.Lines.Where(l => !PolledLines.Contains(l)).ToArray();
                Check.Lines(new[] { "r axis0.encoder.config.hall_polarity", "r axis0.encoder.config.hall_polarity_calibrated",
                                    "r axis0.encoder.config.ignore_illegal_hall_state" }, actual, "the three Hall fields");
            }
        }

        public static void Test_only_the_short_status_list_is_read_without_a_request()
        {
            using (var rig = new WindowTests.WindowRig())
            {
                rig.Rig.Board.ClearLines();
                Thread.Sleep(2300);
                var lines = rig.Rig.Board.Lines;
                Check.True(lines.Length >= PolledLines.Count, "the status list is polled: " + lines.Length);
                foreach (var line in lines)
                    Check.True(PolledLines.Contains(line), "not part of the status list: " + line);
                Check.Equal("—", rig.On(w => w.DisplayedValue("ibus")), "a value that is not polled stays unread");
            }
        }

        public static void Test_every_refresh_button_sends_nothing_but_reads()
        {
            using (var rig = new WindowTests.WindowRig())
            {
                rig.Rig.Board.ClearLines();
                var keys = rig.On(w => w.ButtonKeys.ToArray());
                Check.True(keys.Length > 30, "tabs and groups: " + keys.Length);
                foreach (var key in keys)
                {
                    Check.True(Wait.Until(() => rig.On(w => w.RefreshButton("status").IsEnabled), 8000), "buttons are free again");
                    // Not through automation, which only queues the click: this disables the buttons at once.
                    rig.Host.Invoke(w =>
                    {
                        var parts = key.Split(new[] { '/' }, 2);
                        var button = parts.Length == 2 ? w.RefreshButton(parts[0], parts[1]) : w.RefreshButton(parts[0]);
                        button.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    });
                }
                Check.True(Wait.Until(() => rig.On(w => w.RefreshButton("status").IsEnabled), 8000), "all done");

                Check.True(rig.Rig.Board.WireLines.Length > 180, "lines: " + rig.Rig.Board.WireLines.Length);
                foreach (var line in rig.Rig.Board.WireLines) ParameterReaderTests.CheckIsReadOrSync(line);
                Check.Equal(0, rig.Rig.Board.CountLine(StopLine), "no state change");
                Check.Equal(0, rig.Rig.Board.CountLine("ss"), "no save");
                Check.Equal("8", rig.Rig.Board.Value("axis0.current_state"), "the axis state is unchanged");
            }
        }

        public static void Test_status_tab_decodes_errors()
        {
            using (var rig = new WindowTests.WindowRig())
            {
                rig.Rig.Board.SetValue("axis0.error", "320");
                Check.True(Wait.Until(() => rig.On(w => w.DisplayedValue("axis0.error")) == "0x00000140 — MOTOR_FAILED | ENCODER_FAILED"), "axis error");

                rig.Rig.Board.SetValue("error", "4");
                Check.True(Wait.Until(() => rig.On(w => w.DisplayedValue("error")) == "0x00000004 — DC_BUS_OVER_VOLTAGE"), "system error");

                rig.Rig.Board.SetValue("axis0.motor.error", "2147483648");
                Check.True(Wait.Until(() => rig.On(w => w.DisplayedValue("axis0.motor.error")) == "214748364 (may be cut off, not decoded)"), "cut off");
            }
        }

        public static void Test_old_values_do_not_pass_for_current_ones()
        {
            using (var rig = new WindowTests.WindowRig())
            {
                const string Path = "axis0.motor.config.current_lim";
                rig.Rig.Board.SetValue(Path, "25.000000");
                ClickRefresh(rig, "motor");
                Check.True(Wait.Until(() => rig.On(w => w.DisplayedValue(Path)) == "25.000000 A"), "read");

                rig.Rig.Board.Present = false;
                Check.True(Wait.Until(() => rig.On(w => w.DisplayedValue(Path)) == "outdated (was 25.000000 A)"), "marked while gone");
                Check.True(Wait.Until(() => rig.On(w => w.DisplayedValue("vbus_voltage")) == "outdated (was 48.000 V)"), "a polled value as well");

                ClickRefresh(rig, "motor");
                Check.True(Wait.Until(() => rig.On(w => w.RefreshSummary("motor")).StartsWith("Aborted after 0 of", StringComparison.Ordinal)), "says why nothing was read");

                rig.Rig.Board.SetValue(Path, "30.000000");
                rig.Rig.Board.Present = true;
                rig.Rig.WaitConnected();
                Thread.Sleep(300);
                Check.Equal("outdated (was 25.000000 A)", rig.On(w => w.DisplayedValue(Path)), "still marked after reconnecting");

                ClickRefresh(rig, "motor");
                Check.True(Wait.Until(() => rig.On(w => w.DisplayedValue(Path)) == "30.000000 A"), "the new value");
            }
        }

        public static void Test_stop_button_works_during_a_refresh()
        {
            using (var rig = new WindowTests.WindowRig())
            {
                rig.Rig.Board.ReplyDelayMs = 30;
                rig.Rig.Board.ClearLines();
                ClickRefresh(rig, "motor");
                var firstOfTab = ReadLine(Catalog.InTab("motor").First());
                Check.True(Wait.Until(() => rig.Rig.Board.CountLine(firstOfTab) == 1), "the refresh is under way");

                rig.Host.Invoke(w => WindowTests.Click(w.StopButton));
                Check.True(Wait.Until(() => rig.Rig.Service.GetStatus().LastStop?.IsFinished == true), "the stop should finish");
                Check.Equal(StopState.Confirmed, rig.Rig.Service.GetStatus().LastStop.State, "result");
                Check.True(rig.Rig.Service.GetStatus().LastStop.WrittenAfterMs < 300, "written after " + rig.Rig.Service.GetStatus().LastStop.WrittenAfterMs + " ms");
                Check.Equal("1", rig.Rig.Board.Value("axis0.current_state"), "IDLE");

                Check.True(Wait.Until(() => rig.On(w => w.RefreshSummary("motor")).Contains("software stop")), rig.On(w => w.RefreshSummary("motor")));
                Check.True(rig.On(w => w.RefreshButton("motor").IsEnabled), "can be pressed again");

                var afterStop = rig.Rig.Board.Lines.SkipWhile(l => l != StopLine).ToArray();
                var tabLines = new HashSet<string>(Catalog.InTab("motor").Select(ReadLine));
                Check.Equal(0, afterStop.Count(tabLines.Contains), "no read of the tab after the stop");
            }
        }

        public static void Test_port_button_works_during_a_refresh()
        {
            using (var rig = new WindowTests.WindowRig())
            {
                rig.Rig.Board.ReplyDelayMs = 30;
                rig.Rig.Board.ClearLines();
                ClickRefresh(rig, "controller");
                var firstOfTab = ReadLine(Catalog.InTab("controller").First());
                Check.True(Wait.Until(() => rig.Rig.Board.CountLine(firstOfTab) == 1), "the refresh is under way");

                rig.Host.Invoke(w => WindowTests.Click(w.PortButton));
                Check.True(Wait.Until(() => rig.Rig.Service.GetStatus().State == SerialConnectionState.Released, 2000), "released");
                Check.True(Wait.Until(() => rig.On(w => w.RefreshSummary("controller")).StartsWith("Aborted", StringComparison.Ordinal)), rig.On(w => w.RefreshSummary("controller")));

                rig.Host.Invoke(w => WindowTests.Click(w.PortButton));
                rig.Rig.WaitConnected();
                ClickRefresh(rig, "controller");
                Check.True(Wait.Until(() => rig.On(w => w.RefreshSummary("controller")).StartsWith("Reading", StringComparison.Ordinal)
                                         || rig.On(w => w.RefreshSummary("controller")).StartsWith("Read at", StringComparison.Ordinal)), "works again");
            }
        }

        public static void Test_closing_during_a_refresh_ends_it()
        {
            using (var rig = new WindowTests.WindowRig())
            {
                rig.Rig.Board.ReplyDelayMs = 30;
                rig.Rig.Board.ClearLines();
                ClickRefresh(rig, "axis0");
                var firstOfTab = ReadLine(Catalog.InTab("axis0").First());
                Check.True(Wait.Until(() => rig.Rig.Board.CountLine(firstOfTab) == 1), "the refresh is under way");

                rig.Host.Close(2000);
                Check.True(!rig.Host.IsOpen, "closed");

                Thread.Sleep(150);
                int lines = rig.Rig.Board.Lines.Length;
                Thread.Sleep(300);
                Check.Equal(lines, rig.Rig.Board.Lines.Length, "no more reads after closing");
            }
        }
    }
}

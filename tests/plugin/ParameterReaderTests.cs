using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OdriveWheel.SimHubPlugin.Tests
{
    public static class ParameterReaderTests
    {
        private static readonly OdriveParameterCatalog Catalog = OdriveParameterCatalog.LoadEmbedded();

        private sealed class ReaderRig : IDisposable
        {
            public Rig Rig { get; } = new Rig();
            public FakeBoard Board => Rig.Board;
            public OdriveParameterReader Reader { get; }

            public ReaderRig()
            {
                Reader = NewReader((p, priority, cancellation) => Rig.Parameters.ReadAsync(p.Path, p.Protocol, priority, cancellation));
            }

            public OdriveParameterReader NewReader(OdriveParameterReader.ReadParameter read)
                => new OdriveParameterReader(read, Rig.Service.GetStatus);

            public ParameterRefreshResult Refresh(params string[] paths) => RefreshAsync(paths).Result;

            public Task<ParameterRefreshResult> RefreshAsync(string[] paths)
                => Reader.RefreshAsync(paths.Select(Catalog.Find).ToList(), CommandPriority.Batch, CancellationToken.None);

            public ParameterReading Get(string path) => Reader.Get(Catalog.Find(path));

            public void Dispose() => Rig.Dispose();
        }

        // ---- What a reply turns into -----------------------------------------

        private static string Shown(string path, string value) => OdriveParameterFormat.Format(Catalog.Find(path), value).Text;

        public static void Test_values_are_shown_by_type()
        {
            Check.Equal("25.000000 A", Shown("axis0.motor.config.current_lim", "25.000000"), "float with unit");
            Check.Equal("1 (true)", Shown("config.enable_brake_resistor", "1"), "true");
            Check.Equal("0 (false)", Shown("config.enable_brake_resistor", "0"), "false");
            Check.Equal("7", Shown("axis0.motor.config.pole_pairs", "7"), "integer");
            Check.Equal("2 — GIMBAL", Shown("axis0.motor.config.motor_type", "2"), "enum");
            Check.Equal("1 — ?", Shown("axis0.motor.config.motor_type", "1"), "a number without a name stays visible");
            Check.Equal("261 — SPI_ABS_MT6835", Shown("axis0.encoder.config.mode", "261"), "encoder mode");
            Check.Equal("inf A", Shown("config.dc_max_positive_current", "inf"), "the firmware default of a current limit");
            Check.Equal("19", Shown("sys.vbusdiv", "19"), "OpenFFBoard integer");
            Check.Equal("8 — CLOSED_LOOP_CONTROL", Shown("axis0.current_state", "8"), "axis state");
        }

        public static void Test_errors_are_decoded_with_the_right_table()
        {
            Check.Equal("0 (no error)", Shown("axis0.error", "0"), "no error");
            Check.Equal("0x00000004 — DC_BUS_OVER_VOLTAGE", Shown("error", "4"), "system");
            Check.Equal("0x00000140 — MOTOR_FAILED | ENCODER_FAILED", Shown("axis0.error", "320"), "axis");
            Check.Equal("0x00000008 — DRV_FAULT", Shown("axis0.motor.error", "8"), "motor");
            Check.Equal("0x00000004 — NO_RESPONSE", Shown("axis0.encoder.error", "4"), "encoder");
            Check.Equal("0x00000001 — OVERSPEED", Shown("axis0.controller.error", "1"), "controller");
        }

        // 0x80000000 is 2147483648, of which the board sends 214748364. Decoded as it arrives
        // that would be a list of errors the board does not have.
        public static void Test_a_number_that_fills_the_reply_is_not_trusted()
        {
            Check.Equal("214748364 (may be cut off, not decoded)", Shown("axis0.motor.error", "214748364"), "error bits");
            Check.Equal("99999999", Shown("axis0.controller.config.steps_per_circular_range", "99999999"), "8 digits are complete");
            Check.Equal("123456789 (may be cut off)", Shown("axis0.controller.config.steps_per_circular_range", "123456789"), "integer");
            Check.Equal("123456789 (may be cut off)", Shown("serial_number", "123456789"), "serial number");
            Check.Equal("123456789 A (may be cut off)", Shown("axis0.motor.config.current_lim", "123456789"), "float without a point");
            Check.Equal("100.00000 A", Shown("axis0.motor.config.current_lim", "100.00000"), "here only decimals are gone");
        }

        public static void Test_a_reply_of_the_wrong_kind_is_no_value()
        {
            foreach (var path in new[] { "axis0.motor.config.current_lim", "axis0.motor.config.pole_pairs", "config.enable_brake_resistor",
                                         "axis0.motor.config.motor_type", "axis0.error", "axis0.current_state" })
            {
                var formatted = OdriveParameterFormat.Format(Catalog.Find(path), "hu");
                Check.Equal("unexpected reply (hu)", formatted.Text, path);
                Check.True(!formatted.IsValid, path + " is not a value");
            }
        }

        // ---- Reading ---------------------------------------------------------

        public static void Test_refresh_reads_each_parameter_once_in_order()
        {
            using (var rig = new ReaderRig())
            {
                rig.Board.SetValue("sys.vbusdiv", "19");
                rig.Board.SetValue("config.brake_resistance", "2.000000");

                var result = rig.Refresh("config.brake_resistance", "sys.vbusdiv", "axis0.error");

                Check.Lines(new[] { "r config.brake_resistance", "sys.vbusdiv?", "r axis0.error" }, rig.Board.Lines, "lines");
                Check.Equal(3, result.Succeeded, "succeeded");
                Check.Equal(null, result.AbortReason, "complete");
                Check.Equal("19", rig.Get("sys.vbusdiv").Text, "value");
                Check.Equal(ParameterReadState.Current, rig.Get("sys.vbusdiv").State, "state");
                Check.True(rig.Get("sys.vbusdiv").ReadAt != null, "read time");
            }
        }

        public static void Test_every_catalog_read_of_the_window_is_a_read_and_nothing_else()
        {
            using (var rig = new ReaderRig())
            {
                var tabs = new[] { "status", "odrv", "axis0", "motor", "encoder", "controller" };
                var paths = Catalog.Parameters.Where(p => tabs.Contains(p.Tab)).Select(p => p.Path).ToArray();
                Check.Equal(186, paths.Length, "parameters of the window");

                var result = rig.Refresh(paths);

                Check.Equal(paths.Length, result.Succeeded + result.Failed, "all attempted");
                Check.Equal(paths.Length, rig.Board.Lines.Length, "one line each");
                foreach (var line in rig.Board.WireLines) CheckIsReadOrSync(line);
            }
        }

        /// <summary>A read of one of the two dialects, or a sync line of the service.</summary>
        internal static void CheckIsReadOrSync(string line)
        {
            bool odriveRead = line.StartsWith("r ", StringComparison.Ordinal) && line.Split(' ').Length == 2;
            bool offbRead = line.EndsWith("?", StringComparison.Ordinal) && line.IndexOf('=') < 0 && line.IndexOf('!') < 0;
            bool odriveSync = line.StartsWith("f ", StringComparison.Ordinal);
            Check.True(odriveRead || offbRead || odriveSync, "only reads go out: " + line);
            Check.True(!OdriveCommandGuard.IsBlockedWhileStopped(line), "nothing the stop would block: " + line);
        }

        public static void Test_unsupported_and_refused_parameters_say_so()
        {
            using (var rig = new ReaderRig())
            {
                rig.Board.ReplyOverride = line =>
                {
                    switch (line)
                    {
                        case "r axis0.motor.config.pole_pairs": return "invalid property";
                        case "r axis0.motor.config.motor_type": return "not implemented";
                        case "sys.vbusdiv?":                    return "[sys.vbusdiv?|NOT_FOUND]";
                        case "sys.build?":                      return "[sys.build?|ERR]";
                        default: return null;
                    }
                };

                var result = rig.Refresh("axis0.motor.config.pole_pairs", "axis0.motor.config.motor_type", "sys.vbusdiv", "sys.build", "axis0.error");

                Check.Equal("not supported by this firmware (invalid property)", rig.Get("axis0.motor.config.pole_pairs").Text, "invalid property");
                Check.Equal("not readable (not implemented)", rig.Get("axis0.motor.config.motor_type").Text, "not implemented");
                Check.Equal("not supported by this firmware (NOT_FOUND)", rig.Get("sys.vbusdiv").Text, "NOT_FOUND");
                Check.Equal("not readable (ERR)", rig.Get("sys.build").Text, "ERR");
                Check.Equal(ParameterReadState.Failed, rig.Get("axis0.motor.config.pole_pairs").State, "state");
                Check.Equal(4, result.Failed, "failed");
                Check.Equal(1, result.Succeeded, "the next one still gets read");
            }
        }

        public static void Test_a_failed_read_replaces_the_earlier_value()
        {
            using (var rig = new ReaderRig())
            {
                rig.Board.SetValue("axis0.motor.config.current_lim", "25.000000");
                rig.Refresh("axis0.motor.config.current_lim");
                Check.Equal("25.000000 A", rig.Get("axis0.motor.config.current_lim").Text, "first read");

                rig.Board.ReplyOverride = line => line.StartsWith("r axis0.motor", StringComparison.Ordinal) ? "not implemented" : null;
                rig.Refresh("axis0.motor.config.current_lim");

                var reading = rig.Get("axis0.motor.config.current_lim");
                Check.Equal("not readable (not implemented)", reading.Text, "the old value is gone");
                Check.Equal(null, reading.ReadAt, "no read time");
            }
        }

        public static void Test_a_reply_of_the_wrong_kind_counts_as_failed()
        {
            using (var rig = new ReaderRig())
            {
                rig.Board.SetValue("axis0.motor.config.pole_pairs", "hu");
                var result = rig.Refresh("axis0.motor.config.pole_pairs");

                Check.Equal(1, result.Failed, "failed");
                Check.Equal(ParameterReadState.Failed, rig.Get("axis0.motor.config.pole_pairs").State, "state");
                Check.Equal("unexpected reply (hu)", rig.Get("axis0.motor.config.pole_pairs").Text, "text");
            }
        }

        public static void Test_a_long_error_number_arrives_cut_and_is_not_decoded()
        {
            using (var rig = new ReaderRig())
            {
                rig.Board.SetValue("axis0.motor.error", "2147483648");
                rig.Refresh("axis0.motor.error");
                Check.Equal("214748364 (may be cut off, not decoded)", rig.Get("axis0.motor.error").Text, "text");
            }
        }

        public static void Test_one_timeout_goes_on_and_a_silent_board_ends_the_refresh()
        {
            using (var rig = new ReaderRig())
            {
                // Only this reply gets lost, the board answers again before the timeout is over.
                rig.Board.ReplyOverride = line =>
                {
                    if (line == "r axis0.motor.config.pole_pairs")
                    {
                        rig.Board.MuteOdriveChannel = true;
                        Task.Delay(200).ContinueWith(_ => rig.Board.MuteOdriveChannel = false);
                    }
                    return null;
                };
                var once = rig.Refresh("axis0.motor.config.pole_pairs", "axis0.motor.config.torque_lim");
                Check.Equal("no reply", rig.Get("axis0.motor.config.pole_pairs").Text, "lost reply");
                Check.Equal(ParameterReadState.Current, rig.Get("axis0.motor.config.torque_lim").State, "the next one is fine");
                Check.Equal(null, once.AbortReason, "goes on");

                rig.Board.ReplyOverride = null;
                rig.Board.ClearLines();
                rig.Board.MuteOdriveChannel = true;
                var paths = Catalog.InTab("encoder").Select(p => p.Path).ToArray();
                var silent = rig.Refresh(paths);

                Check.Equal("the board does not answer", silent.AbortReason, "reason");
                Check.True(silent.NotReached >= paths.Length - OdriveParameterReader.MaxTimeoutsInARow, "not reached: " + silent.NotReached);
                Check.True(rig.Board.Lines.Length <= OdriveParameterReader.MaxTimeoutsInARow, "lines: " + rig.Board.Lines.Length);
            }
        }

        // ---- Old values ------------------------------------------------------

        public static void Test_a_value_is_outdated_as_soon_as_the_board_is_gone_and_after_it_is_back()
        {
            using (var rig = new ReaderRig())
            {
                rig.Refresh("vbus_voltage");
                Check.Equal("48.000 V", rig.Get("vbus_voltage").Text, "read");
                int generation = rig.Rig.Service.GetStatus().ConnectGeneration;

                rig.Board.Present = false;
                Check.True(Wait.Until(() => !rig.Rig.Service.GetStatus().IsConnected), "disconnected");

                Check.Equal(ParameterReadState.Outdated, rig.Get("vbus_voltage").State, "state while gone");
                Check.Equal("outdated (was 48.000 V)", rig.Get("vbus_voltage").Text, "text while gone");
                Check.Equal(null, rig.Get("vbus_voltage").ReadAt, "no read time");

                rig.Board.Present = true;
                rig.Rig.WaitConnected();
                Check.True(rig.Rig.Service.GetStatus().ConnectGeneration > generation, "a new connection");
                Check.Equal(ParameterReadState.Outdated, rig.Get("vbus_voltage").State, "still outdated: the board may have rebooted");

                rig.Board.SetValue("vbus_voltage", "47.500");
                rig.Refresh("vbus_voltage");
                Check.Equal("47.500 V", rig.Get("vbus_voltage").Text, "read again");
                Check.Equal(ParameterReadState.Current, rig.Get("vbus_voltage").State, "current again");
            }
        }

        public static void Test_port_release_makes_values_outdated_and_a_refresh_says_why_nothing_was_read()
        {
            using (var rig = new ReaderRig())
            {
                rig.Refresh("vbus_voltage");
                rig.Rig.Service.ReleasePort();
                Check.True(Wait.Until(() => rig.Rig.Service.GetStatus().State == SerialConnectionState.Released), "released");

                Check.Equal(ParameterReadState.Outdated, rig.Get("vbus_voltage").State, "state");

                rig.Board.ClearLines();
                var result = rig.Refresh("vbus_voltage", "axis0.error");
                Check.True(result.AbortReason != null && result.AbortReason.Contains("released"), "reason: " + result.AbortReason);
                Check.Equal(2, result.NotReached, "nothing was attempted");
                Check.Equal(0, rig.Board.Lines.Length, "nothing sent");

                rig.Rig.Service.AcquirePort();
                rig.Rig.WaitConnected();
                Check.Equal(ParameterReadState.Outdated, rig.Get("vbus_voltage").State, "stays outdated after taking over");
            }
        }

        public static void Test_a_failure_does_not_survive_a_new_connection()
        {
            using (var rig = new ReaderRig())
            {
                rig.Board.ReplyOverride = line => line == "r vbus_voltage" ? "not implemented" : null;
                rig.Refresh("vbus_voltage");
                Check.Equal(ParameterReadState.Failed, rig.Get("vbus_voltage").State, "failed");

                rig.Board.Present = false;
                Check.True(Wait.Until(() => !rig.Rig.Service.GetStatus().IsConnected), "disconnected");
                Check.Equal(ParameterReadState.NotRead, rig.Get("vbus_voltage").State, "says nothing any more");
                Check.Equal("—", rig.Get("vbus_voltage").Text, "text");
            }
        }

        public static void Test_a_connection_lost_during_a_refresh_ends_it()
        {
            using (var rig = new ReaderRig())
            {
                rig.Board.ReplyOverride = line =>
                {
                    if (line == "r axis0.encoder.config.cpr") rig.Board.FailNextRead = true;
                    return null;
                };
                var paths = Catalog.InTab("encoder").Select(p => p.Path).ToArray();
                Check.True(paths[1] == "axis0.encoder.config.cpr", "second parameter of the tab");
                int generation = rig.Rig.Service.GetStatus().ConnectGeneration;

                var result = rig.Refresh(paths);

                Check.True(result.AbortReason != null, "ended early");
                Check.Equal(paths.Length - 2, result.NotReached, "the rest is not read");
                Check.Equal(0, rig.Board.CountLine("r axis0.encoder.config.bandwidth"), "nothing goes out after it");

                // The read can report the failure just before the status shows it.
                Check.True(Wait.Until(() =>
                {
                    var status = rig.Rig.Service.GetStatus();
                    return status.IsConnected && status.ConnectGeneration > generation;
                }), "reconnected");
                Check.Equal(ParameterReadState.Outdated, rig.Get(paths[0]).State, "what was read belongs to the old connection");
            }
        }

        // ---- Stop and port during a refresh ------------------------------------

        public static void Test_stop_during_a_refresh_goes_first_and_ends_it()
        {
            using (var rig = new ReaderRig())
            {
                var paths = Catalog.InTab("motor").Select(p => p.Path).ToArray();
                rig.Refresh(paths[0]);
                var earlier = rig.Get(paths[0]).Text;

                rig.Board.ClearLines();
                rig.Board.ReplyDelayMs = 20;
                var running = rig.RefreshAsync(paths);
                Check.True(Wait.Until(() => rig.Board.Lines.Length >= 2), "the refresh is under way");

                var ticket = rig.Rig.Service.RequestStop();
                Check.True(ticket.WaitForResult(3000), "the stop should finish");
                Check.Equal(StopState.Confirmed, ticket.State, "result");
                Check.True(ticket.WrittenAfterMs < 300, "written after " + ticket.WrittenAfterMs + " ms");

                Check.True(running.Wait(3000), "the refresh should end");
                Check.True(running.Result.AbortReason != null && running.Result.AbortReason.Contains("software stop"), "reason: " + running.Result.AbortReason);
                Check.True(running.Result.NotReached > paths.Length / 2, "not reached: " + running.Result.NotReached);

                var afterStop = rig.Board.Lines.SkipWhile(l => l != "w axis0.requested_state 1").ToArray();
                Check.True(afterStop.Length > 0, "the stop went out");
                foreach (var path in paths.Skip(paths.Length / 2))
                    Check.True(!afterStop.Contains("r " + path), path + " is not read after the stop");

                Check.Equal(earlier, rig.Get(paths[0]).Text, "a read value stays");
            }
        }

        // The service only cancels what it holds. Between two reads it holds nothing of this
        // refresh, and the stop must still end it.
        public static void Test_stop_between_two_reads_ends_the_refresh()
        {
            using (var rig = new ReaderRig())
            {
                int reads = 0;
                var reader = rig.NewReader(async (p, priority, cancellation) =>
                {
                    var result = await rig.Rig.Parameters.ReadAsync(p.Path, p.Protocol, priority, cancellation);
                    if (Interlocked.Increment(ref reads) == 2) rig.Rig.Service.RequestStop().WaitForResult(3000);
                    return result;
                });
                var parameters = Catalog.InTab("encoder").ToList();

                var result2 = reader.RefreshAsync(parameters, CommandPriority.Batch, CancellationToken.None).Result;

                Check.Equal(2, reads, "no read after the stop");
                Check.Equal("software stop", result2.AbortReason, "reason");
                Check.Equal(parameters.Count - 2, result2.NotReached, "not reached");
                Check.True(!rig.Board.Lines.Contains("r " + parameters[2].Path), "the third never went out");
            }
        }

        // The service answers a running read at once, its stop token only follows when the
        // worker gets to the stop. Going by the token, this was "cancelled" depending on timing.
        public static void Test_a_read_cancelled_by_the_stop_names_the_stop()
        {
            using (var rig = new ReaderRig())
            {
                var reader = rig.NewReader((p, priority, cancellation) =>
                {
                    rig.Rig.Service.RequestStop();
                    return Task.FromResult(new ParameterResult(ParameterStatus.Cancelled, detail: "software stop, not sent"));
                });

                var result = reader.RefreshAsync(Catalog.InTab("encoder").ToList(), CommandPriority.Batch, CancellationToken.None).Result;

                Check.Equal("software stop", result.AbortReason, "reason");
            }
        }

        public static void Test_a_refresh_during_a_running_stop_reads_nothing()
        {
            using (var rig = new ReaderRig())
            {
                rig.Board.StallWrites();
                rig.Rig.Service.RequestStop();
                int reads = 0;
                var reader = rig.NewReader((p, priority, cancellation) =>
                {
                    Interlocked.Increment(ref reads);
                    return rig.Rig.Parameters.ReadAsync(p.Path, p.Protocol, priority, cancellation);
                });

                var result = reader.RefreshAsync(Catalog.InTab("encoder").ToList(), CommandPriority.Batch, CancellationToken.None).Result;

                Check.Equal(0, reads, "no read");
                Check.Equal("software stop", result.AbortReason, "reason");
            }
        }

        // Reading stays possible after a stop, the latch only blocks what can start the motor.
        public static void Test_a_finished_stop_does_not_end_the_next_refresh()
        {
            using (var rig = new ReaderRig())
            {
                Check.True(rig.Rig.Service.RequestStop().WaitForResult(3000), "the stop should finish");
                Check.True(rig.Rig.Service.GetStatus().StopLatched, "latched");

                var result = rig.Refresh("vbus_voltage", "axis0.error");

                Check.Equal(null, result.AbortReason, "complete");
                Check.Equal(2, result.Succeeded, "both read");
            }
        }

        public static void Test_cancelling_is_not_called_a_stop()
        {
            using (var rig = new ReaderRig())
            {
                var parameters = Catalog.InTab("encoder").ToList();
                var before = rig.Reader.RefreshAsync(parameters, CommandPriority.Batch, new CancellationToken(true)).Result;
                Check.Equal("cancelled", before.AbortReason, "before the first read");

                var reader = rig.NewReader((p, priority, cancellation)
                    => Task.FromResult(new ParameterResult(ParameterStatus.Cancelled, detail: "cancelled, not sent")));
                var during = reader.RefreshAsync(parameters, CommandPriority.Batch, CancellationToken.None).Result;
                Check.Equal("cancelled, not sent", during.AbortReason, "during a read");
            }
        }

        public static void Test_a_read_without_a_way_to_the_board_ends_the_refresh()
        {
            using (var rig = new ReaderRig())
            {
                // The connection still counts as up, for example with a full queue.
                int reads = 0;
                var reader = rig.NewReader((p, priority, cancellation) =>
                {
                    Interlocked.Increment(ref reads);
                    return Task.FromResult(new ParameterResult(ParameterStatus.Unavailable, detail: "queue full"));
                });
                var parameters = Catalog.InTab("encoder").ToList();

                var result = reader.RefreshAsync(parameters, CommandPriority.Batch, CancellationToken.None).Result;

                Check.Equal(1, reads, "ends at once");
                Check.Equal("queue full", result.AbortReason, "reason");
                Check.Equal(parameters.Count - 1, result.NotReached, "not reached");
            }
        }

        public static void Test_port_release_during_a_refresh_ends_it()
        {
            using (var rig = new ReaderRig())
            {
                var paths = Catalog.InTab("motor").Select(p => p.Path).ToArray();
                rig.Board.ReplyDelayMs = 20;
                var running = rig.RefreshAsync(paths);
                Check.True(Wait.Until(() => rig.Board.Lines.Length >= 2), "the refresh is under way");

                rig.Rig.Service.ReleasePort();
                Check.True(Wait.Until(() => rig.Rig.Service.GetStatus().State == SerialConnectionState.Released, 2000), "released during the refresh");

                Check.True(running.Wait(3000), "the refresh should end");
                Check.True(running.Result.AbortReason != null, "ended early");
                Check.True(running.Result.NotReached > paths.Length / 2, "not reached: " + running.Result.NotReached);
                Check.Equal(ParameterReadState.Outdated, rig.Get(paths[0]).State, "no value counts as current");
            }
        }

        // ---- The line above the list -------------------------------------------

        public static void Test_summary_names_what_is_missing()
        {
            var at = new DateTime(2026, 1, 2, 3, 4, 5);
            Check.Equal("Read at 03:04:05: 3 ok.", OdriveStatusText.Refresh(new ParameterRefreshResult(3, 3, 0, null), at), "all");
            Check.Equal("Read at 03:04:05: 2 ok, 1 failed.", OdriveStatusText.Refresh(new ParameterRefreshResult(3, 2, 1, null), at), "one failed");
            Check.Equal("Aborted after 1 of 3: the board does not answer. The rest was not read.",
                        OdriveStatusText.Refresh(new ParameterRefreshResult(3, 1, 0, "the board does not answer"), at), "aborted");
        }
    }
}

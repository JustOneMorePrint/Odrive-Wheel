using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace OdriveWheel.SimHubPlugin.Tests
{
    /// <summary>The real serial service, parameter service and firmware check on a simulated board, with a capture on top.</summary>
    internal sealed class SnapshotRig : IDisposable
    {
        public Rig Rig { get; }
        public OdriveCapabilityDetector Detector { get; }
        public OdriveParameterCatalog Catalog { get; } = OdriveParameterCatalog.LoadEmbedded();
        public OdriveSnapshotCapture Capture { get; }

        public SnapshotRig(Action<FakeBoard> prepare = null, bool waitForCheck = true)
        {
            Rig = new Rig(prepare: prepare ?? Current);
            Detector = new OdriveCapabilityDetector(Rig.Service, Rig.Parameters);
            Detector.Start();
            if (waitForCheck) WaitForCheck();
            Capture = new OdriveSnapshotCapture(Rig.Parameters, Rig.Service.GetStatus, Catalog, Detector.StatusFor);
            Rig.Board.ClearLines();
        }

        public FakeBoard Board => Rig.Board;
        public OdriveSerialService Service => Rig.Service;

        public void WaitForCheck()
            => Check.True(Wait.Until(() => Detector.Status.State != FirmwareCapabilityState.Unknown), "the firmware check ends");

        public CatalogSnapshot Run(IProgress<SnapshotProgress> progress = null, CancellationToken cancellation = default)
            => Capture.CaptureAsync(progress, cancellation).Result;

        /// <summary>Lets the board answer like current firmware, with some values that are easy to get wrong.</summary>
        public static void Current(FakeBoard board)
        {
            board.BecomeCurrentFirmware();
            board.SetValue("serial_number", "345678901234");
            board.SetValue("axis0.motor.error", "2147483648");
            board.SetValue("axis0.motor.config.phase_inductance", "0.0000123456");
            board.SetValue("config.dc_max_positive_current", "inf");
            board.SetValue("config.dc_max_negative_current", "-10");
            board.SetValue("axis0.encoder.config.phase_offset_float", "-0");
            board.SetValue("config.enable_brake_resistor", "1");
            board.SetValue("axis0.motor.config.motor_type", "2");
            board.SetValue("sys.vbusdiv", "19");
            board.SetValue("axis.maxtorque", "5.00");
            board.SetValue("fx.master", "255");
            board.SetValue("axis0.motor.fet_thermistor.temperature", "41.25");
            board.SetValue("axis0.motor.fet_thermistor.config.enabled", "1");
            board.SetValue("axis0.motor.fet_thermistor.config.temp_limit_lower", "100");
            board.SetValue("axis0.motor.fet_thermistor.config.temp_limit_upper", "120");
            board.SetValue("fw_version_major", "0");
            board.SetValue("fw_version_minor", "5");
            board.SetValue("fw_version_revision", "6");
        }

        public static void Older(FakeBoard board)
        {
            Current(board);
            board.BecomeOlderFirmware();
        }

        /// <summary>The line of a catalog read.</summary>
        public static string ReadLine(ParameterDefinition p)
            => p.Protocol == FieldProtocol.Odrive ? OdriveProtocol.OdriveRead(p.Path) : OdriveProtocol.OffbRead(p.Path);

        public HashSet<string> CatalogLines => new HashSet<string>(Catalog.Parameters.Select(ReadLine));

        public void Dispose()
        {
            Detector.Stop();
            Rig.Dispose();
        }
    }

    /// <summary>Progress that runs on the capture's own thread, so a test can act at an exact point.</summary>
    internal sealed class Hook : IProgress<SnapshotProgress>
    {
        private readonly Action<SnapshotProgress> _action;
        public Hook(Action<SnapshotProgress> action) => _action = action;
        public void Report(SnapshotProgress value) => _action(value);
    }

    /// <summary>The real parameter service, with an action right before the nth read is handed to it: after the capture's
    /// own check, before the serial worker sees the request. Only the worker can hold the line back then.</summary>
    internal sealed class HookedParameters : IOdriveParameters
    {
        private readonly IOdriveParameters _inner;
        private readonly int _at;
        private readonly Action _action;
        private int _reads;

        public HookedParameters(IOdriveParameters inner, int at, Action action)
        {
            _inner = inner;
            _at = at;
            _action = action;
        }

        public System.Threading.Tasks.Task<ParameterResult> ReadAsync(string path, FieldProtocol protocol, CommandPriority priority = CommandPriority.Interactive,
                                                                       CancellationToken cancellation = default, SerialOperationContext context = null)
        {
            if (Interlocked.Increment(ref _reads) == _at) _action();
            return _inner.ReadAsync(path, protocol, priority, cancellation, context);
        }

        public System.Threading.Tasks.Task<ParameterResult> WriteAsync(string path, string value, FieldProtocol protocol, CommandPriority priority = CommandPriority.Interactive,
                                                                        CancellationToken cancellation = default, SerialOperationContext context = null)
            => throw new InvalidOperationException("a capture never writes");
        public System.Threading.Tasks.Task<ParameterResult> ExecOffbAsync(string path, CommandPriority priority = CommandPriority.Interactive, CancellationToken cancellation = default)
            => throw new InvalidOperationException("a capture never executes");
        public System.Threading.Tasks.Task<ParameterResult> SaveOdriveAsync() => throw new InvalidOperationException("a capture never saves");
        public ParameterResult Read(string path, FieldProtocol protocol, CommandPriority priority = CommandPriority.Interactive, CancellationToken cancellation = default)
            => throw new InvalidOperationException("not used");
        public ParameterResult Write(string path, string value, FieldProtocol protocol, CommandPriority priority = CommandPriority.Interactive, CancellationToken cancellation = default)
            => throw new InvalidOperationException("a capture never writes");
        public ParameterResult ExecOffb(string path, CommandPriority priority = CommandPriority.Interactive, CancellationToken cancellation = default)
            => throw new InvalidOperationException("a capture never executes");
        public ParameterResult SaveOdrive() => throw new InvalidOperationException("a capture never saves");
    }

    public static class SnapshotCaptureTests
    {
        private const string StopLine = "w axis0.requested_state 1";

        // Between the capture's check and the worker: a stop that has already finished, a cleared latch, a new state of
        // the firmware check. Each must hold the line back in the worker, as nothing else is left to do it.
        public static void Test_a_change_between_the_check_and_the_worker_holds_the_line_back()
        {
            var changes = new (string Name, Action<SnapshotRig> Change, bool Latched, bool Older)[]
            {
                ("finished stop", r => { r.Rig.HidState = 1; var t = r.Service.RequestStop(); t.WaitForResult(3000); }, false, false),
                ("finished stop on a set latch", r => { var t = r.Service.RequestStop(); t.WaitForResult(3000); }, true, false),
                ("cleared latch", r => r.Service.ClearStopLatch(), true, false),
                ("new firmware check", r => r.Detector.CheckAgain(), false, true),
            };
            foreach (var (name, change, latched, older) in changes)
            {
                using (var rig = new SnapshotRig(older ? (Action<FakeBoard>)SnapshotRig.Older : null))
                {
                    if (latched)
                    {
                        rig.Rig.HidState = 1;
                        rig.Service.RequestStop();
                        Check.True(Wait.Until(() => rig.Service.GetStatus().LastStop.IsFinished), name + ": the first stop finishes");
                    }
                    rig.Board.ClearLines();
                    var path = rig.Catalog.Parameters[29];
                    var hooked = new HookedParameters(rig.Rig.Parameters, 30, () => change(rig));
                    var capture = new OdriveSnapshotCapture(hooked, rig.Service.GetStatus, rig.Catalog, rig.Detector.StatusFor);

                    var snapshot = capture.CaptureAsync(null, CancellationToken.None).Result;
                    Check.Equal(SnapshotCompleteness.Partial, snapshot.Completeness, name + ": partial");
                    Check.Equal(29, snapshot.ReadCount, name + ": the values before it");
                    Check.Equal(0, rig.Board.CountLine(SnapshotRig.ReadLine(path)), name + ": the line was held back: " + SnapshotRig.ReadLine(path));
                    Check.Equal(SnapshotReadOutcome.NotRead, snapshot.Entries[29].Outcome, name + ": named as not read");
                }
            }
        }

        // Acts on the board when it receives the nth line of the capture, on the serial worker, before it answers.
        private static void OnCatalogLine(SnapshotRig rig, int n, Action action)
        {
            int seen = 0;
            var lines = rig.CatalogLines;
            rig.Board.ReplyOverride = line =>
            {
                if (lines.Contains(line) && Interlocked.Increment(ref seen) == n) action();
                return null;
            };
        }

        public static void Test_a_capture_reads_every_catalog_path_once_and_keeps_the_replies()
        {
            using (var rig = new SnapshotRig())
            {
                var snapshot = rig.Run();

                Check.Equal(SnapshotCompleteness.Complete, snapshot.Completeness, "complete: " + snapshot.AbortReason);
                Check.Equal(rig.Catalog.Parameters.Count, snapshot.Expected, "every catalog path is expected");
                Check.Equal(snapshot.Expected, snapshot.ReadCount, "and read");
                Check.Lines(rig.Catalog.Parameters.Select(SnapshotRig.ReadLine).ToArray(), rig.Board.Lines, "each path once, in catalog order, nothing else");
                foreach (var line in rig.Board.WireLines) ParameterReaderTests.CheckIsReadOrSync(line);

                // Exactly what the board sent, never formatted or decoded.
                Check.Equal("345678901234", snapshot.Find("serial_number").Reply, "a long integer whole");
                Check.Equal("2147483648", snapshot.Find("axis0.motor.error").Reply, "error bits as the number");
                var inductance = snapshot.Find("axis0.motor.config.phase_inductance").Reply;
                Check.True(inductance.Length > OdriveProtocol.OdriveReadLimit && inductance.Contains("e-05"), "%.9g of the float32, longer than 9 characters: " + inductance);
                Check.True(OdriveFloatText.TryParseFloat32(inductance, out float henry) && henry == (float)0.0000123456, "and the same float32");
                Check.Equal("inf", snapshot.Find("config.dc_max_positive_current").Reply, "infinity");
                Check.Equal("5.00", snapshot.Find("axis.maxtorque").Reply, "an OpenFFBoard value");
                Check.Equal("abc branch=test", snapshot.Find("sys.build").Reply, "the build id");

                Check.True(snapshot.Capabilities.FloatsAreExact, "declared exact");
                Check.Equal(FakeBoard.Declaration, snapshot.Capabilities.Declaration, "the declaration as it came");
                Check.Equal(0, snapshot.Entries.Count(e => e.Certainty != ReplyCertainty.Exact && e.Definition.Protocol == FieldProtocol.Odrive), "every r reply exact");
                Check.Equal(ReplyCertainty.MayBeRounded, snapshot.Find("axis.maxtorque").Certainty, "%.2f of OpenFFBoard is not covered by the declaration");
                Check.True(snapshot.StartedUtc <= snapshot.EndedUtc, "a period, not a moment");
                Check.Equal(rig.Service.GetStatus().ConnectGeneration, snapshot.ConnectGeneration, "bound to its connection");
            }
        }

        public static void Test_settings_and_status_values_are_told_apart()
        {
            using (var rig = new SnapshotRig())
            {
                var snapshot = rig.Run();
                var settings = snapshot.ConfigurationValues().Select(p => p.Key).ToList();
                var status = snapshot.StatusValues().Select(p => p.Key).ToList();

                Check.True(settings.Contains("config.brake_resistance") && settings.Contains("sys.vbusdiv") && settings.Contains("fx.master"), "settings");
                foreach (var path in new[] { "vbus_voltage", "axis0.current_state", "axis0.requested_state", "error", "sys.build", "serial_number",
                                             "axis0.controller.config.anticogging.index" })
                    Check.True(status.Contains(path) && !settings.Contains(path), "status, never in the values file: " + path);
                Check.Equal(snapshot.Expected, settings.Count + status.Count, "every path in one of them");
            }
        }

        public static void Test_the_worker_lets_nothing_but_reads_through_a_reading_context()
        {
            using (var rig = new SnapshotRig())
            {
                var status = rig.Service.GetStatus();
                var context = SerialOperationContext.ForReading(status.ConnectGeneration, status.LastStop, status.StopLatched);
                var refused = new[]
                {
                    new SerialRequest("w config.brake_resistance 3", ReplyMode.SilenceIsOk, 120, context: context),
                    new SerialRequest("w axis0.requested_state 8", ReplyMode.SilenceIsOk, 120, context: context),
                    new SerialRequest("ss", ReplyMode.SilenceIsOk, 120, context: context),
                    new SerialRequest("sr", ReplyMode.SilenceIsOk, 120, context: context),
                    new SerialRequest("axis.maxtorque=5", ReplyMode.OffbEcho, 300, expectedEcho: "[axis.maxtorque=", context: context),
                    new SerialRequest("sys.save!", ReplyMode.OffbEcho, 300, expectedEcho: "[sys.save!", context: context),
                    new SerialRequest("r config.brake_resistance; w config.brake_resistance 3", ReplyMode.OdriveLine, 300, context: context),
                };
                foreach (var request in refused)
                {
                    var result = rig.Service.SendAsync(request).Result;
                    Check.True(!result.LineWritten && result.Outcome != SerialOutcome.Reply, "refused: " + request.Line);
                }
                Check.Lines(new string[0], rig.Board.Lines, "none of them reached the board");

                // The negative control: the same context lets a read through.
                var read = rig.Service.SendAsync(new SerialRequest("r config.brake_resistance", ReplyMode.OdriveLine, 300, context: context)).Result;
                Check.Equal(SerialOutcome.Reply, read.Outcome, "a read goes out");
            }
        }

        public static void Test_a_finished_stop_with_the_latch_set_still_allows_a_capture_and_stays_set()
        {
            using (var rig = new SnapshotRig())
            {
                rig.Rig.HidState = 1;
                rig.Service.RequestStop();
                Check.True(Wait.Until(() => rig.Service.GetStatus().LastStop.IsFinished), "the stop finishes");
                Check.True(rig.Service.GetStatus().StopLatched, "latch set");
                rig.Board.ClearLines();

                Check.Equal(null, rig.Capture.WhyNotStart(), "a finished stop does not prevent reading");
                var snapshot = rig.Run();
                Check.Equal(SnapshotCompleteness.Complete, snapshot.Completeness, "complete: " + snapshot.AbortReason);
                Check.True(snapshot.StopLatched, "the snapshot says the latch was set");
                Check.Equal(StopState.Confirmed, snapshot.LastStop, "and how the stop ended");
                Check.True(rig.Service.GetStatus().StopLatched, "the latch is still set");
                foreach (var line in rig.Board.WireLines) ParameterReaderTests.CheckIsReadOrSync(line);

                var arm = rig.Rig.Send("w axis0.requested_state 8", ReplyMode.SilenceIsOk, 120).Result;
                Check.Equal(SerialOutcome.BlockedByStop, arm.Outcome, "what the latch blocks stays blocked");
            }
        }

        public static void Test_a_running_stop_prevents_the_start()
        {
            using (var rig = new SnapshotRig())
            {
                rig.Board.StallWrites();
                var ticket = rig.Service.RequestStop();
                Check.True(!ticket.IsFinished, "the stop is still running");
                Check.Equal("a software stop is running", rig.Capture.WhyNotStart(), "says why");

                bool refused = false;
                try { rig.Capture.CaptureAsync(null, CancellationToken.None).GetAwaiter().GetResult(); }
                catch (InvalidOperationException ex) { refused = ex.Message == "a software stop is running"; }
                Check.True(refused, "no capture on a running stop");
                rig.Board.ReleaseWrites();
            }
        }

        public static void Test_without_an_ended_firmware_check_there_is_no_capture()
        {
            using (var rig = new SnapshotRig(b => { SnapshotRig.Current(b); b.MuteOffbChannel = true; }, waitForCheck: false))
            {
                Check.Equal("the firmware check has not ended on this connection yet", rig.Capture.WhyNotStart(), "while it runs");
                rig.WaitForCheck();
                rig.Board.MuteOffbChannel = false;
                Check.Equal(null, rig.Capture.WhyNotStart(), "after a check that found nothing");
            }
        }

        private static void CheckEndedBeforeSending(SnapshotRig rig, CatalogSnapshot snapshot, string reason)
        {
            Check.Equal(SnapshotCompleteness.Partial, snapshot.Completeness, "partial");
            Check.True(snapshot.AbortReason != null && snapshot.AbortReason.Contains(reason), "reason: " + snapshot.AbortReason);
            Check.Equal(0, snapshot.ReadCount, "nothing read");
            Check.Equal(0, rig.Board.Lines.Count(rig.CatalogLines.Contains) - rig.Board.CountLine("r axis0.current_state"), "no line of the capture went out");
            Check.True(snapshot.Entries.All(e => e.Outcome == SnapshotReadOutcome.NotRead), "every path named as not read");
        }

        public static void Test_a_stop_right_before_the_first_line_sends_nothing()
        {
            using (var rig = new SnapshotRig())
            {
                rig.Board.OnNextDiscard(() => rig.Service.RequestStop());
                CheckEndedBeforeSending(rig, rig.Run(), "software stop");
            }
        }

        public static void Test_a_released_port_right_before_the_first_line_sends_nothing()
        {
            using (var rig = new SnapshotRig())
            {
                rig.Board.OnNextDiscard(() => rig.Service.ReleasePort());
                var snapshot = rig.Run();
                CheckEndedBeforeSending(rig, snapshot, "released");
            }
        }

        public static void Test_a_new_firmware_check_right_before_the_first_line_sends_nothing()
        {
            using (var rig = new SnapshotRig(SnapshotRig.Older))
            {
                Check.Equal(FirmwareCapabilityState.NotDeclared, rig.Detector.Status.State, "older firmware");
                rig.Board.OnNextDiscard(() => rig.Detector.CheckAgain());
                CheckEndedBeforeSending(rig, rig.Run(), "the firmware check changed");
            }
        }

        private static void CheckEndedDuring(SnapshotRig rig, CatalogSnapshot snapshot, string reason, int at)
        {
            Check.Equal(SnapshotCompleteness.Partial, snapshot.Completeness, "partial");
            Check.True(snapshot.AbortReason != null && snapshot.AbortReason.Contains(reason), "reason: " + snapshot.AbortReason);
            Check.True(snapshot.ReadCount < at && snapshot.ReadCount >= at - 2, "values before it are kept: " + snapshot.ReadCount);
            var read = snapshot.Entries.Where(e => e.HasValue).ToList();
            Check.True(read.SequenceEqual(snapshot.Entries.Take(read.Count)), "only the paths before the change have values");
            Check.True(snapshot.Entries.Skip(read.Count).All(e => e.Outcome == SnapshotReadOutcome.NotRead), "the rest is named as not read");
            int captureLines = rig.Board.Lines.Count(l => rig.CatalogLines.Contains(l) && l != "r axis0.current_state");
            Check.True(captureLines <= at, "nothing of the capture after the change: " + captureLines);
        }

        public static void Test_a_new_stop_during_the_capture_ends_it()
        {
            using (var rig = new SnapshotRig())
            {
                OnCatalogLine(rig, 40, () => rig.Service.RequestStop());
                var snapshot = rig.Run();
                CheckEndedDuring(rig, snapshot, "software stop", 40);
                var afterStop = rig.Board.Lines.SkipWhile(l => l != StopLine).Skip(1).ToArray();
                Check.True(afterStop.All(l => l == "w axis1.requested_state 1" || l == "r axis0.current_state"), "only the stop's own lines after it: " + string.Join(" | ", afterStop));
            }
        }

        public static void Test_a_new_stop_during_a_capture_on_a_set_latch_ends_it()
        {
            using (var rig = new SnapshotRig())
            {
                rig.Rig.HidState = 1;
                rig.Service.RequestStop();
                Check.True(Wait.Until(() => rig.Service.GetStatus().LastStop.IsFinished), "the first stop finishes");
                rig.Board.ClearLines();

                OnCatalogLine(rig, 40, () => rig.Service.RequestStop());
                CheckEndedDuring(rig, rig.Run(), "software stop", 40);
            }
        }

        public static void Test_a_released_port_during_the_capture_ends_it()
        {
            using (var rig = new SnapshotRig())
            {
                OnCatalogLine(rig, 30, () => rig.Service.ReleasePort());
                CheckEndedDuring(rig, rig.Run(), "released", 30);
            }
        }

        public static void Test_a_reconnect_during_the_capture_ends_it()
        {
            using (var rig = new SnapshotRig())
            {
                int generation = rig.Service.GetStatus().ConnectGeneration;
                OnCatalogLine(rig, 30, () => rig.Board.FailNextRead = true);
                var snapshot = rig.Run();
                CheckEndedDuring(rig, snapshot, "", 30);
                Check.Equal(generation, snapshot.ConnectGeneration, "it belongs to the first connection");

                rig.Rig.WaitConnected();
                Check.True(Wait.Until(() => rig.Service.GetStatus().ConnectGeneration > generation), "a new connection");
                Thread.Sleep(200);
                var afterLoss = rig.Board.Lines.Skip(30).ToArray();
                Check.True(afterLoss.All(l => l == "sys.capabilities?"), "only the new check on the new connection: " + string.Join(" | ", afterLoss));
            }
        }

        public static void Test_a_new_firmware_check_during_the_capture_ends_it()
        {
            using (var rig = new SnapshotRig(SnapshotRig.Older))
            {
                OnCatalogLine(rig, 30, () => rig.Detector.CheckAgain());
                CheckEndedDuring(rig, rig.Run(), "the firmware check changed", 30);
            }
        }

        public static void Test_cancelling_ends_the_capture()
        {
            using (var rig = new SnapshotRig())
            using (var cancel = new CancellationTokenSource())
            {
                OnCatalogLine(rig, 30, () => cancel.Cancel());
                CheckEndedDuring(rig, rig.Run(null, cancel.Token), "cancelled", 30);
            }
        }

        public static void Test_a_reply_that_comes_when_the_state_has_changed_is_not_used()
        {
            using (var rig = new SnapshotRig())
            {
                rig.Rig.HidState = 1;
                rig.Service.RequestStop();
                Check.True(Wait.Until(() => rig.Service.GetStatus().LastStop.IsFinished), "the stop finishes");
                rig.Board.ClearLines();

                // Clearing the latch calls back no line, so this reply does come back. It must not count.
                OnCatalogLine(rig, 30, () => rig.Service.ClearStopLatch());
                var snapshot = rig.Run();
                Check.Equal(SnapshotCompleteness.Partial, snapshot.Completeness, "partial");
                Check.Equal("the stop latch was cleared", snapshot.AbortReason, "reason");
                Check.Equal(29, snapshot.ReadCount, "the 29 before it");
                var thirtieth = snapshot.Entries[29];
                Check.Equal(SnapshotReadOutcome.NotRead, thirtieth.Outcome, "the reply is not used");
                Check.True(thirtieth.Detail.StartsWith("reply not used", StringComparison.Ordinal), thirtieth.Detail);
                Check.Equal(null, thirtieth.Reply, "and not kept as a value");
            }
        }

        private static CatalogSnapshot ChangeAfterTheLastReply(SnapshotRig rig, Action change)
        {
            return rig.Run(new Hook(p =>
            {
                if (p.Done == p.Total) change?.Invoke();
            }));
        }

        public static void Test_a_change_after_the_last_reply_gives_no_complete_snapshot()
        {
            var changes = new (string Name, Action<SnapshotRig> Change, string Reason)[]
            {
                ("stop",    r => r.Service.RequestStop(),   "software stop"),
                ("release", r => { r.Service.ReleasePort(); Wait.Until(() => r.Service.GetStatus().State == SerialConnectionState.Released); }, "released"),
                ("check",   r => r.Detector.CheckAgain(),   "the firmware check changed"),
                ("loss",    r => { r.Board.Present = false; Wait.Until(() => !r.Service.GetStatus().IsConnected); }, ""),
            };
            foreach (var (name, change, reason) in changes)
            {
                using (var rig = new SnapshotRig(name == "check" ? (Action<FakeBoard>)SnapshotRig.Older : null))
                {
                    var snapshot = ChangeAfterTheLastReply(rig, () => change(rig));
                    Check.Equal(snapshot.Expected, snapshot.ReadCount, name + ": every value was read");
                    Check.Equal(SnapshotCompleteness.Partial, snapshot.Completeness, name + ": still no complete snapshot");
                    Check.True(snapshot.AbortReason != null && snapshot.AbortReason.Contains(reason)
                               && snapshot.AbortReason.EndsWith("before the capture was finished", StringComparison.Ordinal), name + ": " + snapshot.AbortReason);
                }
            }

            // The negative control: the same hook without a change.
            using (var rig = new SnapshotRig())
                Check.Equal(SnapshotCompleteness.Complete, ChangeAfterTheLastReply(rig, null).Completeness, "without a change it is complete");
        }

        public static void Test_clearing_the_latch_after_the_last_reply_gives_no_complete_snapshot()
        {
            using (var rig = new SnapshotRig())
            {
                rig.Rig.HidState = 1;
                rig.Service.RequestStop();
                Check.True(Wait.Until(() => rig.Service.GetStatus().LastStop.IsFinished), "the stop finishes");
                var snapshot = ChangeAfterTheLastReply(rig, () => rig.Service.ClearStopLatch());
                Check.Equal(SnapshotCompleteness.Partial, snapshot.Completeness, "partial");
                Check.Equal("the stop latch was cleared, before the capture was finished", snapshot.AbortReason, "reason");
            }
        }

        public static void Test_failed_and_unsupported_reads_are_named_and_the_capture_goes_on()
        {
            using (var rig = new SnapshotRig())
            {
                rig.Board.ReplyOverride = line =>
                {
                    switch (line)
                    {
                        case "r axis0.motor.config.acim_gain_min_flux": return "invalid property";
                        case "r axis0.config.watchdog_timeout":         return "not implemented";
                        case "axis.maxtorque?":                        return "[axis.maxtorque?|NOT_FOUND]";
                        case "fx.master?":                             return "[fx.master?|ERR]";
                        case "r axis0.motor.config.torque_lim":         return "garbage";
                        case "r axis0.encoder.config.bandwidth":        return "[stale|1]";   // discarded, so no reply
                        default: return null;
                    }
                };
                var snapshot = rig.Run();

                Check.Equal(SnapshotCompleteness.WithFailures, snapshot.Completeness, "ran to the end, with failures: " + snapshot.AbortReason);
                Check.Equal(snapshot.Expected - 6, snapshot.ReadCount, "all others read");
                Check.Equal(SnapshotReadOutcome.NotSupported, snapshot.Find("axis0.motor.config.acim_gain_min_flux").Outcome, "invalid property");
                Check.Equal(SnapshotReadOutcome.Refused, snapshot.Find("axis0.config.watchdog_timeout").Outcome, "not implemented");
                Check.Equal("not implemented", snapshot.Find("axis0.config.watchdog_timeout").Detail, "with the firmware's words");
                Check.Equal(SnapshotReadOutcome.NotSupported, snapshot.Find("axis.maxtorque").Outcome, "NOT_FOUND");
                Check.Equal(SnapshotReadOutcome.Refused, snapshot.Find("fx.master").Outcome, "ERR");
                var unexpected = snapshot.Find("axis0.motor.config.torque_lim");
                Check.Equal(SnapshotReadOutcome.Unexpected, unexpected.Outcome, "not a float");
                Check.Equal("garbage", unexpected.Reply, "kept as it came");
                Check.Equal(SnapshotReadOutcome.NoReply, snapshot.Find("axis0.encoder.config.bandwidth").Outcome, "no reply");

                var values = snapshot.ConfigurationValues().Select(p => p.Key).ToList();
                foreach (var path in new[] { "axis0.motor.config.acim_gain_min_flux", "axis0.config.watchdog_timeout", "axis.maxtorque", "fx.master",
                                             "axis0.motor.config.torque_lim", "axis0.encoder.config.bandwidth" })
                {
                    Check.True(!values.Contains(path), "no value in the file for " + path);
                    Check.True(snapshot.Find(path).Detail != null, "and a reason: " + path);
                }
                Check.True(snapshot.Entries.Where(e => !e.HasValue && e.Outcome != SnapshotReadOutcome.Unexpected).All(e => e.Reply == null), "nothing filled in");
            }
        }

        public static void Test_two_timeouts_in_a_row_end_the_capture()
        {
            using (var rig = new SnapshotRig())
            {
                rig.Board.ReplyOverride = line => line == "r axis0.motor.config.pole_pairs" || line == "r axis0.motor.config.torque_constant" ? "[stale|1]" : null;
                var snapshot = rig.Run();
                Check.Equal(SnapshotCompleteness.Partial, snapshot.Completeness, "partial");
                Check.Equal("the board does not answer", snapshot.AbortReason, "reason");
                Check.Equal(SnapshotReadOutcome.NoReply, snapshot.Find("axis0.motor.config.torque_constant").Outcome, "the second timeout");
                Check.Equal(SnapshotReadOutcome.NotRead, snapshot.Find("axis0.motor.config.pre_calibrated").Outcome, "nothing after it");
            }
        }

        public static void Test_older_firmware_gives_a_snapshot_with_uncertain_values()
        {
            using (var rig = new SnapshotRig(SnapshotRig.Older))
            {
                var snapshot = rig.Run();
                Check.Equal(SnapshotCompleteness.Complete, snapshot.Completeness, "every path read: " + snapshot.AbortReason);
                Check.True(!snapshot.Capabilities.FloatsAreExact, "nothing declared");
                Check.Equal(FirmwareCapabilityState.NotDeclared, snapshot.Capabilities.State, "state");
                Check.Equal("345678901", snapshot.Find("serial_number").Reply, "cut like older firmware, kept as it came");
                Check.Equal(ReplyCertainty.MayBeCutOff, snapshot.Find("serial_number").Certainty, "and marked");
                Check.Equal(ReplyCertainty.MayBeCutOff, snapshot.Find("axis0.motor.error").Certainty, "error bits too");
                Check.Equal(ReplyCertainty.MayBeRounded, snapshot.Find("config.brake_resistance").Certainty, "a float may be rounded");
                Check.Equal(ReplyCertainty.Exact, snapshot.Find("config.enable_brake_resistor").Certainty, "a boolean cannot be");
                Check.Equal(ReplyCertainty.Exact, snapshot.Find("config.dc_max_positive_current").Certainty, "nor inf");
                Check.Equal(ReplyCertainty.Exact, snapshot.Find("sys.vbusdiv").Certainty, "nor a short integer");
                Check.True(OdriveSnapshotText.Summary(snapshot).Contains("not proven"), "the summary says so");
            }
        }

        public static void Test_a_finished_snapshot_does_not_change()
        {
            using (var rig = new SnapshotRig())
            {
                var snapshot = rig.Run();
                var before = snapshot.Entries.Select(e => e.Path + "=" + e.Reply).ToArray();
                rig.Board.SetValue("config.brake_resistance", "9");
                rig.Board.Present = false;
                Check.True(Wait.Until(() => !rig.Service.GetStatus().IsConnected), "disconnected");

                Check.Lines(before, snapshot.Entries.Select(e => e.Path + "=" + e.Reply).ToArray(), "the same afterwards");
                Check.True(((ICollection<SnapshotEntry>)snapshot.Entries).IsReadOnly, "and it cannot be changed");
                Check.Equal(SnapshotCompleteness.Complete, snapshot.Completeness, "still complete as a record of that time");
            }
        }
    }

    public static class SnapshotFileTests
    {
        private static readonly string Root = Path.Combine(Path.GetTempPath(), "odw-snapshot-tests");

        // What an earlier run left goes once per run, what this run writes stays for a look afterwards.
        private static readonly Lazy<bool> Cleared = new Lazy<bool>(() =>
        {
            try { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return true;
        });

        internal static string NewFolder()
        {
            _ = Cleared.Value;
            var folder = Path.Combine(Root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            return folder;
        }

        internal static CatalogSnapshot Captured(Action<FakeBoard> prepare = null)
        {
            using (var rig = new SnapshotRig(prepare))
                return rig.Run();
        }

        private static readonly Lazy<CatalogSnapshot> Exact = new Lazy<CatalogSnapshot>(() => Captured());

        public static void Test_export_writes_the_flat_values_and_matching_metadata()
        {
            var snapshot = Exact.Value;
            var path = Path.Combine(NewFolder(), OdriveSnapshotFile.FileName(snapshot.StartedUtc, snapshot.Completeness));
            var result = OdriveSnapshotFile.Export(snapshot, path, "1.2.3");
            Check.True(result.Succeeded, "exported: " + result.Error);

            var text = File.ReadAllText(path, Encoding.UTF8);
            Check.True(text.StartsWith("{\n  \"", StringComparison.Ordinal) && text.EndsWith("\"\n}", StringComparison.Ordinal) && !text.Contains("\r"),
                       "the layout of JSON.stringify(obj, null, 2)");
            Check.True(File.ReadAllBytes(path)[0] == (byte)'{', "no byte order mark");
            var values = JObject.Parse(text);
            Check.True(values.Properties().All(p => p.Value.Type == JTokenType.String), "path: string only");
            Check.Lines(snapshot.ConfigurationValues().Select(p => p.Key + "=" + p.Value).ToArray(),
                        values.Properties().Select(p => p.Name + "=" + (string)p.Value).ToArray(), "the settings, as the board sent them, in catalog order");

            var meta = JObject.Parse(File.ReadAllText(result.MetadataPath));
            Check.Equal(OdriveSnapshotFile.MetadataPathFor(path), result.MetadataPath, "next to it");
            Check.Equal("odrive-wheel-catalog-snapshot", (string)meta["_type"], "type");
            Check.Equal(1, (int)meta["_version"], "version");
            Check.Equal(OdriveParameterCatalog.Sha256Hex(File.ReadAllBytes(path)), (string)meta["values_file"]["sha256"], "bound to the values file");
            Check.Equal("complete", (string)meta["completeness"], "completeness");
            Check.True((bool)meta["values_exact"], "exact");
            Check.Equal(FakeBoard.Declaration, (string)meta["capabilities"]["declaration"], "the evidence");
            Check.Equal(false, (bool)meta["capture"]["simultaneous"], "not simultaneous");
            Check.Equal("RAM of the board, read over the serial connection", (string)meta["capture"]["source"], "the origin");
            Check.Equal("abc branch=test", (string)meta["firmware"]["build"], "the build");
            Check.Equal(snapshot.Expected, (int)meta["catalog"]["expected_paths"], "expected paths");
            Check.Equal(snapshot.ReadCount, (int)meta["catalog"]["read"], "read paths");
            Check.True(((string)meta["what"]).Contains("not a complete NVM image"), "what it is not");
            Check.True(((JArray)meta["not_included"]).Count >= 5, "what is not included");
            Check.Equal("48", (string)meta["status_values"]["vbus_voltage"], "status values apart");
            Check.True(meta["status_values"]["config.brake_resistance"] == null, "no setting among them");

            var loaded = OdriveSnapshotFile.Load(path);
            Check.Equal(SnapshotMetadataState.Matches, loaded.Metadata, "matches when loaded: " + loaded.MetadataDetail);
            Check.True(loaded.ToSide("file").AsciiFull && loaded.ToSide("file").Float32RoundTrip, "and proves the precision");
            Check.Equal(SnapshotMetadataState.Matches, OdriveSnapshotFile.Load(result.MetadataPath).Metadata, "also when the metadata file is chosen");
        }

        public static void Test_a_partial_snapshot_is_marked_in_its_names_and_metadata()
        {
            CatalogSnapshot snapshot;
            using (var rig = new SnapshotRig())
            {
                int seen = 0;
                rig.Board.ReplyOverride = line => { if (Interlocked.Increment(ref seen) == 20) rig.Service.RequestStop(); return null; };
                snapshot = rig.Run();
            }
            Check.Equal(SnapshotCompleteness.Partial, snapshot.Completeness, "partial");
            var name = OdriveSnapshotFile.FileName(snapshot.StartedUtc, snapshot.Completeness);
            Check.True(name.EndsWith("_partial.json", StringComparison.Ordinal), name);

            var result = OdriveSnapshotFile.Export(snapshot, Path.Combine(NewFolder(), name), "1");
            Check.True(result.Succeeded, result.Error);
            var meta = JObject.Parse(File.ReadAllText(result.MetadataPath));
            Check.Equal("partial", (string)meta["completeness"], "completeness");
            Check.Equal("software stop", (string)meta["abort_reason"], "and why");
            Check.True(((JArray)meta["failed"]).Count > 100, "every path that was not read is listed");
            Check.True(OdriveSnapshotFile.Load(result.ValuesPath).ToSide("x").Notes.Any(n => n.Contains("partial")), "a comparison says so");
        }

        public static void Test_an_export_never_replaces_a_file_or_pretends_to_succeed()
        {
            var snapshot = Exact.Value;
            var folder = NewFolder();

            var existing = Path.Combine(folder, "taken.json");
            File.WriteAllText(existing, "{}");
            var taken = OdriveSnapshotFile.Export(snapshot, existing, "1");
            Check.True(!taken.Succeeded && taken.Error.Contains("already exists"), "refused: " + taken.Error);
            Check.Equal("{}", File.ReadAllText(existing), "the file is untouched");
            Check.True(!File.Exists(OdriveSnapshotFile.MetadataPathFor(existing)), "no metadata either");

            var metaTaken = Path.Combine(folder, "second.json");
            File.WriteAllText(OdriveSnapshotFile.MetadataPathFor(metaTaken), "{}");
            Check.True(!OdriveSnapshotFile.Export(snapshot, metaTaken, "1").Succeeded, "refused when the metadata name is taken");
            Check.True(!File.Exists(metaTaken), "and no values file without it");

            var noFolder = Path.Combine(folder, "missing", "x.json");
            var missing = OdriveSnapshotFile.Export(snapshot, noFolder, "1");
            Check.True(!missing.Succeeded, "no folder: " + missing.Error);
            Check.True(!Directory.Exists(Path.Combine(folder, "missing")), "nothing created");

            // The temporary metadata file cannot be written: a folder is in its way.
            var blocked = Path.Combine(folder, "blocked.json");
            Directory.CreateDirectory(OdriveSnapshotFile.MetadataPathFor(blocked) + ".tmp");
            var result = OdriveSnapshotFile.Export(snapshot, blocked, "1");
            Check.True(!result.Succeeded, "failed: " + result.Error);
            Check.True(!File.Exists(blocked) && !File.Exists(blocked + ".tmp"), "the values it wrote are gone again");

            // The values file is already in place when the metadata cannot follow it.
            var late = Path.Combine(folder, "late.json");
            Directory.CreateDirectory(OdriveSnapshotFile.MetadataPathFor(late));
            var lateResult = OdriveSnapshotFile.Export(snapshot, late, "1");
            Check.True(!lateResult.Succeeded, "failed: " + lateResult.Error);
            Check.True(!File.Exists(late) && !File.Exists(late + ".tmp"), "the values file it moved into place is removed again");

            var wrongKind = OdriveSnapshotFile.Export(snapshot, Path.Combine(folder, "x.meta.json"), "1");
            Check.True(!wrongKind.Succeeded, "a values file is no metadata file");
            Check.Equal(4, Directory.GetFileSystemEntries(folder).Length, "only what the test put there: " + string.Join(", ", Directory.GetFileSystemEntries(folder)));
        }

        private static string Write(string folder, string name, string text)
        {
            var path = Path.Combine(folder, name);
            File.WriteAllText(path, text, new UTF8Encoding(false));
            return path;
        }

        public static void Test_damaged_files_are_refused_with_a_reason()
        {
            var folder = NewFolder();
            var cases = new (string Name, string Text, string Says)[]
            {
                ("empty.json",     "",                                        "not a valid values file"),
                ("broken.json",    "{ \"a.b\": \"1\" ",                       "not a valid values file"),
                ("array.json",     "[ \"a\" ]",                               "no JSON object"),
                ("twice.json",     "{ \"a.b\": \"1\", \"a.b\": \"2\" }",      "not a valid values file"),
                ("trailing.json",  "{ \"a.b\": \"1\" } { }",                  "not a valid values file"),
                ("profile.json",   "{ \"_type\": \"odrive-wheel-profile\", \"values\": {} }", "no flat values file"),
            };
            foreach (var (name, text, says) in cases)
            {
                var loaded = OdriveSnapshotFile.Load(Write(folder, name, text));
                Check.True(!loaded.Loaded && loaded.Error.Contains(says), name + ": " + loaded.Error);
                Check.Equal(0, loaded.Values.Count, name + ": no values");
            }

            var invalidUtf8 = Path.Combine(folder, "bytes.json");
            File.WriteAllBytes(invalidUtf8, new byte[] { (byte)'{', (byte)'"', 0xC3, 0x28, (byte)'"', (byte)':', (byte)'"', (byte)'1', (byte)'"', (byte)'}' });
            Check.True(OdriveSnapshotFile.Load(invalidUtf8).Error.Contains("UTF-8"), "not UTF-8");

            var large = Path.Combine(folder, "large.json");
            File.WriteAllText(large, "{\"a.b\": \"" + new string('x', (int)OdriveSnapshotFile.MaxBytes) + "\"}");
            Check.True(OdriveSnapshotFile.Load(large).Error.Contains("too large"), "too large");

            Check.True(OdriveSnapshotFile.Load(Path.Combine(folder, "absent.json")).Error.Contains("does not exist"), "absent");

            var mixed = OdriveSnapshotFile.Load(Write(folder, "mixed.json", "{ \"a.b\": \"1\", \"c.d\": 2, \"e.f\": true, \"g.h\": null, \"i.j\": { } }"));
            Check.True(mixed.Loaded, "a file with some values that are no string still loads");
            Check.Equal(1, mixed.Values.Count, "only the string");
            Check.Equal(4, mixed.Problems.Count, "the others are named: " + string.Join("; ", mixed.Problems));

            var date = OdriveSnapshotFile.Load(Write(folder, "date.json", "{ \"a.b\": \"2026-11-03T10:00:00Z\", \"c.d\": \"1.50\" }"));
            Check.Equal("2026-11-03T10:00:00Z", date.Values[0].Value, "no date conversion");
            Check.Equal("1.50", date.Values[1].Value, "no number conversion");
        }

        public static void Test_missing_or_unfitting_metadata_proves_nothing()
        {
            var snapshot = Exact.Value;
            var folder = NewFolder();
            var path = Path.Combine(folder, "a.json");
            Check.True(OdriveSnapshotFile.Export(snapshot, path, "1").Succeeded, "exported");
            var metaPath = OdriveSnapshotFile.MetadataPathFor(path);
            var meta = File.ReadAllText(metaPath);

            // A changed values file: its metadata now belongs to other content.
            var edited = Path.Combine(folder, "edited.json");
            File.WriteAllText(edited, File.ReadAllText(path).Replace("\"config.brake_resistance\": \"2\"", "\"config.brake_resistance\": \"3\""), new UTF8Encoding(false));
            File.WriteAllText(OdriveSnapshotFile.MetadataPathFor(edited), meta);
            var loaded = OdriveSnapshotFile.Load(edited);
            Check.Equal(SnapshotMetadataState.NotForThisFile, loaded.Metadata, "not for this file: " + loaded.MetadataDetail);
            var side = loaded.ToSide("edited");
            Check.True(!side.AsciiFull && !side.Float32RoundTrip, "so no precision is taken from it");
            Check.True(side.Precision.StartsWith("not proven", StringComparison.Ordinal), side.Precision);
            Check.True(!side.Values.ContainsKey("vbus_voltage"), "and none of its status values");

            var alone = Path.Combine(folder, "alone.json");
            File.Copy(path, alone);
            Check.Equal(SnapshotMetadataState.Missing, OdriveSnapshotFile.Load(alone).Metadata, "missing");
            Check.True(!OdriveSnapshotFile.Load(alone).ToSide("x").Float32RoundTrip, "no proof without it");

            var variants = new (string Name, Func<JObject, JObject> Change, SnapshotMetadataState State)[]
            {
                ("version", m => { m["_version"] = 2; return m; }, SnapshotMetadataState.UnknownVersion),
                ("kind",    m => { m["_type"] = "something-else"; return m; }, SnapshotMetadataState.Unreadable),
                ("flags",   m => { m["capabilities"]["declaration"] = "v=1 ascii_full=0 float32_rt=1"; return m; }, SnapshotMetadataState.Unreadable),
                ("bool",    m => { m["capabilities"]["ascii_full"] = "yes"; return m; }, SnapshotMetadataState.Unreadable),
                ("hash",    m => { m["values_file"]["sha256"] = new string('0', 64); return m; }, SnapshotMetadataState.NotForThisFile),
                ("nohash",  m => { ((JObject)m["values_file"]).Remove("sha256"); return m; }, SnapshotMetadataState.Unreadable),
            };
            foreach (var (name, change, state) in variants)
            {
                var copy = Path.Combine(folder, name + ".json");
                File.Copy(path, copy);
                File.WriteAllText(OdriveSnapshotFile.MetadataPathFor(copy), change(JObject.Parse(meta)).ToString(), new UTF8Encoding(false));
                var result = OdriveSnapshotFile.Load(copy);
                Check.Equal(state, result.Metadata, name + ": " + result.MetadataDetail);
                Check.True(result.Loaded && result.Info == null && !result.ToSide("x").Float32RoundTrip, name + ": values usable, precision not proven");
            }

            File.WriteAllText(Path.Combine(folder, "garbage.meta.json"), "{ nope", new UTF8Encoding(false));
            File.Copy(path, Path.Combine(folder, "garbage.json"));
            Check.Equal(SnapshotMetadataState.Unreadable, OdriveSnapshotFile.Load(Path.Combine(folder, "garbage.json")).Metadata, "unreadable metadata");
        }

        public static void Test_a_metadata_file_leads_only_to_a_file_next_to_it()
        {
            var folder = NewFolder();
            var outside = Path.Combine(Path.GetDirectoryName(folder), "outside.json");
            File.WriteAllText(outside, "{ \"a.b\": \"1\" }");
            foreach (var name in new[] { "..\\outside.json", "../outside.json", outside, "x.meta.json", "", "x.txt" })
            {
                var meta = Write(folder, "pointer.meta.json", new JObject { ["_type"] = OdriveSnapshotFile.MetadataType, ["values_file"] = new JObject { ["name"] = name } }.ToString());
                var loaded = OdriveSnapshotFile.Load(meta);
                Check.True(!loaded.Loaded && loaded.Error.Contains("names no values file"), "refused: '" + name + "': " + loaded.Error);
            }
        }
    }

    public static class SnapshotComparisonTests
    {
        private static readonly OdriveParameterCatalog Catalog = OdriveParameterCatalog.LoadEmbedded();
        private static readonly ParameterDefinition OdriveFloat = Catalog.Find("config.brake_resistance");
        private static readonly ParameterDefinition OffbFloat = Catalog.Find("axis.maxtorque");
        private static readonly ParameterDefinition Integer = Catalog.Find("serial_number");
        private static readonly ParameterDefinition Bitmask = Catalog.Find("axis0.motor.error");
        private static readonly ParameterDefinition Boolean = Catalog.Find("config.enable_brake_resistor");
        private static readonly ParameterDefinition Enum = Catalog.Find("axis0.motor.config.motor_type");

        private const ReplyCertainty Exact = ReplyCertainty.Exact, Rounded = ReplyCertainty.MayBeRounded, Cut = ReplyCertainty.MayBeCutOff;

        private static ComparisonVerdict Verdict(ParameterDefinition definition, string a, ReplyCertainty ca, string b, ReplyCertainty cb)
            => OdriveValueComparer.Compare(definition, a, ca, b, cb, out _);

        private static void Expect(ComparisonVerdict expected, ParameterDefinition definition, string a, ReplyCertainty ca, string b, ReplyCertainty cb)
        {
            Check.Equal(expected, Verdict(definition, a, ca, b, cb), (definition?.Path ?? "unknown") + ": " + a + " (" + ca + ") vs " + b + " (" + cb + ")");
            Check.Equal(expected, Verdict(definition, b, cb, a, ca), "and the other way round: " + b + " vs " + a);
        }

        public static void Test_float32_equality_needs_proven_precision()
        {
            Expect(ComparisonVerdict.Same, OdriveFloat, "32.012", Exact, "32.012001", Exact);
            Check.Equal((float)32.012, (float)32.012001, "the premise: one float32");
            Expect(ComparisonVerdict.Same, OdriveFloat, "0.1", Exact, "0.100000001", Exact);
            Expect(ComparisonVerdict.Differs, OdriveFloat, "32.012", Exact, "32.0121", Exact);
            Expect(ComparisonVerdict.Same, OdriveFloat, "1.5e-05", Exact, "0.000015", Exact);

            Expect(ComparisonVerdict.Unclear, OdriveFloat, "32.012", Rounded, "32.012001", Exact);
            Expect(ComparisonVerdict.Unclear, OdriveFloat, "32.012000", Rounded, "32.012001", Rounded);
            Expect(ComparisonVerdict.SameText, OdriveFloat, "32.012", Rounded, "32.012", Exact);
            Expect(ComparisonVerdict.SameText, OdriveFloat, "32.012", Rounded, "32.012", Rounded);
            Expect(ComparisonVerdict.Differs, OdriveFloat, "25.000000", Rounded, "10.000000", Rounded);
            Expect(ComparisonVerdict.Differs, OffbFloat, "5.00", Rounded, "5.02", Exact);
            Expect(ComparisonVerdict.Unclear, OffbFloat, "5.00", Rounded, "5.004", Exact);
            Expect(ComparisonVerdict.Unclear, OdriveFloat, "123456.78", Cut, "123456.789", Exact);
            Expect(ComparisonVerdict.SameText, OdriveFloat, "123456.78", Cut, "123456.78", Cut);

            Expect(ComparisonVerdict.Unclear, OdriveFloat, "abc", Exact, "1", Exact);
            Expect(ComparisonVerdict.SameText, OdriveFloat, "abc", Exact, "abc", Exact);
            Expect(ComparisonVerdict.SameText, OdriveFloat, "1e39", Exact, "1e39", Exact);
            Expect(ComparisonVerdict.Unclear, OdriveFloat, "1e39", Exact, "1", Exact);
            Expect(ComparisonVerdict.Unclear, OdriveFloat, " 1.5", Exact, "1.5", Exact);
        }

        public static void Test_signed_zero_nan_and_infinity_have_fixed_rules()
        {
            Expect(ComparisonVerdict.Same, OdriveFloat, "0", Exact, "-0", Exact);
            OdriveValueComparer.Compare(OdriveFloat, "0", Exact, "-0", Exact, out var note);
            Check.True(note != null && note.Contains("+0 and -0"), "said: " + note);
            Expect(ComparisonVerdict.Same, OdriveFloat, "nan", Exact, "nan", Exact);
            Expect(ComparisonVerdict.Same, OdriveFloat, "nan", Exact, "-nan", Exact);
            Expect(ComparisonVerdict.Differs, OdriveFloat, "nan", Exact, "0", Exact);
            Expect(ComparisonVerdict.Differs, OdriveFloat, "nan", Exact, "0.000000", Rounded);
            Expect(ComparisonVerdict.Same, OdriveFloat, "inf", Exact, "inf", Exact);
            Expect(ComparisonVerdict.Differs, OdriveFloat, "inf", Exact, "-inf", Exact);
            Expect(ComparisonVerdict.Differs, OdriveFloat, "inf", Exact, "340282347000000000000000000000000000000", Exact);
            Expect(ComparisonVerdict.Differs, OdriveFloat, "-inf", Exact, "-3.40282347e38", Rounded);
            Expect(ComparisonVerdict.Unclear, OdriveFloat, "0.000000", Rounded, "-0", Exact);
            Expect(ComparisonVerdict.Unclear, OdriveFloat, "0.000000", Rounded, "1e-07", Exact);

            // Non-finite replies cannot be rounded, so they are exact even without a declaration.
            Check.Equal(ReplyCertainty.Exact, OdriveReplyCertainty.Of(OdriveFloat, "inf", false, false), "inf");
            Check.Equal(ReplyCertainty.Exact, OdriveReplyCertainty.Of(OdriveFloat, "nan", false, false), "nan");
            Check.Equal(ReplyCertainty.MayBeRounded, OdriveReplyCertainty.Of(OdriveFloat, "0", false, false), "0 may be a rounded small number");
        }

        public static void Test_large_integers_compare_without_loss()
        {
            Expect(ComparisonVerdict.Differs, Integer, "18446744073709551615", Exact, "18446744073709551614", Exact);
            Check.Equal((double)18446744073709551615UL, (double)18446744073709551614UL, "the premise: a double cannot tell them apart");
            Expect(ComparisonVerdict.Differs, Integer, "9007199254740993", Exact, "9007199254740992", Exact);
            Expect(ComparisonVerdict.Same, Integer, "123456789012345678901234567890", Exact, "123456789012345678901234567890", Exact);
            Expect(ComparisonVerdict.Same, Integer, "+5", Exact, "5", Exact);

            // Older firmware kept the first 9 characters.
            Expect(ComparisonVerdict.Unclear, Bitmask, "214748364", Cut, "2147483648", Exact);
            Expect(ComparisonVerdict.Differs, Bitmask, "214748364", Cut, "3", Exact);
            Expect(ComparisonVerdict.Differs, Bitmask, "214748364", Cut, "2147483", Exact);
            Expect(ComparisonVerdict.Differs, Bitmask, "214748364", Cut, "314748364", Cut);
            Expect(ComparisonVerdict.SameText, Bitmask, "214748364", Cut, "214748364", Cut);
            Check.Equal(ReplyCertainty.MayBeCutOff, OdriveReplyCertainty.Of(Integer, "345678901", false, false), "9 characters without a declaration");
            Check.Equal(ReplyCertainty.Exact, OdriveReplyCertainty.Of(Integer, "34567890", false, false), "8 cannot be cut");
            Check.Equal(ReplyCertainty.Exact, OdriveReplyCertainty.Of(Integer, "345678901", true, false), "ascii_full is enough for integers");
            Expect(ComparisonVerdict.Unclear, Integer, "1.5", Exact, "1", Exact);
        }

        public static void Test_booleans_and_enums_follow_the_catalog()
        {
            Expect(ComparisonVerdict.Same, Boolean, "1", Exact, "True", Exact);
            Expect(ComparisonVerdict.Same, Boolean, "0", Exact, "false", Exact);
            Expect(ComparisonVerdict.Differs, Boolean, "0", Exact, "1", Exact);
            Expect(ComparisonVerdict.Unclear, Boolean, "2", Exact, "1", Exact);
            Expect(ComparisonVerdict.Same, Enum, "2", Exact, "2", Exact);
            Expect(ComparisonVerdict.Differs, Enum, "2", Exact, "3", Exact);
            Expect(ComparisonVerdict.Unclear, Enum, "GIMBAL", Exact, "2", Exact);
            Expect(ComparisonVerdict.Same, Catalog.Find("sys.build"), "abc", Exact, "abc", Exact);
            Expect(ComparisonVerdict.Differs, Catalog.Find("sys.build"), "abc", Exact, "abd", Exact);
            Expect(ComparisonVerdict.SameText, null, "1", Exact, "1", Exact);
            Expect(ComparisonVerdict.Unclear, null, "1", Exact, "1.0", Exact);
        }

        private static SnapshotSide FileSide(string name, string text, string folder = null)
        {
            folder = folder ?? SnapshotFileTests.NewFolder();
            var path = Path.Combine(folder, name);
            File.WriteAllText(path, text, new UTF8Encoding(false));
            return OdriveSnapshotFile.Load(path).ToSide(name);
        }

        public static void Test_a_file_without_metadata_gives_same_text_only_for_floats()
        {
            var left = FileSide("a.json", "{ \"config.brake_resistance\": \"32.012\", \"config.enable_brake_resistor\": \"1\", \"sys.vbusdiv\": \"19\" }");
            var right = FileSide("b.json", "{ \"config.brake_resistance\": \"32.012\", \"config.enable_brake_resistor\": \"True\", \"sys.vbusdiv\": \"19\" }");
            var comparison = SnapshotComparison.Compare(left, right, Catalog);
            Check.Equal(ComparisonVerdict.SameText, comparison.Values.First(v => v.Path == "config.brake_resistance").Verdict, "a float: same text only");
            Check.Equal(ComparisonVerdict.Same, comparison.Values.First(v => v.Path == "config.enable_brake_resistor").Verdict, "a boolean cannot be rounded");
            Check.Equal(ComparisonVerdict.Same, comparison.Values.First(v => v.Path == "sys.vbusdiv").Verdict, "nor a short integer");

            var board = SnapshotFileTests.Captured(b => { SnapshotRig.Current(b); b.SetValue("config.brake_resistance", "32.012001"); });
            Check.Equal("32.012001", board.Find("config.brake_resistance").Reply, "the board's exact reply");
            var proven = SnapshotComparison.Compare(SnapshotSide.FromSnapshot(board, "board"), left, Catalog);
            Check.Equal(ComparisonVerdict.Unclear, proven.Values.First(v => v.Path == "config.brake_resistance").Verdict, "one side without proof is enough");
        }

        public static void Test_32_012_and_32_012001_are_one_float32_with_proof_on_both_sides()
        {
            var folder = SnapshotFileTests.NewFolder();
            var paths = new List<string>();
            foreach (var text in new[] { "32.012", "32.012001" })
            {
                var snapshot = SnapshotFileTests.Captured(b => { SnapshotRig.Current(b); b.ReplyOverride = l => l == "r config.brake_resistance" ? text : null; });
                var path = Path.Combine(folder, "v" + paths.Count + ".json");
                Check.True(OdriveSnapshotFile.Export(snapshot, path, "1").Succeeded, "exported");
                paths.Add(path);
            }
            var a = OdriveSnapshotFile.Load(paths[0]);
            var b = OdriveSnapshotFile.Load(paths[1]);
            var comparison = SnapshotComparison.Compare(a.ToSide("a"), b.ToSide("b"), Catalog);
            var value = comparison.Values.First(v => v.Path == "config.brake_resistance");
            Check.Equal(ComparisonVerdict.Same, value.Verdict, "the same float32: " + value.Note);
            Check.Equal("32.012", value.Left, "the texts stay as they were");
            Check.Equal("32.012001", value.Right, "on both sides");

            // The same files once their metadata is gone: equal as text or unclear, nothing more.
            File.Delete(OdriveSnapshotFile.MetadataPathFor(paths[1]));
            var unproven = SnapshotComparison.Compare(a.ToSide("a"), OdriveSnapshotFile.Load(paths[1]).ToSide("b"), Catalog);
            Check.Equal(ComparisonVerdict.Unclear, unproven.Values.First(v => v.Path == "config.brake_resistance").Verdict, "without proof only unclear");
        }

        public static void Test_unknown_file_keys_are_data_and_compared_as_text()
        {
            var left = FileSide("evil.json", "{ \"w axis0.requested_state 8\": \"1\", \"ss\": \"\", \"config.brake_resistance\": \"2\", \"made.up\": \"x\" }");
            var right = FileSide("other.json", "{ \"made.up\": \"y\", \"config.brake_resistance\": \"2\" }");
            var comparison = SnapshotComparison.Compare(left, right, Catalog);
            var unknown = comparison.Values.Where(v => v.Definition == null).ToList();
            Check.Lines(new[] { "w axis0.requested_state 8", "ss", "made.up" }, unknown.Select(v => v.Path).ToArray(), "every unknown key is listed");
            Check.True(unknown.All(v => v.Section == SnapshotSection.Configuration && v.Note != null && v.Note.Contains("not in the plugin's catalog")), "as unknown");
            Check.Equal(ComparisonVerdict.OnlyLeft, unknown[0].Verdict, "only in one file");
            Check.Equal(ComparisonVerdict.Unclear, unknown[2].Verdict, "different text of an unknown type");
        }

        public static void Test_settings_and_status_values_are_shown_apart()
        {
            var snapshot = SnapshotFileTests.Captured();
            var folder = SnapshotFileTests.NewFolder();
            var path = Path.Combine(folder, "a.json");
            Check.True(OdriveSnapshotFile.Export(snapshot, path, "1").Succeeded, "exported");

            var later = SnapshotFileTests.Captured(b => { SnapshotRig.Current(b); b.SetValue("vbus_voltage", "47.5"); b.SetValue("config.brake_resistance", "2.5"); });
            var comparison = SnapshotComparison.Compare(OdriveSnapshotFile.Load(path).ToSide("file"), SnapshotSide.FromSnapshot(later, "board"), Catalog);

            var setting = comparison.Configuration.First(v => v.Path == "config.brake_resistance");
            Check.Equal(ComparisonVerdict.Differs, setting.Verdict, "a changed setting");
            var status = comparison.Status.First(v => v.Path == "vbus_voltage");
            Check.Equal(ComparisonVerdict.Differs, status.Verdict, "a changed status value, from the metadata");
            Check.True(comparison.Configuration.All(v => v.Section == SnapshotSection.Configuration) && comparison.Status.All(v => v.Section == SnapshotSection.Status), "apart");
            var text = OdriveSnapshotText.Comparison(comparison);
            Check.True(text.IndexOf("Settings:", StringComparison.Ordinal) < text.IndexOf("Status values", StringComparison.Ordinal), "settings first");
            Check.Equal(1, comparison.Configuration.Count(v => v.IsDifference), "one setting differs: " + string.Join("; ", comparison.Configuration.Where(v => v.IsDifference).Select(OdriveSnapshotText.Line)));
        }

        public static void Test_a_value_a_side_could_not_read_is_not_compared()
        {
            var board = SnapshotFileTests.Captured(b => { SnapshotRig.Current(b); b.ReplyOverride = l => l == "r axis0.motor.config.torque_lim" ? "not implemented" : null; });
            var file = FileSide("f.json", "{ \"axis0.motor.config.torque_lim\": \"10\" }");
            var value = SnapshotComparison.Compare(file, SnapshotSide.FromSnapshot(board, "board"), Catalog).Values.First(v => v.Path == "axis0.motor.config.torque_lim");
            Check.Equal(ComparisonVerdict.NotCompared, value.Verdict, "not compared");
            Check.True(value.Note.Contains("not implemented"), value.Note);
        }
    }

    public static class SnapshotReportTests
    {
        public static void Test_the_report_names_what_it_has_and_its_limits()
        {
            var snapshot = SnapshotFileTests.Captured(b =>
            {
                SnapshotRig.Current(b);
                b.SetValue("axis0.error", "320");
                b.SetValue("axis0.motor.motor_thermistor.temperature", "0");
                b.SetValue("axis0.motor.motor_thermistor.config.enabled", "0");
                b.ReplyOverride = l => l == "r ibus" ? "not implemented" : null;
            });
            var report = OdriveDiagnosticReport.Build(snapshot, null, "1.2.3", new DateTime(2026, 11, 3, 10, 0, 0, DateTimeKind.Utc));

            foreach (var part in new[] { "## Capture", "## Firmware", "## Errors", "## Bus voltage", "## Temperatures", "## Values without a usable reply", "## Differences" })
                Check.True(report.Contains(part), "has " + part);
            Check.True(report.Contains("`abc branch=test`"), "the build");
            Check.True(report.Contains(FakeBoard.Declaration), "the capabilities");
            Check.True(report.Contains("MOTOR_FAILED | ENCODER_FAILED"), "errors in clear text");
            Check.True(report.Contains("axis0.motor.error: 0x80000000 — "), "bit 31 decoded, the whole number came");
            Check.True(report.Contains("48 V"), "the bus voltage");
            Check.True(report.Contains("41.25"), "the FET temperature");
            Check.True(report.Contains("the firmware stops at 125 °C"), "the error at upper + 5");
            Check.True(report.Contains("0 is ambiguous"), "0 is not taken as a reading");
            Check.True(report.Contains("Disabled: not used for limiting"), "a disabled sensor says nothing");
            Check.True(report.Contains("cannot tell a missing sensor"), "no sensor detection");
            Check.True(report.Contains("does not identify the board"), "no board identity");
            Check.True(report.Contains("refused (not implemented): ibus"), "the failed read");
            Check.True(report.Contains("No comparison with a file"), "without a comparison");
            Check.True(report.Contains("not a complete NVM image"), "what a snapshot is not");
        }

        public static void Test_the_report_lists_the_differences_from_a_file()
        {
            var folder = SnapshotFileTests.NewFolder();
            var path = Path.Combine(folder, "old.json");
            Check.True(OdriveSnapshotFile.Export(SnapshotFileTests.Captured(), path, "1").Succeeded, "exported");
            var now = SnapshotFileTests.Captured(b => { SnapshotRig.Current(b); b.SetValue("axis0.motor.config.current_lim", "20"); });
            var comparison = SnapshotComparison.Compare(OdriveSnapshotFile.Load(path).ToSide("old.json"), SnapshotSide.FromSnapshot(now, "board"), OdriveParameterCatalog.LoadEmbedded());

            var report = OdriveDiagnosticReport.Build(now, comparison, "1", DateTime.UtcNow);
            Check.True(report.Contains("axis0.motor.config.current_lim: 25 | 20  DIFFERS"), "the changed setting");
            Check.True(report.Contains("Left: old.json"), "against which file");
            Check.True(report.Contains("Settings: "), "settings");
            Check.True(report.Contains("Status values, which change on their own"), "status apart");
        }
    }

    public static class SnapshotWindowTests
    {
        private static WindowTests.WindowRig NewRig(Action<FakeBoard> prepare = null)
        {
            var rig = new WindowTests.WindowRig(prepare ?? SnapshotRig.Current, detectCapabilities: true);
            Check.True(Wait.Until(() => rig.Capabilities.Status.State != FirmwareCapabilityState.Unknown), "the firmware check ends");
            return rig;
        }

        private static void StartCapture(WindowTests.WindowRig rig)
        {
            Check.True(Wait.Until(() => rig.On(w => w.CaptureButton.IsEnabled)), "can capture");
            rig.Host.Invoke(w => WindowTests.Click(w.CaptureButton));
        }

        private static CatalogSnapshot WaitForSnapshot(WindowTests.WindowRig rig, CatalogSnapshot previous = null)
        {
            Check.True(Wait.Until(() => rig.On(w => !w.IsCapturing && w.CurrentSnapshot != null && w.CurrentSnapshot != previous), 15000), "the capture ends");
            return rig.On(w => w.CurrentSnapshot);
        }

        public static void Test_the_window_captures_exports_and_writes_a_report()
        {
            using (var rig = NewRig())
            {
                Check.True(rig.On(w => !w.ExportButton.IsEnabled && !w.ReportButton.IsEnabled), "nothing to export before a capture");
                StartCapture(rig);
                var snapshot = WaitForSnapshot(rig);
                Check.Equal(SnapshotCompleteness.Complete, snapshot.Completeness, "complete: " + snapshot.AbortReason);
                Check.True(rig.On(w => w.SnapshotSummaryText.Text).StartsWith("Catalog snapshot complete", StringComparison.Ordinal), rig.On(w => w.SnapshotSummaryText.Text));
                Check.True(rig.On(w => w.SnapshotSummaryText.Text).Contains("no calibration or restore backup"), "what it is not");

                var folder = SnapshotFileTests.NewFolder();
                var path = Path.Combine(folder, OdriveSnapshotFile.FileName(snapshot.StartedUtc, snapshot.Completeness));
                var result = rig.On(w => w.ExportSnapshotTo(snapshot, path));
                Check.True(result.Succeeded, result.Error);
                Check.True(rig.On(w => w.SnapshotFileText.Text).StartsWith("Exported and read back", StringComparison.Ordinal), rig.On(w => w.SnapshotFileText.Text));

                var again = rig.On(w => w.ExportSnapshotTo(snapshot, path));
                Check.True(!again.Succeeded, "a second export to the same name fails");
                Check.True(rig.On(w => w.SnapshotFileText.Text).StartsWith("Export FAILED", StringComparison.Ordinal), "and says so");

                var report = Path.Combine(folder, "report.md");
                Check.Equal(null, rig.On(w => w.WriteReportTo(snapshot, report)), "report written");
                Check.True(File.ReadAllText(report).Contains("# Odrive-Wheel diagnostic report"), "report content");
                Check.True(rig.On(w => w.WriteReportTo(snapshot, report)) != null, "never replaced");
            }
        }

        public static void Test_the_window_reads_only_catalog_paths_for_a_capture()
        {
            using (var rig = NewRig())
            {
                rig.Rig.Board.ClearLines();
                StartCapture(rig);
                WaitForSnapshot(rig);
                var catalog = new HashSet<string>(OdriveParameterCatalog.LoadEmbedded().Parameters.Select(SnapshotRig.ReadLine));
                foreach (var line in rig.Rig.Board.WireLines) ParameterReaderTests.CheckIsReadOrSync(line);
                foreach (var line in rig.Rig.Board.Lines) Check.True(catalog.Contains(line), "a catalog read: " + line);
            }
        }

        public static void Test_stop_and_port_buttons_work_during_a_capture()
        {
            using (var rig = NewRig())
            {
                rig.Rig.Board.ReplyDelayMs = 15;
                StartCapture(rig);
                Check.True(Wait.Until(() => rig.On(w => w.IsCapturing)), "under way");
                rig.Host.Invoke(w => WindowTests.Click(w.StopButton));
                Check.True(Wait.Until(() => rig.Rig.Board.Value("axis0.current_state") == "1"), "the stop went out");
                var stopped = WaitForSnapshot(rig);
                Check.Equal(SnapshotCompleteness.Partial, stopped.Completeness, "partial");
                Check.Equal("software stop", stopped.AbortReason, "reason");
                Check.True(rig.On(w => w.SnapshotSummaryText.Text).StartsWith("PARTIAL", StringComparison.Ordinal), "shown as partial");

                Check.True(Wait.Until(() => rig.Rig.Service.GetStatus().LastStop.IsFinished), "the stop has finished");
                StartCapture(rig);
                Check.True(Wait.Until(() => rig.On(w => w.IsCapturing)), "under way again, on the set latch");
                rig.Host.Invoke(w => WindowTests.Click(w.PortButton));
                Check.True(Wait.Until(() => rig.Rig.Service.GetStatus().State == SerialConnectionState.Released), "released");
                var released = WaitForSnapshot(rig, stopped);
                Check.Equal(SnapshotCompleteness.Partial, released.Completeness, "partial");
                Check.True(released.AbortReason.Contains("released"), released.AbortReason);
                Check.True(Wait.Until(() => rig.On(w => w.SnapshotAgeText.Visibility) == System.Windows.Visibility.Visible), "the snapshot's connection has ended");
                Check.True(rig.Rig.Service.GetStatus().StopLatched, "the latch is untouched");
            }
        }

        public static void Test_closing_the_window_ends_a_capture()
        {
            using (var rig = NewRig())
            {
                rig.Rig.Board.ReplyDelayMs = 15;
                StartCapture(rig);
                Check.True(Wait.Until(() => rig.On(w => w.IsCapturing)), "under way");
                rig.Host.Close(2000);
                Check.True(!rig.Host.IsOpen, "closed");
                Thread.Sleep(150);
                int lines = rig.Rig.Board.Lines.Length;
                Thread.Sleep(400);
                Check.Equal(lines, rig.Rig.Board.Lines.Length, "no more reads after closing");
            }
        }

        public static void Test_comparing_two_files_is_offline()
        {
            var folder = SnapshotFileTests.NewFolder();
            var a = Path.Combine(folder, "a.json");
            var b = Path.Combine(folder, "b.json");
            Check.True(OdriveSnapshotFile.Export(SnapshotFileTests.Captured(), a, "1").Succeeded, "a");
            File.WriteAllText(b, "{ \"config.brake_resistance\": \"3\" }");

            using (var rig = NewRig())
            {
                rig.Host.Invoke(w => WindowTests.Click(w.PortButton));
                Check.True(Wait.Until(() => rig.Rig.Service.GetStatus().State == SerialConnectionState.Released), "released");
                Thread.Sleep(100);
                rig.Rig.Board.ClearLines();
                int opens = rig.Rig.Board.OpenCount;

                rig.Host.Invoke(w =>
                {
                    w.CompareLeftBox.Text = "\"" + a + "\"";
                    w.CompareRightBox.Text = b;
                    WindowTests.Click(w.CompareFilesButton);
                });
                Check.True(Wait.Until(() => rig.On(w => w.ComparisonBox.Text).Contains("Settings:")), rig.On(w => w.ComparisonBox.Text));
                var text = rig.On(w => w.ComparisonBox.Text);
                Check.True(text.Contains("config.brake_resistance: 2 | 3  DIFFERS") || text.Contains("config.brake_resistance: 2 | 3  unclear"), "the difference");
                Check.True(text.Contains("no metadata"), "the second file proves nothing");

                Thread.Sleep(200);
                Check.Lines(new string[0], rig.Rig.Board.WireLines, "nothing on the line");
                Check.Equal(opens, rig.Rig.Board.OpenCount, "the port was not opened");
                Check.Equal(SerialConnectionState.Released, rig.Rig.Service.GetStatus().State, "and stays released");
            }
        }

        public static void Test_board_against_a_file_never_sends_a_file_key()
        {
            var folder = SnapshotFileTests.NewFolder();
            var file = Path.Combine(folder, "evil.json");
            File.WriteAllText(file, "{ \"w axis0.requested_state 8\": \"1\", \"sys.reboot!\": \"1\", \"axis0.requested_state\": \"8\", \"config.brake_resistance\": \"7\" }");

            using (var rig = NewRig())
            {
                rig.Rig.Board.ClearLines();
                rig.Host.Invoke(w =>
                {
                    w.CompareLeftBox.Text = file;
                    WindowTests.Click(w.CompareBoardButton);
                });
                Check.True(Wait.Until(() => rig.On(w => w.ComparisonBox.Text).Contains("Settings:"), 15000), "compared");
                var text = rig.On(w => w.ComparisonBox.Text);
                Check.True(text.Contains("config.brake_resistance: 7 | 2"), "the setting: " + text);
                Check.True(text.Contains("w axis0.requested_state 8"), "the unknown key is shown");

                var catalog = new HashSet<string>(OdriveParameterCatalog.LoadEmbedded().Parameters.Select(SnapshotRig.ReadLine));
                foreach (var line in rig.Rig.Board.WireLines) ParameterReaderTests.CheckIsReadOrSync(line);
                foreach (var line in rig.Rig.Board.Lines) Check.True(catalog.Contains(line), "a catalog read, not a file key: " + line);
                Check.Equal("8", rig.Rig.Board.Value("axis0.current_state"), "the axis is unchanged");
                Check.Equal("0", rig.Rig.Board.Value("axis0.requested_state"), "nothing requested");
            }
        }

        public static void Test_a_damaged_file_is_named_and_nothing_is_read()
        {
            var folder = SnapshotFileTests.NewFolder();
            var file = Path.Combine(folder, "broken.json");
            File.WriteAllText(file, "{ broken");
            using (var rig = NewRig())
            {
                rig.Rig.Board.ClearLines();
                rig.Host.Invoke(w =>
                {
                    w.CompareLeftBox.Text = file;
                    WindowTests.Click(w.CompareBoardButton);
                });
                Check.True(Wait.Until(() => rig.On(w => w.ComparisonBox.Text).Contains("Nothing was read from the board")), rig.On(w => w.ComparisonBox.Text));
                Thread.Sleep(100);
                Check.True(rig.Rig.Board.Lines.All(l => l == "r vbus_voltage" || l.StartsWith("r axis0.", StringComparison.Ordinal) || l == "r error" || l == "sys.build?"),
                           "only the status poll: " + string.Join(" | ", rig.Rig.Board.Lines));
                Check.Equal(null, rig.On(w => w.CurrentSnapshot), "no snapshot");
            }
        }
    }

    public static class WebClientImportTests
    {
        private static JObject RunWebClient(string valuesPath)
        {
            var script = CatalogInterfaceTests.FromRepoRoot("tests/plugin/WebClientImport.js");
            var html = CatalogInterfaceTests.FromRepoRoot("Odrive-Wheel/tools/odrive-wheel.html");
            var start = new ProcessStartInfo("node", "\"" + script + "\" \"" + html + "\" \"" + valuesPath + "\"")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            Process node;
            try { node = Process.Start(start); }
            catch (System.ComponentModel.Win32Exception ex)
            {
                Check.Fail("node is needed for this test, it runs the web client's own code: " + ex.Message);
                return null;
            }
            using (node)
            {
                var stdout = node.StandardOutput.ReadToEndAsync();
                var stderr = node.StandardError.ReadToEndAsync();
                Check.True(node.WaitForExit(20000), "node ends");
                Check.Equal(0, node.ExitCode, "node: " + stderr.Result);
                return JObject.Parse(stdout.Result);
            }
        }

        // The web client's import as it is, against what the plugin exports. The page itself is no part of the check:
        // its inputs are built from its own SCHEMA the way its renderer does it, see WebClientImport.js.
        public static void Test_the_web_client_imports_the_export_as_its_own()
        {
            var snapshot = SnapshotFileTests.Captured();
            var path = Path.Combine(SnapshotFileTests.NewFolder(), OdriveSnapshotFile.FileName(snapshot.StartedUtc, snapshot.Completeness));
            Check.True(OdriveSnapshotFile.Export(snapshot, path, "1").Succeeded, "exported");
            var exported = snapshot.ConfigurationValues().ToList();

            var web = RunWebClient(path);
            if (web == null) return;

            var schema = new HashSet<string>(web["schemaPaths"].Select(t => (string)t));
            foreach (var pair in exported) Check.True(schema.Contains(pair.Key), "the web client has an input for " + pair.Key);

            var imported = (JObject)web["imported"];
            Check.Lines(exported.Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal).ToArray(),
                        imported.Properties().Select(p => p.Name).OrderBy(k => k, StringComparer.Ordinal).ToArray(), "it loads every value, and nothing else");
            foreach (var pair in exported)
            {
                var input = (JObject)imported[pair.Key];
                if ((string)input["type"] == "bool") Check.Equal(pair.Value == "1", (bool)input["checked"], "checkbox " + pair.Key);
                else Check.Equal(pair.Value, (string)input["value"], "input " + pair.Key);
            }
            Check.True(web["logs"].Any(l => ((string)l).Contains("import: " + exported.Count + " campos carregados")), "its own count: " + web["logs"]);
            Check.True(web["toasts"].Any(t => (string)t == "import.loaded_only"), "loaded only, nothing written");

            // Its own export of the same replies is the plugin's file, value for value.
            var webExport = (JObject)web["webExport"];
            Check.Lines(exported.Select(p => p.Key + "=" + p.Value).OrderBy(k => k, StringComparer.Ordinal).ToArray(),
                        webExport.Properties().Select(p => p.Name + "=" + (string)p.Value).OrderBy(k => k, StringComparer.Ordinal).ToArray(),
                        "the web client would export the same");

            var skipped = web["readonlyExportPaths"].Select(t => (string)t).OrderBy(k => k, StringComparer.Ordinal).ToArray();
            Check.Lines(skipped, OdriveSnapshotCapture.WebClientExportSkips.OrderBy(k => k, StringComparer.Ordinal).ToArray(), "the same paths left out");
        }

        public static void Test_the_web_client_imports_a_snapshot_with_failures_and_older_replies()
        {
            var snapshot = SnapshotFileTests.Captured(b => { SnapshotRig.Older(b); b.ReplyOverride = l => l == "r axis0.motor.config.torque_lim" ? "garbage" : null; });
            Check.Equal(SnapshotCompleteness.WithFailures, snapshot.Completeness, "with a failure");
            var path = Path.Combine(SnapshotFileTests.NewFolder(), "older.json");
            Check.True(OdriveSnapshotFile.Export(snapshot, path, "1").Succeeded, "exported");

            var web = RunWebClient(path);
            if (web == null) return;
            var imported = (JObject)web["imported"];
            Check.Equal(snapshot.ConfigurationValues().Count(), imported.Count, "every value it has");
            Check.True(imported["axis0.motor.config.torque_lim"] == null, "the unusable reply is not in the file");
        }
    }
}

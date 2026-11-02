using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls.Primitives;

namespace OdriveWheel.SimHubPlugin.Tests
{
    using R = OdrivePowerBrakeRules;

    public static class FirmwareCapabilityParserTests
    {
        private static bool Parses(string reply) => FirmwareCapabilities.TryParse(reply, 1, out _, out _);

        public static void Test_the_declaration_parses_with_its_flags()
        {
            Check.True(FirmwareCapabilities.TryParse("v=1 ascii_full=1 float32_rt=1", 7, out var both, out _), "both");
            Check.Equal(7, both.ConnectGeneration, "bound to its connection");
            Check.True(both.AsciiFull && both.Float32RoundTrip && both.FloatsAreExact, "flags");

            Check.True(FirmwareCapabilities.TryParse("v=1 ascii_full=1 float32_rt=0", 7, out var whole, out _), "whole only");
            Check.True(whole.AsciiFull && !whole.Float32RoundTrip && !whole.FloatsAreExact, "flags");

            Check.True(FirmwareCapabilities.TryParse("v=1 ascii_full=0 float32_rt=1", 7, out var roundTrip, out _), "round trip only");
            Check.True(!roundTrip.AsciiFull && roundTrip.Float32RoundTrip && !roundTrip.FloatsAreExact, "a cut reply is not exact either");

            Check.True(Parses("float32_rt=1 v=1 ascii_full=1"), "named fields, so the order does not matter");
        }

        public static void Test_anything_else_is_no_declaration()
        {
            var refused = new[]
            {
                null, "", " ", "NOT_FOUND", "ERR", "OK", "1",
                // Missing
                "v=1", "ascii_full=1 float32_rt=1", "v=1 ascii_full=1", "v=1 float32_rt=1",
                // Twice, also with the same value
                "v=1 v=1 ascii_full=1 float32_rt=1", "v=1 ascii_full=1 ascii_full=1 float32_rt=1", "v=1 ascii_full=1 float32_rt=1 float32_rt=1",
                "v=1 ascii_full=1 ascii_full=0 float32_rt=1",
                // Another schema, which may mean something else by the same words
                "v=2 ascii_full=1 float32_rt=1", "v=0 ascii_full=1 float32_rt=1", "v=01 ascii_full=1 float32_rt=1",
                "v=1.0 ascii_full=1 float32_rt=1", "v=+1 ascii_full=1 float32_rt=1", "v= ascii_full=1 float32_rt=1", "v ascii_full=1 float32_rt=1",
                // Invalid values
                "v=1 ascii_full=2 float32_rt=1", "v=1 ascii_full= float32_rt=1", "v=1 ascii_full float32_rt=1", "v=1 ascii_full=true float32_rt=1",
                "v=1 ascii_full=1 float32_rt=-1", "v=1 ascii_full=1 float32_rt=1=1", "v=1 ascii_full=01 float32_rt=1",
                // Not this schema's form
                "v=1 ascii_full=1 float32_rt=1 extra=1", "extra v=1 ascii_full=1 float32_rt=1", "V=1 ascii_full=1 float32_rt=1",
                "v=1  ascii_full=1 float32_rt=1", " v=1 ascii_full=1 float32_rt=1", "v=1 ascii_full=1 float32_rt=1 ",
                "v=1,ascii_full=1,float32_rt=1", "v=1\tascii_full=1\tfloat32_rt=1",
            };
            foreach (var reply in refused)
            {
                Check.True(!FirmwareCapabilities.TryParse(reply, 1, out var capabilities, out var problem), "refused: " + reply);
                Check.True(capabilities == null, "nothing comes of it: " + reply);
                Check.True(!string.IsNullOrEmpty(problem), "and it says why: " + reply);
            }
        }

        // What the firmware sends and what the plugin accepts must not drift apart unnoticed.
        public static void Test_the_plugin_reads_what_the_firmware_declares()
        {
            var header = File.ReadAllText(CatalogInterfaceTests.FromRepoRoot("Odrive-Wheel/inc/firmware_capabilities.h"));
            var declared = Regex.Match(header, "#define FIRMWARE_CAPABILITIES_REPLY \"([^\"]*)\"");
            Check.True(declared.Success, "the constant is in the header");
            Check.Equal(FakeBoard.Declaration, declared.Groups[1].Value, "the simulated board declares the same");

            Check.True(FirmwareCapabilities.TryParse(declared.Groups[1].Value, 1, out var capabilities, out var problem), "parsed: " + problem);
            Check.True(capabilities.FloatsAreExact, "and it covers all that this firmware does");
        }
    }

    public static class FirmwareCapabilityDetectionTests
    {
        private const string Line = "sys.capabilities?";

        private static OdriveCapabilityDetector Started(Rig rig, IOdriveParameters parameters = null)
        {
            var detector = new OdriveCapabilityDetector(rig.Service, parameters ?? rig.Parameters);
            detector.Start();
            return detector;
        }

        private static void WaitFor(OdriveCapabilityDetector detector, FirmwareCapabilityState state, int timeoutMs = 5000)
            => Check.True(Wait.Until(() => detector.Status.State == state, timeoutMs), "expected " + state + ", is " + detector.Status.State + " (" + detector.Status.Detail + ")");

        private static FirmwareCapabilities Now(Rig rig, OdriveCapabilityDetector detector) => detector.For(rig.Service.GetStatus());

        private static void Reconnect(Rig rig)
        {
            int before = rig.Service.GetStatus().ConnectGeneration;
            rig.Service.ReleasePort();
            Check.True(Wait.Until(() => rig.Service.GetStatus().State == SerialConnectionState.Released), "released");
            rig.Service.AcquirePort();
            Check.True(Wait.Until(() => rig.Service.GetStatus().IsConnected && rig.Service.GetStatus().ConnectGeneration > before), "a newer connection");
        }

        /// <summary>Holds the replies of the check back, so a test can decide when they arrive.</summary>
        private sealed class HeldCheck : IOdriveParameters
        {
            private readonly Rig _rig;
            public readonly List<TaskCompletionSource<ParameterResult>> Held = new List<TaskCompletionSource<ParameterResult>>();
            public int Asked;
            public Func<int, bool> Hold = _ => true;
            public Func<ParameterResult> Instead;

            public HeldCheck(Rig rig) => _rig = rig;

            public int HeldCount
            {
                get { lock (Held) return Held.Count; }
            }

            public Task<ParameterResult> ReadAsync(string path, FieldProtocol protocol, CommandPriority priority = CommandPriority.Interactive, CancellationToken cancellation = default, SerialOperationContext context = null)
            {
                int attempt = Interlocked.Increment(ref Asked);
                if (Instead != null) return Task.FromResult(Instead());
                if (!Hold(attempt)) return _rig.Parameters.ReadAsync(path, protocol, priority, cancellation, context);
                var held = new TaskCompletionSource<ParameterResult>();
                lock (Held) Held.Add(held);
                return held.Task;
            }

            public Task<ParameterResult> WriteAsync(string path, string value, FieldProtocol protocol, CommandPriority priority = CommandPriority.Interactive, CancellationToken cancellation = default, SerialOperationContext context = null)
                => throw new InvalidOperationException("the check must not write");
            public Task<ParameterResult> ExecOffbAsync(string path, CommandPriority priority = CommandPriority.Interactive, CancellationToken cancellation = default)
                => throw new InvalidOperationException("the check must not execute anything");
            public Task<ParameterResult> SaveOdriveAsync() => throw new InvalidOperationException("the check must not save");
            public ParameterResult Read(string path, FieldProtocol protocol, CommandPriority priority = CommandPriority.Interactive, CancellationToken cancellation = default)
                => throw new InvalidOperationException("the check must not block");
            public ParameterResult Write(string path, string value, FieldProtocol protocol, CommandPriority priority = CommandPriority.Interactive, CancellationToken cancellation = default)
                => throw new InvalidOperationException("the check must not write");
            public ParameterResult ExecOffb(string path, CommandPriority priority = CommandPriority.Interactive, CancellationToken cancellation = default)
                => throw new InvalidOperationException("the check must not execute anything");
            public ParameterResult SaveOdrive() => throw new InvalidOperationException("the check must not save");
        }

        public static void Test_the_check_asks_once_and_does_not_poll()
        {
            using (var rig = new Rig(prepare: b => b.BecomeCurrentFirmware()))
            {
                var detector = Started(rig);
                WaitFor(detector, FirmwareCapabilityState.Declared);

                Check.True(Now(rig, detector).FloatsAreExact, "declared");
                Thread.Sleep(150);
                Check.Lines(new[] { Line }, rig.Board.Lines, "this one line, and nothing else");
                detector.Stop();
            }
        }

        public static void Test_the_check_starts_when_a_connection_comes_up()
        {
            using (var rig = new Rig(connect: false, prepare: b => { b.BecomeCurrentFirmware(); b.Present = false; }))
            {
                var detector = Started(rig);
                Thread.Sleep(50);
                Check.Equal(FirmwareCapabilityState.Unknown, detector.Status.State, "no connection, nothing to ask");
                Check.Equal(0, rig.Board.Lines.Length, "nothing sent");

                rig.Board.Present = true;
                rig.WaitConnected();
                WaitFor(detector, FirmwareCapabilityState.Declared);
                Check.Equal(1, rig.Board.CountLine(Line), "once, though start and event may both report a connection");
                detector.Stop();
            }
        }

        // A handler on the worker would wait for itself here and never get an answer.
        public static void Test_the_connected_event_leaves_the_worker_free()
        {
            using (var rig = new Rig(connect: false, prepare: b => b.Present = false))
            {
                var seen = new List<int>();
                string thread = null;
                string reply = null;
                rig.Service.Connected += generation =>
                {
                    thread = Thread.CurrentThread.Name;
                    reply = rig.Parameters.ReadAsync("sys.swver", FieldProtocol.Openffb).Result.Value;
                    lock (seen) seen.Add(generation);
                };

                rig.Board.Present = true;
                Check.True(Wait.Until(() => { lock (seen) return seen.Count == 1; }), "reported, and the read in it came back");
                Check.Equal("1.17.0", reply, "the reply");
                Check.True(thread != "OdriveSerialWorker", "not on the worker: " + thread);
                Check.Equal(rig.Service.GetStatus().ConnectGeneration, seen[0], "with its generation");
            }
        }

        public static void Test_firmware_that_declares_nothing_stays_in_doubt()
        {
            using (var rig = new Rig())
            {
                var detector = Started(rig);
                WaitFor(detector, FirmwareCapabilityState.NotDeclared);

                Check.True(Now(rig, detector) == null, "no declaration");
                Check.True(detector.Status.Detail.Contains("NOT_FOUND"), detector.Status.Detail);
                Check.True(detector.CanCheckAgain, "it can be asked again");
                detector.Stop();
            }
        }

        public static void Test_an_unusable_reply_is_no_declaration()
        {
            var replies = new[]
            {
                "ERR", "hu", "v=1", "v=1 ascii_full=1", "v=1 v=1 ascii_full=1 float32_rt=1", "v=1 ascii_full=1 ascii_full=1 float32_rt=1",
                "v=2 ascii_full=1 float32_rt=1", "v=1 ascii_full=2 float32_rt=1", "v=1 ascii_full=1 float32_rt=1 extra=1",
            };
            foreach (var reply in replies)
            {
                using (var rig = new Rig(prepare: b => b.SetValue(FirmwareCapabilities.CommandPath, reply)))
                {
                    var detector = Started(rig);
                    WaitFor(detector, FirmwareCapabilityState.NotDeclared);
                    Check.True(Now(rig, detector) == null, "no declaration from: " + reply);
                    detector.Stop();
                }
            }
        }

        public static void Test_a_silent_board_stays_in_doubt_until_asked_again()
        {
            using (var rig = new Rig(prepare: b => b.BecomeCurrentFirmware()))
            {
                rig.Board.MuteOffbChannel = true;
                var detector = Started(rig);
                WaitFor(detector, FirmwareCapabilityState.NotDeclared);
                Check.True(detector.Status.Detail.Contains("no reply"), detector.Status.Detail);
                Check.True(Now(rig, detector) == null, "no declaration");

                rig.Board.MuteOffbChannel = false;
                Thread.Sleep(100);
                Check.Equal(FirmwareCapabilityState.NotDeclared, detector.Status.State, "no try on its own after a timeout");
                Check.Equal(1, rig.Board.CountLine(Line), "one line");

                Check.True(detector.CheckAgain(), "asked for");
                WaitFor(detector, FirmwareCapabilityState.Declared);
                Check.Equal(2, rig.Board.CountLine(Line), "one more line");

                Check.True(!detector.CanCheckAgain, "nothing to do any more");
                Check.True(!detector.CheckAgain(), "nothing to do any more");
                detector.Stop();
            }
        }

        public static void Test_a_check_in_progress_is_not_doubled()
        {
            using (var rig = new Rig())
            {
                var held = new HeldCheck(rig);
                var detector = Started(rig, held);
                Check.True(Wait.Until(() => held.HeldCount == 1), "asked");

                Check.True(!detector.CheckAgain(), "one at a time");
                Check.True(!detector.CanCheckAgain, "and the button stays away");
                Check.Equal(1, Volatile.Read(ref held.Asked), "still one");

                held.Held[0].SetResult(new ParameterResult(ParameterStatus.Timeout, detail: "no reply"));
                WaitFor(detector, FirmwareCapabilityState.NotDeclared);
                Check.True(detector.CanCheckAgain, "now it may");
                detector.Stop();
            }
        }

        // The stop came first and is still set, for as long as the connection lasts if nobody clears it.
        public static void Test_a_latched_stop_does_not_hold_the_check_back_or_loosen_anything()
        {
            using (var rig = new Rig(prepare: b => b.BecomeCurrentFirmware()))
            {
                var bound = new SerialOperationContext(rig.Service.GetStatus().ConnectGeneration, rig.Service.GetStatus().LastStop);
                Check.True(rig.Service.RequestStop().WaitForResult(3000), "stop");
                Check.True(rig.Service.GetStatus().StopLatched, "latched");
                rig.Board.ClearLines();

                var detector = Started(rig);
                WaitFor(detector, FirmwareCapabilityState.Declared);
                Check.True(rig.Service.GetStatus().StopLatched, "and it stays latched");

                // What the latch blocks stays blocked, also with a declaration and a context that ignores the latch.
                var arm = rig.Parameters.WriteAsync("axis0.requested_state", "8", FieldProtocol.Odrive).Result;
                Check.Equal(ParameterStatus.BlockedByStop, arm.Status, "arming");
                var bridged = rig.Parameters.WriteAsync("axis0.requested_state", "8", FieldProtocol.Odrive,
                                                        context: SerialOperationContext.ForConnection(rig.Service.GetStatus().ConnectGeneration)).Result;
                Check.Equal(ParameterStatus.BlockedByStop, bridged.Status, "arming with a context for the connection only");
                var save = rig.Parameters.SaveOdriveAsync().Result;
                Check.Equal(ParameterStatus.BlockedByStop, save.Status, "saving");
                var write = rig.Parameters.WriteAsync("config.brake_resistance", "3", FieldProtocol.Odrive, context: bound).Result;
                Check.Equal(ParameterStatus.BlockedByStop, write.Status, "a write bound to the state before the stop");

                Check.Lines(new[] { Line }, rig.Board.Lines, "only the check went out");
                detector.Stop();
            }
        }

        public static void Test_a_stop_during_the_check_goes_first_and_the_check_follows()
        {
            using (var rig = new Rig(prepare: b => b.BecomeCurrentFirmware()))
            {
                rig.Board.ReplyDelayMs = 400;
                var detector = Started(rig);
                Check.True(Wait.Until(() => rig.Board.CountLine(Line) == 1), "the check is under way");
                Check.Equal(FirmwareCapabilityState.Unknown, detector.Status.State, "no reply yet");

                rig.Board.ReplyDelayMs = 0;
                var stop = rig.Service.RequestStop();
                Check.True(stop.WaitForResult(3000), "the stop should finish");
                Check.Equal(StopState.Confirmed, stop.State, "and it got through");

                WaitFor(detector, FirmwareCapabilityState.Declared);
                Check.True(rig.Service.GetStatus().StopLatched, "the latch is still set");

                var lines = rig.Board.Lines;
                int stopAt = Array.IndexOf(lines, "w axis0.requested_state 1");
                Check.True(stopAt > 0, "the stop went out: " + string.Join(" | ", lines));
                Check.Equal(Line, lines.Last(), "the second try came after it");
                Check.Equal(2, rig.Board.CountLine(Line), "two tries");
                detector.Stop();
            }
        }

        public static void Test_stops_without_end_do_not_keep_the_check_going()
        {
            using (var rig = new Rig())
            {
                var held = new HeldCheck(rig) { Instead = () => new ParameterResult(ParameterStatus.Cancelled, detail: "software stop, not sent") };
                var detector = Started(rig, held);
                WaitFor(detector, FirmwareCapabilityState.NotDeclared);

                Check.Equal(OdriveCapabilityDetector.MaxAttempts, Volatile.Read(ref held.Asked), "a limited number of tries");
                Check.True(detector.Status.Detail.Contains("software stop"), detector.Status.Detail);
                Check.True(detector.CanCheckAgain, "and the button is there");
                detector.Stop();
            }
        }

        // The new connection can be up before the event has reached the check, with the old answer still there.
        public static void Test_a_declaration_is_for_its_own_connection_only()
        {
            using (var rig = new Rig(prepare: b => b.BecomeCurrentFirmware()))
            {
                var detector = Started(rig);
                WaitFor(detector, FirmwareCapabilityState.Declared);
                int generation = rig.Service.GetStatus().ConnectGeneration;

                Check.True(detector.For(generation) != null, "its own");
                Check.True(detector.For(generation + 1) == null, "not the next one");
                Check.True(detector.For(generation - 1) == null, "not an earlier one");

                var later = new SerialStatus(SerialConnectionState.Connected, "COM99", generation + 1, null, null, false);
                Check.True(detector.For(later) == null, "not for a status of the next one");
                var gone = new SerialStatus(SerialConnectionState.Searching, "COM99", generation, null, null, false);
                Check.True(detector.For(gone) == null, "not without a connection");
                Check.True(!detector.For(generation).BelongsTo(later) && !detector.For(generation).BelongsTo(gone), "and it knows that itself");
                detector.Stop();
            }
        }

        /// <summary>Lets a test act in the instant between a status being read and it being looked at.</summary>
        private sealed class SerialWithHook : IOdriveSerialService
        {
            private readonly IOdriveSerialService _inner;
            public Action Once;

            public SerialWithHook(IOdriveSerialService inner) => _inner = inner;

            public SerialStatus GetStatus()
            {
                var status = _inner.GetStatus();
                Interlocked.Exchange(ref Once, null)?.Invoke();
                return status;
            }

            public event Action<int> Connected
            {
                add => _inner.Connected += value;
                remove => _inner.Connected -= value;
            }

            public Task<SerialResult> SendAsync(SerialRequest request) => _inner.SendAsync(request);
            public StopTicket RequestStop() => _inner.RequestStop();
            public void ClearStopLatch() => _inner.ClearStopLatch();
            public void ReleasePort() => _inner.ReleasePort();
            public void AcquirePort() => _inner.AcquirePort();
            public CancellationToken StopToken => _inner.StopToken;
            public void Start() => _inner.Start();
            public void Shutdown(int timeoutMs) => _inner.Shutdown(timeoutMs);
        }

        // The reply is in and the connection looks unchanged, then the new connection comes up and is done
        // with its own check before the old result is stored. That must not replace the newer answer.
        public static void Test_a_result_that_loses_the_race_with_a_new_connection_changes_nothing()
        {
            using (var rig = new Rig())
            {
                var serial = new SerialWithHook(rig.Service);
                var held = new HeldCheck(rig) { Hold = attempt => attempt == 1 };
                var detector = new OdriveCapabilityDetector(serial, held);
                detector.Start();
                Check.True(Wait.Until(() => held.HeldCount == 1), "the first connection asked");

                serial.Once = () =>
                {
                    Reconnect(rig);
                    WaitFor(detector, FirmwareCapabilityState.NotDeclared);
                };
                held.Held[0].SetResult(new ParameterResult(ParameterStatus.Ok, FakeBoard.Declaration, lineWritten: true));

                Thread.Sleep(100);
                Check.Equal(FirmwareCapabilityState.NotDeclared, detector.Status.State, "the answer of the new connection stands");
                Check.True(Now(rig, detector) == null, "no declaration");
                Check.True(detector.CanCheckAgain, "and nothing is still running");
                detector.Stop();
            }
        }

        public static void Test_a_released_port_drops_the_declaration_and_a_new_connection_asks_again()
        {
            using (var rig = new Rig(prepare: b => b.BecomeCurrentFirmware()))
            {
                var detector = Started(rig);
                WaitFor(detector, FirmwareCapabilityState.Declared);
                int first = rig.Service.GetStatus().ConnectGeneration;

                rig.Service.ReleasePort();
                Check.True(Wait.Until(() => rig.Service.GetStatus().State == SerialConnectionState.Released), "released");
                Check.Equal(FirmwareCapabilityState.Unknown, detector.Status.State, "no connection, no statement");
                Check.True(Now(rig, detector) == null, "no declaration");

                // The firmware may have changed while the port was free: another tool can flash it.
                rig.Board.BecomeOlderFirmware();
                rig.Service.AcquirePort();
                Check.True(Wait.Until(() => rig.Service.GetStatus().IsConnected && rig.Service.GetStatus().ConnectGeneration > first), "a newer connection");
                WaitFor(detector, FirmwareCapabilityState.NotDeclared);

                Check.True(Now(rig, detector) == null, "nothing carried over");
                Check.True(detector.For(first) == null, "and the earlier one does not count any more either");
                detector.Stop();
            }
        }

        public static void Test_a_lost_connection_drops_the_declaration()
        {
            using (var rig = new Rig(prepare: b => b.BecomeCurrentFirmware()))
            {
                var detector = Started(rig);
                WaitFor(detector, FirmwareCapabilityState.Declared);
                int first = rig.Service.GetStatus().ConnectGeneration;

                rig.Board.Present = false;
                Check.True(Wait.Until(() => !rig.Service.GetStatus().IsConnected), "disconnected");
                Check.Equal(FirmwareCapabilityState.Unknown, detector.Status.State, "no statement");
                Check.True(Now(rig, detector) == null, "no declaration");

                rig.Board.BecomeOlderFirmware();
                rig.Board.Present = true;
                Check.True(Wait.Until(() => rig.Service.GetStatus().IsConnected && rig.Service.GetStatus().ConnectGeneration > first), "a newer connection");
                WaitFor(detector, FirmwareCapabilityState.NotDeclared);
                Check.True(Now(rig, detector) == null, "nothing carried over");
                detector.Stop();
            }
        }

        // The reply of the first connection arrives when the second is up and has its own answer.
        public static void Test_a_late_declaration_of_an_earlier_connection_changes_nothing()
        {
            using (var rig = new Rig())
            {
                var held = new HeldCheck(rig) { Hold = attempt => attempt == 1 };
                var detector = Started(rig, held);
                Check.True(Wait.Until(() => held.HeldCount == 1), "the first connection asked");

                Reconnect(rig);
                WaitFor(detector, FirmwareCapabilityState.NotDeclared);
                int version = detector.Version;
                var second = rig.Service.GetStatus().ConnectGeneration;

                held.Held[0].SetResult(new ParameterResult(ParameterStatus.Ok, FakeBoard.Declaration, lineWritten: true));
                Thread.Sleep(100);

                Check.True(Now(rig, detector) == null, "the new connection stays in doubt");
                Check.True(detector.For(second) == null, "by generation as well");
                Check.True(detector.For(second - 1) == null, "and nothing is kept for the old one");
                Check.Equal(FirmwareCapabilityState.NotDeclared, detector.Status.State, "unchanged");
                Check.Equal(version, detector.Version, "nothing happened at all");
                detector.Stop();
            }
        }

        // The same with the answer of the new connection still open: the old one must not stand in for it.
        public static void Test_a_late_declaration_does_not_end_the_check_of_the_new_connection()
        {
            using (var rig = new Rig(prepare: b => b.BecomeCurrentFirmware()))
            {
                var held = new HeldCheck(rig);
                var detector = Started(rig, held);
                Check.True(Wait.Until(() => held.HeldCount == 1), "the first connection asked");

                Reconnect(rig);
                Check.True(Wait.Until(() => held.HeldCount == 2), "the second connection asked");

                held.Held[0].SetResult(new ParameterResult(ParameterStatus.Ok, FakeBoard.Declaration, lineWritten: true));
                Thread.Sleep(100);
                Check.Equal(FirmwareCapabilityState.Unknown, detector.Status.State, "still waiting for its own reply");
                Check.True(Now(rig, detector) == null, "no declaration yet");

                held.Held[1].SetResult(new ParameterResult(ParameterStatus.Rejected, detail: "NOT_FOUND", lineWritten: true));
                WaitFor(detector, FirmwareCapabilityState.NotDeclared);
                detector.Stop();
            }
        }

        // On the wire: the frame of the old connection is still in the stream of the new one, whose firmware
        // declares nothing. The reply path is out of sync after a reconnect, so it goes before the sync echo.
        public static void Test_a_late_reply_line_on_a_new_connection_passes_for_nothing()
        {
            using (var rig = new Rig(prepare: b => b.BecomeCurrentFirmware()))
            {
                var detector = Started(rig);
                WaitFor(detector, FirmwareCapabilityState.Declared);

                rig.Service.ReleasePort();
                Check.True(Wait.Until(() => rig.Service.GetStatus().State == SerialConnectionState.Released), "released");
                rig.Board.BecomeOlderFirmware();
                rig.Board.OnNextWrite(() => rig.Board.Inject("[sys.capabilities?|" + FakeBoard.Declaration + "]"));
                rig.Service.AcquirePort();

                WaitFor(detector, FirmwareCapabilityState.NotDeclared);
                Check.True(Now(rig, detector) == null, "the late line unlocks nothing");
                lock (rig.Log) Check.True(rig.Log.Any(l => l.Contains("discarded late reply") && l.Contains(FakeBoard.Declaration)), "it was seen, and discarded");
                detector.Stop();
            }
        }

        public static void Test_a_request_bound_to_the_connection_only()
        {
            using (var rig = new Rig())
            {
                var context = SerialOperationContext.ForConnection(rig.Service.GetStatus().ConnectGeneration);
                Check.True(rig.Service.RequestStop().WaitForResult(3000), "stop");
                Check.True(rig.Parameters.ReadAsync("sys.swver", FieldProtocol.Openffb, context: context).Result.IsOk, "goes out with the latch set");

                rig.Service.ClearStopLatch();
                Reconnect(rig);
                rig.Board.ClearLines();
                var stale = rig.Parameters.ReadAsync("sys.swver", FieldProtocol.Openffb, context: context).Result;
                Check.Equal(ParameterStatus.Unavailable, stale.Status, "not on another connection");
                Check.Equal(0, rig.Board.WireLines.Length, "nothing on the wire");
            }
        }
    }

    public static class FirmwareCapabilityValueTests
    {
        private static ParameterDefinition Float => PowerBrakeRig.Catalog.Find(R.BrakeResistance);
        private static ParameterDefinition Boolean => PowerBrakeRig.Catalog.Find(R.EnableBrake);
        private static readonly FirmwareCapabilities Full = new FirmwareCapabilities(1, true, true);

        private static string Shown(string path, string reply, FirmwareCapabilities capabilities)
            => OdriveParameterFormat.Format(PowerBrakeRig.Catalog.Find(path), reply, capabilities).Text;

        public static void Test_complete_numbers_carry_no_warning_with_a_declaration()
        {
            Check.Equal("281474976710655", Shown("serial_number", "281474976710655", Full), "serial number");
            Check.Equal("123456789", Shown("serial_number", "123456789", Full), "nine digits");
            Check.Equal("123456792 A", Shown("axis0.motor.config.current_lim", "123456792", Full), "float without a point");
            Check.Equal("1.23456792e+08 A", Shown("axis0.motor.config.current_lim", "1.23456792e+08", Full), "exponent");
            Check.Equal("32.012001 A", Shown("axis0.motor.config.current_lim", "32.012001", Full), "the float that started this");
        }

        public static void Test_a_large_error_code_is_decoded_and_never_cut_to_32_bits()
        {
            var shown = Shown("axis0.motor.error", "2147483648", Full);
            Check.True(!shown.Contains("cut off") && !shown.Contains("not decoded"), "decoded: " + shown);
            Check.Equal(OdriveErrorDecoder.Decode(ParameterErrorKind.Motor, 2147483648u), shown, "bit 31");

            // Above 32 bits there are no names yet, but the whole number stays.
            Check.Equal("4294967296 (above 32 bits, not decoded)", Shown("axis0.motor.error", "4294967296", Full), "bit 32");
            Check.Equal("6442450944 (above 32 bits, not decoded)", Shown("axis0.motor.error", "6442450944", Full), "bits 32 and 31");
            Check.Equal("18446744073709551615 (above 32 bits, not decoded)", Shown("axis0.motor.error", "18446744073709551615", Full), "all of them");
        }

        public static void Test_cut_replies_stay_in_doubt_without_a_declaration()
        {
            foreach (var capabilities in new[] { null, new FirmwareCapabilities(1, false, false), new FirmwareCapabilities(1, false, true) })
            {
                Check.Equal("214748364 (may be cut off, not decoded)", Shown("axis0.motor.error", "214748364", capabilities), "error bits");
                Check.Equal("281474976710655 (may be cut off)", Shown("serial_number", "281474976710655", capabilities), "serial number");
                Check.Equal("123456792 A (may be cut off)", Shown("axis0.motor.config.current_lim", "123456792", capabilities), "float");
            }
        }

        public static void Test_a_declaration_makes_any_float_reply_exact()
        {
            foreach (var reply in new[] { "32.012001", "0.015625", "2.123456", "123.45678", "56.000000", "123456792", "-0.0999997407", "1.23456792e+08", "inf" })
                Check.True(OdriveConfigValue.TryParseExactReply(Float, reply, Full, out _), reply);

            foreach (var reply in new[] { "hu", "nan", "", null, "1,5" })
                Check.True(!OdriveConfigValue.TryParseExactReply(Float, reply, Full, out _), "no value: " + reply);

            Check.True(OdriveConfigValue.TryParseExactReply(Float, "32.012001", Full, out double value), "parsed");
            Check.Equal((double)32.012f, value, "as the float32 it stands for");
        }

        public static void Test_without_a_declaration_the_old_doubt_stays()
        {
            foreach (var reply in new[] { "32.012001", "0.015625", "2.123456", "123.45678", "56.000000", "123456792" })
                Check.True(!OdriveConfigValue.TryParseExactReply(Float, reply, null, out _), reply);
            foreach (var reply in new[] { "2.5", "25", "0.100000001", "inf" })
                Check.True(OdriveConfigValue.TryParseExactReply(Float, reply, null, out _), reply);
        }

        // It says floats are rounded or cut, so none of them can be a basis, however it looks.
        public static void Test_a_declaration_without_exact_floats_rules_out_every_float()
        {
            foreach (var capabilities in new[] { new FirmwareCapabilities(1, true, false), new FirmwareCapabilities(1, false, true), new FirmwareCapabilities(1, false, false) })
            {
                foreach (var reply in new[] { "2.5", "25", "32.012001" })
                {
                    Check.True(!OdriveConfigValue.TryParseExactReply(Float, reply, capabilities, out _), reply);
                    Check.Equal(ReadbackVerdict.NotVerified, OdriveConfigValue.CompareReadback(Float, reply, reply, capabilities, out _), reply);
                }
                Check.True(OdriveConfigValue.TryParseExactReply(Boolean, "1", capabilities, out _), "a boolean is no float");
            }
        }

        public static void Test_readback_with_a_declaration_compares_float32_values_only()
        {
            void Verdict(ReadbackVerdict expected, string command, string reply)
                => Check.Equal(expected, OdriveConfigValue.CompareReadback(Float, command, reply, Full, out _), command + " read as " + reply);

            Verdict(ReadbackVerdict.Verified, "32.012", "32.012001");
            Verdict(ReadbackVerdict.Verified, "0.015625", "0.015625");
            Verdict(ReadbackVerdict.Verified, "2.123456", "2.123456");
            Verdict(ReadbackVerdict.Verified, "123456792", "123456792");
            Verdict(ReadbackVerdict.Verified, "2.1", "2.0999999");
            Verdict(ReadbackVerdict.Verified, "1E-05", "9.99999975e-06");
            Verdict(ReadbackVerdict.Differs, "32.012", "32.0120049");      // the next float32
            Verdict(ReadbackVerdict.Differs, "2.1", "2.5");
            Verdict(ReadbackVerdict.Differs, "2.1", "nan");
            Verdict(ReadbackVerdict.Differs, "2.1", "inf");
            Verdict(ReadbackVerdict.NotVerified, "2.1", "hu");

            Check.Equal(ReadbackVerdict.Verified, OdriveConfigValue.CompareReadback(Boolean, "1", "1", Full, out _), "boolean");
            Check.Equal(ReadbackVerdict.Differs, OdriveConfigValue.CompareReadback(Boolean, "1", "0", Full, out _), "boolean");
            Check.Equal(ReadbackVerdict.NotVerified, OdriveConfigValue.CompareReadback(Boolean, "1", "1.000000", Full, out _), "not a boolean");
        }

        public static void Test_readback_without_a_declaration_stays_as_careful_as_it_was()
        {
            foreach (var (command, reply) in new[] { ("32.012", "32.012001"), ("0.015625", "0.015625"), ("123456792", "123456792") })
                Check.Equal(ReadbackVerdict.NotVerified, OdriveConfigValue.CompareReadback(Float, command, reply, out _), command);
        }
    }

    public static class FirmwareCapabilityApplyTests
    {
        private static void Board32(FakeBoard board)
        {
            board.SetValue("vbus_voltage", "30");
            board.SetValue(R.OverVoltage, "32.012");
            board.SetValue(R.MaxRegen, "0.015625");
        }

        private static string[] Unclear(PowerBrakeReview review) => review.Errors.Where(e => e.Contains("unclear")).ToArray();

        // Their replies are 32.012001 and 0.015625, which older firmware could have rounded just the same.
        public static void Test_values_that_look_rounded_work_from_review_to_readback_with_a_declaration()
        {
            using (var rig = new PowerBrakeRig(declared: true, prepare: Board32))
            {
                Check.Equal("32.012001", rig.Reader.Get(PowerBrakeRig.Catalog.Find(R.OverVoltage)).RawValue, "the reply");
                Check.Equal("0.015625", rig.Reader.Get(PowerBrakeRig.Catalog.Find(R.MaxRegen)).RawValue, "the reply");

                var other = rig.Review((R.BrakeResistance, "2.5"));
                Check.True(other.CanApply, "as a basis in the review: " + string.Join(" ", other.Errors));
                var result = rig.Apply(other);
                Check.True(result.AllVerified, "as a basis in the fresh check: " + OdriveStatusText.RamApply(result));

                // Now as the values written: they read back in the same form.
                rig.ReadAll();
                var written = rig.Review((R.OverVoltage, "32.019"), (R.MaxRegen, "0.046875"));
                Check.True(written.CanApply, string.Join(" ", written.Errors));
                rig.Board.ClearLines();
                result = rig.Apply(written);

                Check.True(result.AllVerified, OdriveStatusText.RamApply(result));
                Check.Lines(new[] { "w " + R.OverVoltage + " 32.019", "w " + R.MaxRegen + " 0.046875" }, rig.Writes, "exactly these");
                Check.True(OdriveStatusText.RamApply(result).Contains("verified by readback"), OdriveStatusText.RamApply(result));

                // And they do not lock the editor afterwards, as they did without a declaration.
                rig.ReadAll();
                Check.True(rig.Review((R.BrakeResistance, "3")).CanApply, "the next review works");
            }
        }

        public static void Test_the_same_value_written_again_counts_as_unchanged_with_a_declaration()
        {
            using (var rig = new PowerBrakeRig(declared: true, prepare: Board32))
            {
                var review = rig.Review((R.OverVoltage, "32.012"));
                Check.Equal(1, review.Unchanged.Count, "the reply is exact, so this is known");
                Check.True(!review.CanApply, "nothing to apply");
            }
        }

        // Typed by the user and shown in the summary: there is nothing that could have cut it.
        public static void Test_the_value_to_be_written_is_no_reply_and_carries_no_warning()
        {
            foreach (var declared in new[] { false, true })
            {
                using (var rig = new PowerBrakeRig(declared: declared))
                {
                    var review = rig.Review((R.OverVoltage, "123456792"));
                    Check.True(review.CanApply, string.Join(" ", review.Errors));
                    Check.Equal("123456792 V", review.Changes.Single().NewText, "as typed");
                }
            }
        }

        public static void Test_the_same_values_stay_refused_without_a_declaration()
        {
            // Older firmware cuts the replies, and the board in between replies in full but declares nothing.
            foreach (var whole in new[] { false, true })
            {
                using (var rig = new PowerBrakeRig(prepare: b => { Board32(b); b.SetValue(R.OverVoltage, "32.012001"); b.WholeReplies = whole; }))
                {
                    var review = rig.Review((R.BrakeResistance, "2.5"));
                    Check.True(!review.CanApply, "no basis");
                    Check.True(Unclear(review).Any(e => e.Contains(R.OverVoltage)), string.Join(" ", review.Errors));
                    Check.True(Unclear(review).Any(e => e.Contains(R.MaxRegen)), string.Join(" ", review.Errors));
                }
            }
        }

        // The example of PR #7 on current firmware: the reply shows what the board holds, so this is a real refusal.
        public static void Test_a_relation_is_judged_by_the_exact_value()
        {
            using (var rig = new PowerBrakeRig(declared: true, prepare: b => b.SetValue(R.MaxNegative, "-0.09999974")))
            {
                Check.Equal("-0.0999997407", rig.Reader.Get(PowerBrakeRig.Catalog.Find(R.MaxNegative)).RawValue, "the whole reply");

                var review = rig.Review((R.MaxRegen, "0.0999999"));
                Check.True(!review.CanApply, "over the limit");
                Check.True(review.Errors.Single().Contains("greater in magnitude"), string.Join(" ", review.Errors));
                Check.True(rig.Review((R.MaxRegen, "0.0999")).CanApply, "under it");
            }
        }

        public static void Test_a_declaration_of_another_connection_counts_for_nothing()
        {
            using (var rig = new PowerBrakeRig(prepare: b => { Board32(b); b.WholeReplies = true; }))
            {
                var status = rig.Rig.Service.GetStatus();
                var elsewhere = new FirmwareCapabilities(status.ConnectGeneration + 1, true, true);
                var inputs = new Dictionary<string, string> { [R.BrakeResistance] = "2.5" };

                var review = OdrivePowerBrakeEditor.Review(PowerBrakeRig.Catalog, rig.Reader.Get, status, inputs, elsewhere);
                Check.True(!review.CanApply, "ignored by the review");
                Check.True(Unclear(review).Length == 2, string.Join(" ", review.Errors));
            }
        }

        public static void Test_the_applier_refuses_a_declaration_of_another_connection()
        {
            using (var rig = new PowerBrakeRig(declared: true))
            {
                var valid = rig.Review((R.BrakeResistance, "2.5"));
                var elsewhere = new FirmwareCapabilities(valid.ConnectGeneration + 1, true, true);
                var handedOver = new PowerBrakeReview(new string[0], valid.Changes, new string[0], new string[0],
                                                      valid.ReviewedReplies, valid.Group, valid.ConnectGeneration, valid.LastStop, elsewhere);
                var result = rig.Apply(handedOver);

                Check.True(result.AbortReason != null && result.AbortReason.Contains("another connection"), "reason: " + result.AbortReason);
                Check.Equal(0, rig.Writes.Length, "no write");
            }
        }

        // The review and what follows use one and the same declaration.
        public static void Test_a_review_keeps_the_declaration_it_rests_on()
        {
            using (var rig = new PowerBrakeRig(declared: true))
            {
                var status = rig.Rig.Service.GetStatus();
                Check.True(rig.Review((R.BrakeResistance, "2.5")).Capabilities == rig.Detector.For(status), "the same object");
            }
        }

        public static void Test_an_unconfirmed_readback_ends_it_with_a_declaration()
        {
            foreach (var (reply, outcome) in new[] { ("hu", RamWriteOutcome.NotVerified), ("2.75", RamWriteOutcome.ReadbackDiffers), ("invalid property", RamWriteOutcome.NotVerified) })
            {
                using (var rig = new PowerBrakeRig(declared: true))
                {
                    var review = rig.Review((R.BrakeResistance, "2.5"), (R.MaxRegen, "0.05"));
                    rig.Board.ReplyOverride = line =>
                        line == "r " + R.BrakeResistance && rig.Board.CountLine("w " + R.BrakeResistance + " 2.5") > 0 ? reply : null;
                    var result = rig.Apply(review);

                    Check.True(result.AbortReason != null, reply + ": ended");
                    Check.True(!result.AllVerified, reply + ": no success");
                    Check.Equal(outcome, result.Writes[0].Outcome, reply);
                    Check.Equal(RamWriteOutcome.NotSent, result.Writes[1].Outcome, reply + ": the next one");
                    Check.Lines(new[] { "w " + R.BrakeResistance + " 2.5" }, rig.Writes, reply + ": no retry and no writing back");
                    Check.True(OdriveStatusText.RamApply(result).Contains("Already transferred, and still in place: " + R.BrakeResistance), OdriveStatusText.RamApply(result));
                }
            }
        }

        // A declaration says something about replies, nothing about the state of the axes or the stop.
        public static void Test_every_guard_of_an_apply_stands_with_a_declaration()
        {
            using (var rig = new PowerBrakeRig(declared: true))
            {
                var review = rig.Review((R.BrakeResistance, "2.5"));
                rig.Board.SetValue("axis0.current_state", "8");
                var result = rig.Apply(review);
                Check.True(result.AbortReason != null && result.AbortReason.Contains("axis0.current_state is 8"), "active axis: " + result.AbortReason);
                Check.Equal(0, rig.Writes.Length, "no write");
            }

            foreach (var act in new Action<PowerBrakeRig>[] { PowerBrakeApplyTests.Stop, PowerBrakeApplyTests.StopAndClear, PowerBrakeApplyTests.Reconnect })
            {
                // Right before the write, where only the serial path can still say no.
                PowerBrakeApplyTests.WriteHook hook = null;
                using (var rig = new PowerBrakeRig(r => hook = new PowerBrakeApplyTests.WriteHook(r), declared: true))
                {
                    hook.Before = path => act(rig);
                    var result = rig.Apply(rig.Review((R.BrakeResistance, "2.5")));
                    Check.True(result.AbortReason != null, "ended");
                    Check.Equal(0, rig.Writes.Length, "no write");
                    Check.Equal(RamWriteOutcome.NotSent, result.Writes[0].Outcome, "not sent");
                }

                // Around the readback, where a reply must not confirm anything.
                foreach (var afterTheReply in new[] { false, true })
                {
                    using (var rig = new PowerBrakeRig(r => hook = new PowerBrakeApplyTests.WriteHook(r), declared: true))
                    {
                        var review = rig.Review((R.BrakeResistance, "2.5"));
                        PowerBrakeApplyTests.AtReadback(hook, R.BrakeResistance, () => act(rig), afterTheReply);
                        var result = rig.Apply(review);
                        Check.Equal(RamWriteOutcome.NotVerified, result.Writes[0].Outcome, "no confirmation");
                        Check.True(!result.AllVerified, "no success");
                    }
                }
            }
        }

        public static void Test_a_value_changed_since_the_review_blocks_every_write_with_a_declaration()
        {
            using (var rig = new PowerBrakeRig(declared: true))
            {
                var review = rig.Review((R.BrakeResistance, "2.5"));
                rig.Board.SetValue(R.OverVoltage, "50.25");
                var result = rig.Apply(review);
                Check.True(result.AbortReason != null && result.AbortReason.Contains("changed since the review"), "reason: " + result.AbortReason);
                Check.Equal(0, rig.Writes.Length, "no write");
            }
        }
    }

    public static class FirmwareCapabilityReaderTests
    {
        private static readonly ParameterDefinition Serial = PowerBrakeRig.Catalog.Find("serial_number");
        private static readonly ParameterDefinition MotorError = PowerBrakeRig.Catalog.Find("axis0.motor.error");

        private static OdriveParameterReader NewReader(Rig rig, OdriveCapabilityDetector detector)
            => new OdriveParameterReader((p, priority, cancellation) => rig.Parameters.ReadAsync(p.Path, p.Protocol, priority, cancellation),
                                         rig.Service.GetStatus, detector.For);

        private static void Read(OdriveParameterReader reader, params ParameterDefinition[] parameters)
            => Check.Equal(null, reader.RefreshAsync(parameters, CommandPriority.Batch, CancellationToken.None).Result.AbortReason, "should read");

        // Asked before the reply is here: the same connection, so the same firmware answered both.
        public static void Test_values_read_before_the_check_ended_need_no_second_read()
        {
            using (var rig = new Rig(prepare: b =>
            {
                b.BecomeCurrentFirmware();
                b.SetValue("serial_number", "281474976710655");
                b.SetValue("axis0.motor.error", "2147483648");
            }))
            {
                var detector = new OdriveCapabilityDetector(rig.Service, rig.Parameters);
                var reader = NewReader(rig, detector);
                Read(reader, Serial, MotorError);
                Check.Equal("281474976710655 (may be cut off)", reader.Get(Serial).Text, "in doubt so far");
                Check.Equal("2147483648 (may be cut off, not decoded)", reader.Get(MotorError).Text, "in doubt so far");

                detector.Start();
                Check.True(Wait.Until(() => detector.Status.State == FirmwareCapabilityState.Declared), "declared");

                Check.Equal("281474976710655", reader.Get(Serial).Text, "complete now");
                Check.True(!reader.Get(MotorError).Text.Contains("cut off"), "decoded now: " + reader.Get(MotorError).Text);
                Check.Equal(StatusSeverity.Neutral, reader.Get(Serial).Severity, "no warning either");
                Check.Equal(1, rig.Board.CountLine("r serial_number"), "without another read");
                Check.Equal("281474976710655", reader.Get(Serial).RawValue, "the same reply");
                detector.Stop();
            }
        }

        public static void Test_values_of_another_connection_are_not_upgraded()
        {
            using (var rig = new Rig(prepare: b => b.SetValue("serial_number", "123456789")))
            {
                var detector = new OdriveCapabilityDetector(rig.Service, rig.Parameters);
                detector.Start();
                Check.True(Wait.Until(() => detector.Status.State == FirmwareCapabilityState.NotDeclared), "older firmware");
                var reader = NewReader(rig, detector);
                Read(reader, Serial);

                // Another firmware on the new connection, and it declares everything.
                int first = rig.Service.GetStatus().ConnectGeneration;
                rig.Service.ReleasePort();
                Check.True(Wait.Until(() => rig.Service.GetStatus().State == SerialConnectionState.Released), "released");
                rig.Board.BecomeCurrentFirmware();
                rig.Service.AcquirePort();
                Check.True(Wait.Until(() => rig.Service.GetStatus().IsConnected && rig.Service.GetStatus().ConnectGeneration > first), "a newer connection");
                Check.True(Wait.Until(() => detector.Status.State == FirmwareCapabilityState.Declared), "declared");

                var reading = reader.Get(Serial);
                Check.Equal(ParameterReadState.Outdated, reading.State, "outdated, not current");
                Check.Equal("outdated (was 123456789 (may be cut off))", reading.Text, "and with its old doubt");
                Check.True(reading.RawValue == null, "no value to build on");

                Read(reader, Serial);
                Check.Equal("123456789", reader.Get(Serial).Text, "a new read counts");
                detector.Stop();
            }
        }

        public static void Test_a_declaration_lost_with_the_connection_takes_the_values_with_it()
        {
            using (var rig = new Rig(prepare: b => { b.BecomeCurrentFirmware(); b.SetValue("serial_number", "281474976710655"); }))
            {
                var detector = new OdriveCapabilityDetector(rig.Service, rig.Parameters);
                detector.Start();
                Check.True(Wait.Until(() => detector.Status.State == FirmwareCapabilityState.Declared), "declared");
                var reader = NewReader(rig, detector);
                Read(reader, Serial);
                Check.Equal("281474976710655", reader.Get(Serial).Text, "complete");

                rig.Board.Present = false;
                Check.True(Wait.Until(() => !rig.Service.GetStatus().IsConnected), "disconnected");
                Check.Equal(ParameterReadState.Outdated, reader.Get(Serial).State, "outdated");
                detector.Stop();
            }
        }
    }

    public static class FirmwareCapabilityWindowTests
    {
        private static void Press(WindowTests.WindowRig rig, Func<OdriveConfigWindow, System.Windows.Controls.Button> button)
            => rig.Host.Invoke(w => button(w).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)));

        private static void ReadPowerBrake(WindowTests.WindowRig rig)
        {
            var before = rig.Rig.Board.CountLine("r " + R.BrakeResistance);
            Press(rig, w => w.RefreshButton("odrv", "editor"));
            Check.True(Wait.Until(() => rig.Rig.Board.CountLine("r " + R.BrakeResistance) > before
                                     && rig.On(w => w.RefreshSummary("odrv")).StartsWith("Read at", StringComparison.Ordinal), 5000), rig.On(w => w.RefreshSummary("odrv")));
            Check.True(Wait.Until(() => rig.On(w => w.DisplayedValue("vbus_voltage")) == "30 V"), "the bus voltage comes with the poll: " + rig.On(w => w.DisplayedValue("vbus_voltage")));
        }

        private static void Current(FakeBoard board)
        {
            PowerBrakeRig.PrepareBoard(board);
            board.BecomeCurrentFirmware();
            board.SetValue("vbus_voltage", "30");
            board.SetValue(R.OverVoltage, "32.012");
            board.SetValue("serial_number", "281474976710655");
            board.SetValue("axis0.motor.error", "2147483648");
        }

        public static void Test_the_window_says_what_is_declared_and_shows_complete_numbers()
        {
            using (var rig = new WindowTests.WindowRig(Current, detectCapabilities: true))
            {
                Check.True(Wait.Until(() => rig.On(w => w.CapabilityText.Text).Contains("declared complete and exact")), rig.On(w => w.CapabilityText.Text));
                Check.Equal(Visibility.Collapsed, rig.On(w => w.CheckCapabilitiesButton.Visibility), "nothing to check again");

                Press(rig, w => w.RefreshButton("status"));
                Check.True(Wait.Until(() => rig.On(w => w.DisplayedValue("serial_number")) == "281474976710655", 8000), rig.On(w => w.DisplayedValue("serial_number")));
                var error = rig.On(w => w.DisplayedValue("axis0.motor.error"));
                Check.True(!error.Contains("cut off") && !error.Contains("not decoded"), "decoded: " + error);
            }
        }

        public static void Test_the_window_says_when_nothing_is_declared_and_can_ask_again()
        {
            using (var rig = new WindowTests.WindowRig(b => { Current(b); b.BecomeOlderFirmware(); }, detectCapabilities: true))
            {
                Check.True(Wait.Until(() => rig.On(w => w.CapabilityText.Text).Contains("nothing declared")), rig.On(w => w.CapabilityText.Text));
                Check.True(rig.On(w => w.CapabilityText.Text).Contains("NOT_FOUND"), rig.On(w => w.CapabilityText.Text));
                Check.True(Wait.Until(() => rig.On(w => w.CheckCapabilitiesButton.Visibility) == Visibility.Visible), "the button is there");
                Check.Equal(1, rig.Rig.Board.CountLine("sys.capabilities?"), "asked once");

                rig.Rig.Board.BecomeCurrentFirmware();
                Press(rig, w => w.CheckCapabilitiesButton);
                Check.True(Wait.Until(() => rig.On(w => w.CapabilityText.Text).Contains("declared complete and exact")), rig.On(w => w.CapabilityText.Text));
                Check.True(Wait.Until(() => rig.On(w => w.CheckCapabilitiesButton.Visibility) == Visibility.Collapsed), "the button is gone");
                Check.Equal(2, rig.Rig.Board.CountLine("sys.capabilities?"), "asked once more, on request");
            }
        }

        public static void Test_a_declaration_that_arrives_late_redraws_the_values_and_drops_the_review()
        {
            using (var rig = new WindowTests.WindowRig(b =>
            {
                Current(b);
                b.SetValue(R.OverVoltage, "56");
                b.BecomeOlderFirmware();
                b.WholeReplies = true;
            }, detectCapabilities: true))
            {
                Check.True(Wait.Until(() => rig.On(w => w.CapabilityText.Text).Contains("nothing declared")), rig.On(w => w.CapabilityText.Text));
                ReadPowerBrake(rig);
                Press(rig, w => w.RefreshButton("status"));
                Check.True(Wait.Until(() => rig.On(w => w.DisplayedValue("serial_number")) == "281474976710655 (may be cut off)", 8000), rig.On(w => w.DisplayedValue("serial_number")));

                rig.Host.Invoke(w =>
                {
                    w.TypePowerBrakeValue(R.BrakeResistance, "2.5");
                    w.SelectPowerBrakeField(R.BrakeResistance, true);
                });
                Press(rig, w => w.ReviewButton);
                Check.True(rig.On(w => w.ApplyButton.IsEnabled), "reviewed in the careful way: " + rig.On(w => w.ReviewText.Text));

                rig.Rig.Board.BecomeCurrentFirmware();
                Press(rig, w => w.CheckCapabilitiesButton);

                Check.True(Wait.Until(() => rig.On(w => w.CapabilityText.Text).Contains("declared complete and exact")), rig.On(w => w.CapabilityText.Text));
                Check.True(Wait.Until(() => rig.On(w => w.DisplayedValue("serial_number")) == "281474976710655"), "redrawn: " + rig.On(w => w.DisplayedValue("serial_number")));
                Check.True(Wait.Until(() => !rig.On(w => w.ApplyButton.IsEnabled)), "that review is gone");
                Check.True(rig.On(w => w.ReviewText.Text).Contains("review again"), rig.On(w => w.ReviewText.Text));

                Press(rig, w => w.ReviewButton);
                Check.True(rig.On(w => w.ApplyButton.IsEnabled), "a new one works: " + rig.On(w => w.ReviewText.Text));
            }
        }

        public static void Test_apply_works_in_the_window_on_a_value_that_looks_rounded()
        {
            using (var rig = new WindowTests.WindowRig(Current, detectCapabilities: true))
            {
                Check.True(Wait.Until(() => rig.On(w => w.CapabilityText.Text).Contains("declared complete and exact")), rig.On(w => w.CapabilityText.Text));
                ReadPowerBrake(rig);
                Check.True(Wait.Until(() => rig.On(w => w.DisplayedValue(R.OverVoltage)) == "32.012001 V"), rig.On(w => w.DisplayedValue(R.OverVoltage)));

                rig.Host.Invoke(w =>
                {
                    w.TypePowerBrakeValue(R.OverVoltage, "32.014");
                    w.SelectPowerBrakeField(R.OverVoltage, true);
                });
                Press(rig, w => w.ReviewButton);
                Check.True(rig.On(w => w.ApplyButton.IsEnabled), rig.On(w => w.ReviewText.Text));
                Press(rig, w => w.ApplyButton);

                Check.True(Wait.Until(() => rig.On(w => w.ApplyText.Text).Contains("verified by readback"), 5000), rig.On(w => w.ApplyText.Text));
                Check.Equal(0, rig.Rig.Board.CountLine("ss"), "no save");
                Check.True(Wait.Until(() => rig.On(w => w.DisplayedValue(R.OverVoltage)) == "32.0139999 V"), rig.On(w => w.DisplayedValue(R.OverVoltage)));
            }
        }
    }
}

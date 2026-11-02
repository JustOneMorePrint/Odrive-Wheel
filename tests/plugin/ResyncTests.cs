using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OdriveWheel.SimHubPlugin.Tests
{
    /// <summary>Holds the worker at its next read or write until the test lets it go.</summary>
    internal sealed class WorkerHold
    {
        public ManualResetEventSlim Reached { get; } = new ManualResetEventSlim(false);
        public ManualResetEventSlim Go { get; } = new ManualResetEventSlim(false);

        public static WorkerHold AtNextRead(FakeBoard board)
        {
            var hold = new WorkerHold();
            board.OnNextRead(hold.Stop);
            return hold;
        }

        public static WorkerHold AtNextWrite(FakeBoard board)
        {
            var hold = new WorkerHold();
            board.OnNextWrite(hold.Stop);
            return hold;
        }

        private void Stop()
        {
            Reached.Set();
            Go.Wait();
        }
    }

    /// <summary>Everything that happens while the worker prepares a command: nothing that
    /// was called off in the meantime may reach the board.</summary>
    public static class PreparationTests
    {
        private const string StopAxis0 = "w axis0.requested_state 1";

        // After a timeout the next command has to prepare first.
        private static void LeaveATimeoutBehind(Rig rig)
        {
            rig.Board.Mute = true;
            Check.Equal(SerialOutcome.Timeout, rig.Send("r axis0.error", timeoutMs: 30).Result.Outcome, "the read times out");
            rig.Board.Mute = false;
        }

        public static void Test_stop_during_preparation_keeps_the_command_off_the_wire()
        {
            using (var rig = new Rig())
            {
                LeaveATimeoutBehind(rig);
                var hold = WorkerHold.AtNextRead(rig.Board);
                var save = rig.Send("ss", ReplyMode.SilenceIsOk, 50);
                Check.True(hold.Reached.Wait(3000), "the worker should be preparing");

                var ticket = rig.Service.RequestStop();
                hold.Go.Set();
                Check.True(ticket.WaitForResult(3000), "the stop should finish");

                Check.Equal(0, rig.Board.CountLine("ss"), "ss must not go out");
                Check.Equal("r axis0.error", rig.Board.Lines[0], "first the old read");
                Check.Equal(StopAxis0, rig.Board.Lines[1], "then the stop");
                Check.Equal(SerialOutcome.Cancelled, save.Result.Outcome, "outcome");
                Check.True(!save.Result.LineWritten, "the result says it was not sent");
            }
        }

        public static void Test_cancelling_during_preparation_keeps_the_command_off_the_wire()
        {
            using (var rig = new Rig())
            using (var cancel = new CancellationTokenSource())
            {
                LeaveATimeoutBehind(rig);
                var hold = WorkerHold.AtNextRead(rig.Board);
                var write = rig.Send("w axis0.motor.config.current_lim 99", ReplyMode.SilenceIsOk, 50, cancellation: cancel.Token);
                Check.True(hold.Reached.Wait(3000), "the worker should be preparing");

                cancel.Cancel();
                hold.Go.Set();

                Check.Equal(SerialOutcome.Cancelled, write.Result.Outcome, "outcome");
                Check.True(!write.Result.LineWritten, "the result says it was not sent");
                // Once the next command is through, the worker has passed the cancelled one.
                Check.Equal(SerialOutcome.Reply, rig.Send("r vbus_voltage").Result.Outcome, "next command");
                Check.Equal(0, rig.Board.CountLine("w axis0.motor.config.current_lim 99"), "must not go out");
            }
        }

        public static void Test_release_during_preparation_keeps_the_command_off_the_wire()
        {
            using (var rig = new Rig())
            {
                LeaveATimeoutBehind(rig);
                var hold = WorkerHold.AtNextRead(rig.Board);
                var write = rig.Send("w axis0.motor.config.current_lim 99", ReplyMode.SilenceIsOk, 50);
                Check.True(hold.Reached.Wait(3000), "the worker should be preparing");

                rig.Service.ReleasePort();
                hold.Go.Set();

                Check.Equal(SerialOutcome.PortReleased, write.Result.Outcome, "outcome");
                Check.True(!write.Result.LineWritten, "the result says it was not sent");
                Check.True(Wait.Until(() => !rig.Board.IsOpen), "the port goes back");
                Check.Equal(0, rig.Board.CountLine("w axis0.motor.config.current_lim 99"), "must not go out");
            }
        }

        public static void Test_shutdown_during_preparation_keeps_the_command_off_the_wire()
        {
            var rig = new Rig();
            LeaveATimeoutBehind(rig);
            var hold = WorkerHold.AtNextRead(rig.Board);
            var write = rig.Send("w axis0.motor.config.current_lim 99", ReplyMode.SilenceIsOk, 50);
            Check.True(hold.Reached.Wait(3000), "the worker should be preparing");

            var shutdown = Task.Run(() => rig.Service.Shutdown(3000));
            Check.True(Wait.Until(() => rig.Send("r vbus_voltage").Result.Outcome == SerialOutcome.ShuttingDown), "shutdown should be under way");
            hold.Go.Set();

            Check.True(shutdown.Wait(4000), "shutdown should finish");
            Check.Equal(SerialOutcome.ShuttingDown, write.Result.Outcome, "outcome");
            Check.True(!write.Result.LineWritten, "the result says it was not sent");
            Check.Equal(0, rig.Board.CountLine("w axis0.motor.config.current_lim 99"), "must not go out");
        }

        public static void Test_stop_at_the_last_moment_before_sending_keeps_the_command_off_the_wire()
        {
            using (var rig = new Rig())
            {
                var reached = new ManualResetEventSlim(false);
                var go = new ManualResetEventSlim(false);
                rig.Board.OnNextDiscard(() =>
                {
                    reached.Set();
                    go.Wait();
                });
                var save = rig.Send("ss", ReplyMode.SilenceIsOk, 50);
                Check.True(reached.Wait(3000), "the worker should be about to send");

                var ticket = rig.Service.RequestStop();
                go.Set();
                Check.True(ticket.WaitForResult(3000), "the stop should finish");

                Check.Equal(0, rig.Board.CountLine("ss"), "ss must not go out");
                Check.True(!save.Result.LineWritten, "the result says it was not sent");
            }
        }

        public static void Test_stop_interrupts_a_resync_at_once()
        {
            using (var rig = new Rig())
            {
                LeaveATimeoutBehind(rig);
                rig.Board.Mute = true;   // no echo, the resync would run into its 1 s limit
                var write = rig.Send("w axis0.motor.config.current_lim 99", ReplyMode.SilenceIsOk, 50);
                Check.True(Wait.Until(() => rig.Board.WireLines.Any(l => l.StartsWith("f ", StringComparison.Ordinal))), "the resync should be running");

                var ticket = rig.Service.RequestStop();
                Check.True(Wait.Until(() => ticket.WrittenAfterMs >= 0, 1000), "the stop should go out");
                Check.True(ticket.WrittenAfterMs < 150, "written after " + ticket.WrittenAfterMs + " ms");
                Check.Equal(SerialOutcome.Cancelled, write.Result.Outcome, "outcome");
                Check.True(!write.Result.LineWritten, "the result says it was not sent");
            }
        }

        public static void Test_stop_while_ending_a_partial_line_keeps_the_command_off_the_wire()
        {
            using (var rig = new Rig(o => o.WriteTimeoutMs = 40))
            {
                rig.Board.StallWrites();
                Check.Equal(SerialOutcome.Timeout, rig.Send("r axis0.error").Result.Outcome, "stalled write");
                rig.Board.ReleaseWrites();

                var hold = WorkerHold.AtNextWrite(rig.Board);
                var save = rig.Send("ss", ReplyMode.SilenceIsOk, 50);
                Check.True(hold.Reached.Wait(3000), "the worker should be ending the partial line");

                var ticket = rig.Service.RequestStop();
                hold.Go.Set();
                Check.True(ticket.WaitForResult(3000), "the stop should finish");

                Check.Equal(0, rig.Board.CountLine("ss"), "ss must not go out");
                Check.True(!save.Result.LineWritten, "the result says it was not sent");
            }
        }

        public static void Test_command_that_expires_during_preparation_is_not_sent()
        {
            using (var rig = new Rig(o => o.MaxQueueWaitMs = 60))
            {
                LeaveATimeoutBehind(rig);
                var hold = WorkerHold.AtNextRead(rig.Board);
                var write = rig.Send("w axis0.motor.config.current_lim 99", ReplyMode.SilenceIsOk, 50);
                Check.True(hold.Reached.Wait(3000), "the worker should be preparing");
                Thread.Sleep(90);
                hold.Go.Set();

                Check.Equal(SerialOutcome.Timeout, write.Result.Outcome, "outcome");
                Check.True(!write.Result.LineWritten, "the result says it was not sent");
                Check.Equal(0, rig.Board.CountLine("w axis0.motor.config.current_lim 99"), "must not go out");
            }
        }

        public static void Test_results_tell_sent_from_not_sent()
        {
            using (var rig = new Rig())
            {
                rig.Board.Mute = true;
                var running = rig.Send("sys.save!", ReplyMode.OffbEcho, 2000, echo: "[sys.save!");
                var queued  = rig.Send("r vbus_voltage");
                Check.True(Wait.Until(() => rig.Board.Lines.Length == 1), "the command should go out");
                rig.Board.Mute = false;

                rig.Service.RequestStop().WaitForResult(3000);

                Check.Equal(SerialOutcome.Cancelled, running.Result.Outcome, "running command");
                Check.True(running.Result.LineWritten, "the running command did go out");
                Check.True(running.Result.Detail.Contains("already sent"), "says so: " + running.Result.Detail);
                Check.Equal(SerialOutcome.Cancelled, queued.Result.Outcome, "queued command");
                Check.True(!queued.Result.LineWritten, "the queued command did not");
                Check.True(queued.Result.Detail.Contains("not sent"), "says so: " + queued.Result.Detail);
            }
        }

        public static void Test_parameter_results_tell_sent_from_not_sent()
        {
            using (var rig = new Rig())
            {
                Check.True(rig.Parameters.Read("vbus_voltage", FieldProtocol.Odrive).LineWritten, "normal read");
                Check.True(!rig.Parameters.Write("axis.range", "1;2", FieldProtocol.Openffb).LineWritten, "invalid input");

                rig.Board.Mute = true;
                using (var cancel = new CancellationTokenSource())
                {
                    var read = rig.Parameters.ReadAsync("vbus_voltage", FieldProtocol.Odrive, cancellation: cancel.Token);
                    Check.True(Wait.Until(() => rig.Board.CountLine("r vbus_voltage") == 2), "the command should go out");
                    cancel.Cancel();
                    Check.Equal(ParameterStatus.Cancelled, read.Result.Status, "cancelled read");
                    Check.True(read.Result.LineWritten, "it had gone out");
                }
                rig.Board.Mute = false;
            }

            using (var rig = new Rig(connect: false, prepare: b => b.Present = false))
            {
                Check.True(!rig.Parameters.Read("vbus_voltage", FieldProtocol.Odrive).LineWritten, "without a board");
            }
        }
    }

    /// <summary>
    /// The firmware answers ODrive reads with a bare value. After a timeout or an abort a reply
    /// can still be on its way, for any length of time, so waiting a fixed time proves nothing.
    /// </summary>
    public static class ResyncTests
    {
        private static bool IsSyncLine(string line) => line.StartsWith("f ", StringComparison.Ordinal);

        public static void Test_late_reply_after_a_timeout_cannot_answer_the_next_read()
        {
            using (var rig = new Rig())
            {
                rig.Board.SetValue("axis0.error", "11");
                rig.Board.ReplyDelayMs = 200;
                Check.Equal(SerialOutcome.Timeout, rig.Send("r axis0.error", timeoutMs: 100).Result.Outcome, "first read");

                rig.Board.ReplyDelayMs = 400;
                var second = rig.Parameters.ReadAsync("vbus_voltage", FieldProtocol.Odrive);

                Check.True(second.Result.IsOk, "second read: " + second.Result.Detail);
                Check.Equal("48.000", second.Result.Value, "its own value, not the old 11");
            }
        }

        public static void Test_slow_second_read_times_out_instead_of_taking_the_old_value()
        {
            using (var rig = new Rig())
            {
                rig.Board.SetValue("axis0.error", "11");
                rig.Board.ReplyDelayMs = 200;
                Check.Equal(SerialOutcome.Timeout, rig.Send("r axis0.error", timeoutMs: 100).Result.Outcome, "first read");

                rig.Board.ReplyDelayMs = 400;
                var second = rig.Send("r vbus_voltage", timeoutMs: 100);

                Check.Equal(SerialOutcome.Timeout, second.Result.Outcome, "second read");
                Check.Equal(null, second.Result.Reply, "no value");
            }
        }

        public static void Test_late_reply_after_a_cancel_cannot_answer_the_next_read()
        {
            using (var rig = new Rig())
            using (var cancel = new CancellationTokenSource())
            {
                rig.Board.SetValue("axis0.error", "11");
                rig.Board.ReplyDelayMs = 200;
                var first = rig.Send("r axis0.error", cancellation: cancel.Token);
                Check.True(Wait.Until(() => rig.Board.Lines.Length == 1), "the command should go out");
                cancel.Cancel();
                Check.Equal(SerialOutcome.Cancelled, first.Result.Outcome, "first read");

                rig.Board.ReplyDelayMs = 0;
                Check.Equal("48.000", rig.Send("r vbus_voltage").Result.Reply, "second read");
            }
        }

        public static void Test_late_reply_after_a_stop_cannot_answer_the_next_read()
        {
            using (var rig = new Rig())
            {
                rig.Board.SetValue("axis0.error", "11");
                rig.Board.ReplyDelayMs = 300;
                rig.HidState = 1;
                var first = rig.Send("r axis0.error");
                Check.True(Wait.Until(() => rig.Board.Lines.Length == 1), "the command should go out");
                Check.True(rig.Service.RequestStop().WaitForResult(3000), "the stop should finish");
                Check.Equal(SerialOutcome.Cancelled, first.Result.Outcome, "first read");

                rig.Board.ReplyDelayMs = 0;
                Check.Equal("48.000", rig.Send("r vbus_voltage").Result.Reply, "second read");
            }
        }

        public static void Test_late_rejection_of_a_silent_write_cannot_answer_the_next_read()
        {
            using (var rig = new Rig())
            {
                rig.Board.ReplyDelayMs = 200;
                var write = rig.Parameters.Write("axis0.nosuch", "1", FieldProtocol.Odrive);
                Check.True(write.IsOk, "past the silence window it counts as accepted, the known limit");

                rig.Board.ReplyDelayMs = 0;
                Check.Equal("48.000", rig.Send("r vbus_voltage").Result.Reply, "the read gets its own value");
                lock (rig.Log)
                    Check.True(rig.Log.Any(l => l.Contains("invalid property")), "the late rejection shows in the log");
            }
        }

        public static void Test_late_offb_reply_cannot_answer_the_same_read_again()
        {
            using (var rig = new Rig())
            {
                rig.Board.SetValue("axis.range", "900");
                rig.Board.ReplyDelayMs = 200;
                Check.Equal(SerialOutcome.Timeout,
                            rig.Send("axis.range?", ReplyMode.OffbEcho, 100, echo: "[axis.range?").Result.Outcome, "first read");

                rig.Board.ReplyDelayMs = 0;
                rig.Board.SetValue("axis.range", "1080");
                Check.Equal("1080", rig.Parameters.Read("axis.range", FieldProtocol.Openffb).Value, "the new value, not the late 900");
            }
        }

        public static void Test_without_sync_the_next_command_stays_off_the_wire()
        {
            using (var rig = new Rig(o => o.ResyncTimeoutMs = 120))
            {
                rig.Board.Mute = true;
                Check.Equal(SerialOutcome.Timeout, rig.Send("r axis0.error", timeoutMs: 30).Result.Outcome, "first read");

                var write = rig.Send("w axis0.motor.config.current_lim 99", ReplyMode.SilenceIsOk, 50);

                Check.Equal(SerialOutcome.Timeout, write.Result.Outcome, "outcome");
                Check.True(!write.Result.LineWritten, "the result says it was not sent");
                Check.True(write.Result.Detail.Contains("out of sync"), "says why: " + write.Result.Detail);
                Check.Equal(0, rig.Board.CountLine("w axis0.motor.config.current_lim 99"), "must not go out");
                Check.True(rig.Service.GetStatus().IsConnected, "the connection stays");

                rig.Board.Mute = false;
                Check.Equal("48.000", rig.Send("r vbus_voltage").Result.Reply, "recovers as soon as the board answers");
            }
        }

        public static void Test_several_late_replies_all_pass_before_the_next_read()
        {
            using (var rig = new Rig())
            {
                rig.Board.Mute = true;
                Check.Equal(SerialOutcome.Timeout, rig.Send("r axis0.error", timeoutMs: 30).Result.Outcome, "first read");
                rig.Board.Mute = false;

                // The echo comes only after both: one late line alone proves nothing.
                rig.Board.ReplyDelayMs = 200;
                rig.Board.Inject("11", 60);
                rig.Board.Inject("22", 100);

                Check.Equal("48.000", rig.Send("r vbus_voltage").Result.Reply, "its own value");
            }
        }

        public static void Test_stop_never_trusts_a_read_back_without_sync()
        {
            using (var rig = new Rig())
            {
                // No echo comes back, so whatever arrives could be late. It must not even ask.
                rig.Board.Mute = true;
                var ticket = rig.Service.RequestStop();

                Check.True(ticket.WaitForResult(3000), "the stop should finish");
                Check.Equal(StopState.Unconfirmed, ticket.State, "nothing proves IDLE");
                Check.Equal(0, rig.Board.CountLine("r axis0.current_state"), "no read-back without sync");
            }
        }

        public static void Test_stuck_odrive_channel_leaves_offb_commands_working()
        {
            using (var rig = new Rig())
            {
                rig.Board.MuteOdriveChannel = true;
                Check.Equal(SerialOutcome.Timeout, rig.Send("r axis0.error", timeoutMs: 30).Result.Outcome, "first read");

                rig.Board.SetValue("axis.range", "900");
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var offb = rig.Parameters.Read("axis.range", FieldProtocol.Openffb);
                Check.Equal("900", offb.Value, "the echoed command still works");
                Check.True(watch.ElapsedMilliseconds < 800, "without waiting for that channel: " + watch.ElapsedMilliseconds + " ms");

                var plain = rig.Send("r vbus_voltage");
                Check.Equal(SerialOutcome.Timeout, plain.Result.Outcome, "a command without echo waits for the sync");
                Check.True(plain.Result.Detail.Contains("out of sync"), plain.Result.Detail);
                Check.Equal(0, rig.Board.CountLine("r vbus_voltage"), "and stays off the wire");

                rig.Board.MuteOdriveChannel = false;
                Check.Equal("48.000", rig.Send("r vbus_voltage").Result.Reply, "recovers");
            }
        }

        public static void Test_sync_lines_are_repeated_and_never_taken_for_a_reply()
        {
            using (var rig = new Rig(o => o.ResyncRetryMs = 50))
            {
                rig.Board.ReplyDelayMs = 220;
                Check.Equal(SerialOutcome.Timeout, rig.Send("r axis0.error", timeoutMs: 100).Result.Outcome, "first read");

                var second = rig.Send("r vbus_voltage", timeoutMs: 800);

                Check.Equal("48.000", second.Result.Reply, "its own value");
                Check.True(rig.Board.WireLines.Count(IsSyncLine) >= 2, "repeated while no echo came back");
            }
        }

        public static void Test_sync_ends_as_soon_as_the_echo_arrives()
        {
            using (var rig = new Rig())
            {
                rig.Board.Mute = true;
                Check.Equal(SerialOutcome.Timeout, rig.Send("r axis0.error", timeoutMs: 30).Result.Outcome, "first read");
                rig.Board.Mute = false;

                var watch = System.Diagnostics.Stopwatch.StartNew();
                Check.Equal("48.000", rig.Send("r vbus_voltage").Result.Reply, "second read");
                Check.True(watch.ElapsedMilliseconds < 500, "no fixed wait: " + watch.ElapsedMilliseconds + " ms");
                Check.Equal(1, rig.Board.WireLines.Count(IsSyncLine), "one sync line is enough");
            }
        }

        public static void Test_normal_traffic_carries_no_sync_lines()
        {
            using (var rig = new Rig())
            {
                rig.Board.SetValue("axis.range", "900");
                rig.Parameters.Read("vbus_voltage", FieldProtocol.Odrive);
                rig.Parameters.Read("axis.range", FieldProtocol.Openffb);
                rig.Parameters.Write("axis.range", "900", FieldProtocol.Openffb);
                rig.Parameters.Read("axis0.error", FieldProtocol.Odrive);

                Check.Lines(new[] { "r vbus_voltage", "axis.range?", "axis.range=900", "r axis0.error" },
                            rig.Board.WireLines, "exactly the commands");
            }
        }

        public static void Test_silent_write_is_checked_before_the_next_command()
        {
            using (var rig = new Rig())
            {
                rig.Parameters.Write("axis0.motor.config.current_lim", "10", FieldProtocol.Odrive);
                rig.Parameters.Read("vbus_voltage", FieldProtocol.Odrive);

                var wire = rig.Board.WireLines;
                Check.Equal(3, wire.Length, string.Join(" | ", wire));
                Check.Equal("w axis0.motor.config.current_lim 10", wire[0], "the write");
                Check.True(IsSyncLine(wire[1]), "then the sync: " + wire[1]);
                Check.Equal("r vbus_voltage", wire[2], "then the read");
            }
        }

        public static void Test_sync_line_is_read_only_in_the_firmware_syntax()
        {
            // Above AXIS_COUNT the firmware only answers "invalid motor <n>" (cmd_get_feedback).
            Check.True(!OdriveCommandGuard.IsBlockedWhileStopped(OdriveProtocol.SyncRequest(1000000)), "allowed after a stop");
            Check.Equal("f 1000000", OdriveProtocol.SyncRequest(1000000), "request");
            Check.Equal("invalid motor 1000000", OdriveProtocol.SyncEcho(1000000), "echo");
            Check.True(OdriveProtocol.IsSyncEcho("invalid motor 1000001"), "recognised");
            Check.True(!OdriveProtocol.IsSyncEcho("invalid property"), "other errors are not");
        }

        public static void Test_a_stop_read_back_waits_for_the_late_reply_to_pass()
        {
            using (var rig = new Rig())
            {
                // A late "1" and then the real state: the stop must not take the "1" for IDLE.
                rig.Board.SetValue("axis0.error", "1");
                rig.Board.IgnoreStop = true;
                rig.Board.ReplyDelayMs = 80;
                var running = rig.Send("r axis0.error");
                Check.True(Wait.Until(() => rig.Board.Lines.Length == 1), "the command should go out");

                var ticket = rig.Service.RequestStop();
                Check.True(ticket.WaitForResult(3000), "the stop should finish");

                Check.Equal(StopState.Unconfirmed, ticket.State, "the axis is still armed");
                running.Wait(1000);
            }
        }
    }

    /// <summary>
    /// An OpenFFBoard echo names the path, not the request, so a late reply looks exactly like
    /// the current one. These replies also take their own way through the firmware: only a
    /// marker on that way can tell where the stream stands, whatever the ODrive channel does.
    /// </summary>
    public static class OffbResyncTests
    {
        private const string Range = "axis.range";

        private static bool IsOdriveSync(string line) => line.StartsWith("f ", StringComparison.Ordinal);
        private static bool IsOffbSync(string line) => line.StartsWith("odwsync.", StringComparison.Ordinal);

        private static SerialResult ShortRead(Rig rig)
            => rig.Send(Range + "?", ReplyMode.OffbEcho, 100, echo: "[" + Range + "?").Result;

        private static uint LastMarker(Rig rig, Func<string, bool> isSync)
        {
            var line = rig.Board.WireLines.Last(isSync);
            return uint.Parse(new string(line.Where(char.IsDigit).ToArray()));
        }

        // Whatever is still under way may take a few reads to pass, but it must never count.
        private static string ReadUntilOk(Rig rig, string notThis)
        {
            for (int i = 0; i < 8; i++)
            {
                var read = rig.Parameters.Read(Range, FieldProtocol.Openffb);
                if (read.IsOk) return read.Value;
                Check.Equal(ParameterStatus.Timeout, read.Status, "either no value yet");
                Check.True(read.Value != notThis, "or the right one");
            }
            return null;
        }

        public static void Test_late_reply_cannot_answer_a_repeated_read_with_the_odrive_channel_down()
        {
            using (var rig = new Rig())
            {
                rig.Board.MuteOdriveChannel = true;
                rig.Board.SetValue(Range, "900");
                rig.Board.ReplyDelayMs = 600;
                Check.Equal(SerialOutcome.Timeout, ShortRead(rig).Outcome, "first read");

                rig.Board.SetValue(Range, "1080");
                rig.Board.ReplyDelayMs = 1000;
                var second = rig.Parameters.ReadAsync(Range, FieldProtocol.Openffb).Result;

                Check.Equal(ParameterStatus.Timeout, second.Status, "the board is too slow for this read, value " + second.Value);
                Check.Equal(null, second.Value, "no value, certainly not the old 900");

                rig.Board.ReplyDelayMs = 0;
                Check.Equal("1080", ReadUntilOk(rig, "900"), "recovers with the new value");
            }
        }

        public static void Test_repeated_read_gets_its_own_value_with_the_odrive_channel_down()
        {
            using (var rig = new Rig())
            {
                rig.Board.MuteOdriveChannel = true;
                rig.Board.SetValue(Range, "900");
                rig.Board.ReplyDelayMs = 600;
                Check.Equal(SerialOutcome.Timeout, ShortRead(rig).Outcome, "first read");

                rig.Board.SetValue(Range, "1080");
                rig.Board.ReplyDelayMs = 0;
                var second = rig.Parameters.ReadAsync(Range, FieldProtocol.Openffb).Result;

                Check.True(second.IsOk, "second read: " + second.Detail);
                Check.Equal("1080", second.Value, "its own value, not the old 900");
            }
        }

        public static void Test_late_echo_cannot_confirm_a_repeated_write_with_the_odrive_channel_down()
        {
            using (var rig = new Rig())
            {
                rig.Board.MuteOdriveChannel = true;
                rig.Board.ReplyDelayMs = 600;
                var first = rig.Send(Range + "=900", ReplyMode.OffbEcho, 100, echo: "[" + Range + "=");
                Check.Equal(SerialOutcome.Timeout, first.Result.Outcome, "first write");

                rig.Board.ReplyDelayMs = 0;
                var second = rig.Parameters.WriteAsync(Range, "1080", FieldProtocol.Openffb).Result;

                Check.True(second.IsOk, "second write: " + second.Detail);
                Check.Equal("1080", second.Value, "its own echo, not the old 900");
                Check.Equal("1080", rig.Board.Value(Range), "on the board");
            }
        }

        public static void Test_slow_repeated_write_times_out_instead_of_taking_the_old_echo()
        {
            using (var rig = new Rig())
            {
                rig.Board.MuteOdriveChannel = true;
                rig.Board.ReplyDelayMs = 600;
                var first = rig.Send(Range + "=900", ReplyMode.OffbEcho, 100, echo: "[" + Range + "=");
                Check.Equal(SerialOutcome.Timeout, first.Result.Outcome, "first write");

                rig.Board.ReplyDelayMs = 1000;
                var second = rig.Parameters.WriteAsync(Range, "1080", FieldProtocol.Openffb).Result;

                Check.Equal(ParameterStatus.Timeout, second.Status, "too slow for this write, value " + second.Value);
                Check.Equal(null, second.Value, "no value, certainly not the old 900");
            }
        }

        public static void Test_late_ok_cannot_answer_a_repeated_exec()
        {
            using (var rig = new Rig())
            {
                int calls = 0;
                rig.Board.ReplyOverride = line => line != "sys.save!" ? null
                    : Interlocked.Increment(ref calls) == 1 ? "[sys.save!|OK]" : "[sys.save!|ERR]";
                rig.Board.MuteOdriveChannel = true;
                rig.Board.ReplyDelayMs = 600;
                Check.Equal(SerialOutcome.Timeout,
                            rig.Send("sys.save!", ReplyMode.OffbEcho, 100, echo: "[sys.save!").Result.Outcome, "first exec");

                rig.Board.ReplyDelayMs = 0;
                var second = rig.Parameters.ExecOffbAsync("sys.save").Result;

                Check.Equal(ParameterStatus.Rejected, second.Status, "its own ERR, not the old OK");
                Check.Equal(2, calls, "both reached the board");
            }
        }

        public static void Test_an_odrive_echo_says_nothing_about_offb_replies()
        {
            using (var rig = new Rig())
            {
                // The ODrive channel is fast here, its echo overtakes the late reply.
                rig.Board.SetValue(Range, "900");
                rig.Board.ReplyDelayMs = 300;
                Check.Equal(SerialOutcome.Timeout, ShortRead(rig).Outcome, "first read");

                rig.Board.SetValue(Range, "1080");
                rig.Board.ReplyDelayMs = 0;
                Check.Equal("1080", rig.Parameters.ReadAsync(Range, FieldProtocol.Openffb).Result.Value, "its own value");
            }
        }

        public static void Test_without_its_echo_an_offb_command_stays_off_the_wire()
        {
            using (var rig = new Rig(o => o.ResyncTimeoutMs = 120))
            {
                rig.Board.MuteOffbChannel = true;
                Check.Equal(SerialOutcome.Timeout, ShortRead(rig).Outcome, "first read");

                var write = rig.Parameters.WriteAsync(Range, "1080", FieldProtocol.Openffb).Result;

                Check.Equal(ParameterStatus.Timeout, write.Status, "outcome");
                Check.True(!write.LineWritten, "the result says it was not sent");
                Check.True(write.Detail.Contains("out of sync"), "says why: " + write.Detail);
                Check.Equal(0, rig.Board.CountLine(Range + "=1080"), "must not go out");
                Check.True(rig.Service.GetStatus().IsConnected, "the connection stays");

                Check.Equal("48.000", rig.Send("r vbus_voltage").Result.Reply, "the other channel keeps working");

                rig.Board.MuteOffbChannel = false;
                var again = rig.Parameters.WriteAsync(Range, "1080", FieldProtocol.Openffb).Result;
                Check.True(again.IsOk, "recovers as soon as the board answers: " + again.Detail);
                Check.Equal("1080", again.Value, "with its own echo");
            }
        }

        public static void Test_an_echo_counts_only_on_its_own_channel()
        {
            using (var rig = new Rig(o => o.ResyncTimeoutMs = 120))
            {
                rig.Board.MuteOdriveChannel = true;
                Check.Equal(SerialOutcome.Timeout, rig.Send("r axis0.error", timeoutMs: 30).Result.Outcome, "ODrive read");
                rig.Board.OnNextRead(() => rig.Board.Inject(OdriveProtocol.OffbSyncEcho(LastMarker(rig, IsOdriveSync))));

                var plain = rig.Send("r vbus_voltage").Result;

                Check.Equal(SerialOutcome.Timeout, plain.Outcome, "an OpenFFBoard echo proves nothing here");
                Check.Equal(0, rig.Board.CountLine("r vbus_voltage"), "must not go out");
                rig.Board.MuteOdriveChannel = false;

                rig.Board.MuteOffbChannel = true;
                Check.Equal(SerialOutcome.Timeout, ShortRead(rig).Outcome, "OpenFFBoard read");
                rig.Board.OnNextRead(() => rig.Board.Inject(OdriveProtocol.SyncEcho(LastMarker(rig, IsOffbSync))));

                var offb = rig.Parameters.ReadAsync("axis.maxtorque", FieldProtocol.Openffb).Result;

                Check.Equal(ParameterStatus.Timeout, offb.Status, "an ODrive echo proves nothing here");
                Check.Equal(0, rig.Board.CountLine("axis.maxtorque?"), "must not go out");
            }
        }

        public static void Test_an_old_offb_echo_proves_nothing()
        {
            using (var rig = new Rig(o => o.ResyncTimeoutMs = 120))
            {
                rig.Board.MuteOffbChannel = true;
                Check.Equal(SerialOutcome.Timeout, ShortRead(rig).Outcome, "first read");
                Check.Equal(ParameterStatus.Timeout, rig.Parameters.ReadAsync(Range, FieldProtocol.Openffb).Result.Status, "no echo");

                // The echo of a sync line from before: it says nothing about what came after it.
                rig.Board.OnNextRead(() => rig.Board.Inject(OdriveProtocol.OffbSyncEcho(1000000)));
                var read = rig.Parameters.ReadAsync(Range, FieldProtocol.Openffb).Result;

                Check.Equal(ParameterStatus.Timeout, read.Status, "still out of sync");
                Check.Equal(1, rig.Board.CountLine(Range + "?"), "only the first read went out");
            }
        }

        public static void Test_offb_sync_lines_are_repeated_and_never_taken_for_a_reply()
        {
            using (var rig = new Rig(o => o.ResyncRetryMs = 50))
            {
                rig.Board.SetValue(Range, "900");
                rig.Board.ReplyDelayMs = 220;
                Check.Equal(SerialOutcome.Timeout, ShortRead(rig).Outcome, "first read");

                // Without an expected echo any bracket would do.
                var second = rig.Send(Range + "?", ReplyMode.OffbEcho, 800);

                Check.Equal("[axis.range?|900]", second.Result.Reply, "its own reply");
                Check.True(rig.Board.WireLines.Count(IsOffbSync) >= 2, "repeated while no echo came back");
            }
        }

        public static void Test_an_odrive_timeout_costs_offb_commands_nothing()
        {
            using (var rig = new Rig())
            {
                rig.Board.MuteOdriveChannel = true;
                Check.Equal(SerialOutcome.Timeout, rig.Send("r axis0.error", timeoutMs: 30).Result.Outcome, "ODrive read");
                rig.Board.ClearLines();

                rig.Board.SetValue(Range, "900");
                Check.Equal("900", rig.Parameters.Read(Range, FieldProtocol.Openffb).Value, "the OpenFFBoard read");
                Check.Lines(new[] { Range + "?" }, rig.Board.WireLines, "nothing else on the wire");
            }
        }

        public static void Test_an_offb_timeout_costs_odrive_commands_nothing()
        {
            using (var rig = new Rig())
            {
                rig.Board.MuteOffbChannel = true;
                Check.Equal(SerialOutcome.Timeout, ShortRead(rig).Outcome, "OpenFFBoard read");
                rig.Board.ClearLines();

                Check.Equal("48.000", rig.Parameters.Read("vbus_voltage", FieldProtocol.Odrive).Value, "the ODrive read");
                Check.Lines(new[] { "r vbus_voltage" }, rig.Board.WireLines, "nothing else on the wire");
            }
        }

        public static void Test_late_offb_reply_after_a_cancel_cannot_answer_the_same_read()
        {
            using (var rig = new Rig())
            using (var cancel = new CancellationTokenSource())
            {
                rig.Board.SetValue(Range, "900");
                rig.Board.ReplyDelayMs = 200;
                var first = rig.Send(Range + "?", ReplyMode.OffbEcho, echo: "[" + Range + "?", cancellation: cancel.Token);
                Check.True(Wait.Until(() => rig.Board.Lines.Length == 1), "the command should go out");
                cancel.Cancel();
                Check.Equal(SerialOutcome.Cancelled, first.Result.Outcome, "first read");

                rig.Board.SetValue(Range, "1080");
                rig.Board.ReplyDelayMs = 0;
                Check.Equal("1080", rig.Parameters.Read(Range, FieldProtocol.Openffb).Value, "its own value");
            }
        }

        public static void Test_late_offb_reply_after_a_stop_cannot_answer_the_same_read()
        {
            using (var rig = new Rig())
            {
                rig.HidState = 1;
                rig.Board.SetValue(Range, "900");
                rig.Board.MuteOdriveChannel = true;
                rig.Board.ReplyDelayMs = 600;
                var first = rig.Send(Range + "?", ReplyMode.OffbEcho, echo: "[" + Range + "?");
                Check.True(Wait.Until(() => rig.Board.Lines.Length == 1), "the command should go out");
                Check.True(rig.Service.RequestStop().WaitForResult(3000), "the stop should finish");
                Check.Equal(SerialOutcome.Cancelled, first.Result.Outcome, "first read");

                rig.Board.SetValue(Range, "1080");
                rig.Board.ReplyDelayMs = 0;
                Check.Equal("1080", rig.Parameters.Read(Range, FieldProtocol.Openffb).Value, "its own value");
            }
        }

        private static void CheckBothChannelsSync(Rig rig)
        {
            rig.Board.SetValue(Range, "900");
            rig.Board.ClearLines();
            Check.Equal("900", rig.Parameters.Read(Range, FieldProtocol.Openffb).Value, "OpenFFBoard read");
            Check.True(IsOffbSync(rig.Board.WireLines[0]), "first its sync: " + rig.Board.WireLines[0]);

            rig.Board.ClearLines();
            Check.Equal("48.000", rig.Parameters.Read("vbus_voltage", FieldProtocol.Odrive).Value, "ODrive read");
            Check.True(IsOdriveSync(rig.Board.WireLines[0]), "first its sync: " + rig.Board.WireLines[0]);
        }

        public static void Test_both_channels_sync_after_a_partial_line()
        {
            using (var rig = new Rig(o => o.WriteTimeoutMs = 40))
            {
                rig.Board.StallWrites();
                Check.Equal(SerialOutcome.Timeout, rig.Send("r axis0.error").Result.Outcome, "stalled write");
                rig.Board.ReleaseWrites();
                CheckBothChannelsSync(rig);
            }
        }

        public static void Test_both_channels_sync_after_a_reconnect()
        {
            using (var rig = new Rig())
            {
                rig.Board.FailNextWrite = true;
                Check.Equal(SerialOutcome.TransportError, rig.Send("r axis0.error").Result.Outcome, "failed write");
                Check.True(Wait.Until(() => rig.Service.GetStatus().ConnectGeneration == 2), "should reconnect");
                CheckBothChannelsSync(rig);
            }
        }

        public static void Test_both_channels_sync_after_a_release()
        {
            using (var rig = new Rig())
            {
                rig.Service.ReleasePort();
                Check.True(Wait.Until(() => !rig.Board.IsOpen), "the port goes back");
                rig.Service.AcquirePort();
                rig.WaitConnected();
                CheckBothChannelsSync(rig);
            }
        }

        public static void Test_both_channels_sync_after_a_stop()
        {
            using (var rig = new Rig())
            {
                rig.HidState = 1;
                Check.True(rig.Service.RequestStop().WaitForResult(3000), "the stop should finish");
                CheckBothChannelsSync(rig);
            }
        }

        public static void Test_stop_read_back_needs_the_echo_of_the_odrive_channel()
        {
            using (var rig = new Rig())
            {
                // The OpenFFBoard channel answers, but that says nothing about ODrive replies.
                rig.Board.MuteOdriveChannel = true;
                var ticket = rig.Service.RequestStop();

                Check.True(ticket.WaitForResult(3000), "the stop should finish");
                Check.Equal(StopState.Unconfirmed, ticket.State, "nothing proves IDLE");
                Check.Equal(0, rig.Board.CountLine("r axis0.current_state"), "no read-back without sync");
                Check.Equal(0, rig.Board.WireLines.Count(IsOffbSync), "and no OpenFFBoard sync for it");
            }
        }

        public static void Test_offb_sync_line_is_read_only_in_the_firmware_syntax()
        {
            // The class doesn't exist, so parse_one runs no handler. tests/host/test_cmdparser_sync.cpp
            // feeds the same line into the real parser, a change here has to show up there as well.
            Check.Equal("odwsync.m1000000?", OdriveProtocol.OffbSyncRequest(1000000), "request");
            Check.Equal("[odwsync.m1000000?|NOT_FOUND]", OdriveProtocol.OffbSyncEcho(1000000), "echo");
            Check.True(!OdriveCommandGuard.IsBlockedWhileStopped(OdriveProtocol.OffbSyncRequest(1000000)), "allowed after a stop");

            Check.True(OdriveProtocol.TryParseOffbSyncEcho("[odwsync.m1000001?|NOT_FOUND]", out uint marker), "recognised");
            Check.Equal(1000001u, marker, "marker");
            Check.True(!OdriveProtocol.TryParseOffbSyncEcho("[axis.range?|900]", out _), "a normal reply is not");
            Check.True(!OdriveProtocol.TryParseOffbSyncEcho("[odwsync.m1000001?|900]", out _), "only the exact form");
            Check.True(!OdriveProtocol.TryParseOffbSyncEcho("[odwsync.m?|NOT_FOUND]", out _), "without a number");
            Check.True(!OdriveProtocol.TryParseOffbSyncEcho("invalid motor 1000001", out _), "nor the ODrive echo");
            Check.True(!OdriveProtocol.TryParseSyncEcho("[odwsync.m1000001?|NOT_FOUND]", out _), "and the other way round");
        }
    }

    /// <summary>
    /// A command in the queue can simply be dropped. Once the worker has it, only the worker
    /// knows how far the line got, and a cancellation must not take that away.
    /// </summary>
    public static class CancelStateTests
    {
        private const string Path = "axis0.motor.config.current_lim";
        private const string Line = "w " + Path + " 99";

        public static void Test_cancelling_after_the_write_reports_the_line_as_sent()
        {
            using (var rig = new Rig())
            using (var cancel = new CancellationTokenSource())
            {
                // The worker reads right after the write.
                var hold = WorkerHold.AtNextRead(rig.Board);
                var write = rig.Parameters.WriteAsync(Path, "99", FieldProtocol.Odrive, cancellation: cancel.Token);
                Check.True(hold.Reached.Wait(3000), "the worker should wait for a reply");
                Check.Equal(1, rig.Board.CountLine(Line), "the line is on the wire");

                cancel.Cancel();
                hold.Go.Set();

                Check.Equal(ParameterStatus.Cancelled, write.Result.Status, "status");
                Check.True(write.Result.LineWritten, "the result says it was sent");
                Check.True(write.Result.Detail.Contains("already sent"), "says so: " + write.Result.Detail);
                Check.Equal("99", rig.Board.Value(Path), "and the board did get it");
            }
        }

        public static void Test_every_cancel_after_the_recorded_write_reports_the_line_as_sent()
        {
            using (var rig = new Rig())
            {
                for (int i = 1; i <= 10; i++)
                {
                    using (var cancel = new CancellationTokenSource())
                    {
                        var write = rig.Parameters.WriteAsync(Path, "99", FieldProtocol.Odrive, cancellation: cancel.Token);
                        Check.True(Wait.Until(() => rig.Board.CountLine(Line) == i), "the line should go out");
                        cancel.Cancel();
                        Check.True(write.Result.LineWritten, "run " + i + ": " + write.Result.Status + ", " + write.Result.Detail);
                    }
                }
            }
        }

        public static void Test_cancelling_a_running_read_reports_the_line_as_sent()
        {
            using (var rig = new Rig())
            using (var cancel = new CancellationTokenSource())
            {
                rig.Board.Mute = true;
                var hold = WorkerHold.AtNextRead(rig.Board);
                var read = rig.Send("r vbus_voltage", cancellation: cancel.Token);
                Check.True(hold.Reached.Wait(3000), "the worker should wait for a reply");

                cancel.Cancel();
                hold.Go.Set();

                Check.Equal(SerialOutcome.Cancelled, read.Result.Outcome, "outcome");
                Check.True(read.Result.LineWritten, "the result says it was sent");
                Check.True(read.Result.Detail.Contains("already sent"), "says so: " + read.Result.Detail);
            }
        }

        public static void Test_cancelling_running_offb_commands_reports_the_line_as_sent()
        {
            using (var rig = new Rig())
            {
                rig.Board.Mute = true;
                using (var cancel = new CancellationTokenSource())
                {
                    var read = rig.Parameters.ReadAsync("axis.range", FieldProtocol.Openffb, cancellation: cancel.Token);
                    Check.True(Wait.Until(() => rig.Board.CountLine("axis.range?") == 1), "the read should go out");
                    cancel.Cancel();
                    Check.Equal(ParameterStatus.Cancelled, read.Result.Status, "read");
                    Check.True(read.Result.LineWritten, "the read was sent");
                }
                rig.Board.Mute = false;

                rig.Board.ReplyDelayMs = 100;
                using (var cancel = new CancellationTokenSource())
                {
                    var hold = WorkerHold.AtNextRead(rig.Board);
                    var exec = rig.Parameters.ExecOffbAsync("sys.save", cancellation: cancel.Token);
                    Check.True(hold.Reached.Wait(3000), "the worker should sync first");
                    hold.Go.Set();
                    Check.True(Wait.Until(() => rig.Board.CountLine("sys.save!") == 1), "the exec should go out");
                    cancel.Cancel();
                    Check.Equal(ParameterStatus.Cancelled, exec.Result.Status, "exec");
                    Check.True(exec.Result.LineWritten, "the exec was sent");
                }
            }
        }

        public static void Test_timeout_passes_the_sent_state_on()
        {
            using (var rig = new Rig())
            {
                rig.Board.Mute = true;
                foreach (var protocol in new[] { FieldProtocol.Odrive, FieldProtocol.Openffb })
                {
                    var read = rig.Parameters.ReadAsync("vbus_voltage", protocol).Result;
                    Check.Equal(ParameterStatus.Timeout, read.Status, "status");
                    Check.True(read.LineWritten, "the line did go out");
                }
            }
        }

        public static void Test_blocking_write_passes_the_sent_state_on()
        {
            using (var rig = new Rig())
            using (var cancel = new CancellationTokenSource())
            {
                var hold = WorkerHold.AtNextRead(rig.Board);
                var write = Task.Run(() => rig.Parameters.Write(Path, "99", FieldProtocol.Odrive, cancellation: cancel.Token));
                Check.True(hold.Reached.Wait(3000), "the worker should wait for a reply");

                cancel.Cancel();
                hold.Go.Set();

                Check.Equal(ParameterStatus.Cancelled, write.Result.Status, "status");
                Check.True(write.Result.LineWritten, "the result says it was sent");
            }
        }

        public static void Test_cancelling_a_queued_command_says_not_sent_at_once()
        {
            using (var rig = new Rig())
            using (var cancel = new CancellationTokenSource())
            {
                var hold = WorkerHold.AtNextRead(rig.Board);
                var running = rig.Send("r axis0.error");
                Check.True(hold.Reached.Wait(3000), "the worker should be busy");

                var queued = rig.Parameters.WriteAsync(Path, "99", FieldProtocol.Odrive, cancellation: cancel.Token);
                cancel.Cancel();

                Check.True(queued.Wait(1000), "at once, although the worker is held");
                Check.Equal(ParameterStatus.Cancelled, queued.Result.Status, "status");
                Check.True(!queued.Result.LineWritten, "the result says it was not sent");
                Check.True(queued.Result.Detail.Contains("not sent"), "says so: " + queued.Result.Detail);

                hold.Go.Set();
                running.Wait(1000);
                // Once the next command is through, the worker has passed the cancelled one.
                Check.Equal("48.000", rig.Send("r vbus_voltage").Result.Reply, "next command");
                Check.Equal(0, rig.Board.CountLine(Line), "must not go out");
            }
        }

        public static void Test_already_cancelled_token_says_not_sent()
        {
            using (var rig = new Rig())
            using (var cancel = new CancellationTokenSource())
            {
                cancel.Cancel();
                var write = rig.Parameters.WriteAsync(Path, "99", FieldProtocol.Odrive, cancellation: cancel.Token).Result;

                Check.Equal(ParameterStatus.Cancelled, write.Status, "status");
                Check.True(!write.LineWritten, "the result says it was not sent");
                Check.True(write.Detail.Contains("not sent"), "says so: " + write.Detail);
                Check.Equal(0, rig.Board.CountLine(Line), "must not go out");
            }
        }

        public static void Test_cancelling_right_before_the_last_check_keeps_the_line_off_the_wire()
        {
            using (var rig = new Rig())
            using (var cancel = new CancellationTokenSource())
            {
                rig.Board.OnNextDiscard(cancel.Cancel);
                var write = rig.Parameters.WriteAsync(Path, "99", FieldProtocol.Odrive, cancellation: cancel.Token);

                Check.Equal(ParameterStatus.Cancelled, write.Result.Status, "status");
                Check.True(!write.Result.LineWritten, "the result says it was not sent");
                Check.Equal(0, rig.Board.CountLine(Line), "must not go out");
            }
        }

        public static void Test_cancelling_right_after_the_last_check_reports_the_line_as_sent()
        {
            using (var rig = new Rig())
            using (var cancel = new CancellationTokenSource())
            {
                // Too late to hold the line back, but the result has to say so.
                rig.Board.OnNextWrite(cancel.Cancel);
                var write = rig.Parameters.WriteAsync(Path, "99", FieldProtocol.Odrive, cancellation: cancel.Token);

                Check.Equal(ParameterStatus.Cancelled, write.Result.Status, "status");
                Check.True(write.Result.LineWritten, "the result says it was sent");
                Check.Equal(1, rig.Board.CountLine(Line), "it did go out");
            }
        }

        public static void Test_cancelled_write_that_stalls_reports_not_sent()
        {
            using (var rig = new Rig(o => o.WriteTimeoutMs = 40))
            using (var cancel = new CancellationTokenSource())
            {
                rig.Board.StallWrites();
                rig.Board.OnNextWrite(cancel.Cancel);
                var write = rig.Parameters.WriteAsync(Path, "99", FieldProtocol.Odrive, cancellation: cancel.Token);

                Check.True(!write.Result.LineWritten, "the result says it was not sent");
                Check.True(write.Result.Detail.Contains("not sent"), "says so: " + write.Result.Detail);
                Check.Equal(0, rig.Board.CountLine(Line), "it did not go out");
            }
        }

        public static void Test_cancelled_write_that_loses_the_link_reports_the_line_as_sent()
        {
            using (var rig = new Rig())
            using (var cancel = new CancellationTokenSource())
            {
                var hold = WorkerHold.AtNextRead(rig.Board);
                var write = rig.Parameters.WriteAsync(Path, "99", FieldProtocol.Odrive, cancellation: cancel.Token);
                Check.True(hold.Reached.Wait(3000), "the worker should wait for a reply");

                rig.Board.FailNextRead = true;
                cancel.Cancel();
                hold.Go.Set();

                Check.True(write.Result.LineWritten, "the result says it was sent: " + write.Result.Status);
            }
        }

        public static void Test_cancelling_after_the_result_changes_nothing()
        {
            using (var rig = new Rig())
            using (var cancel = new CancellationTokenSource())
            {
                var read = rig.Parameters.ReadAsync("vbus_voltage", FieldProtocol.Odrive, cancellation: cancel.Token);
                Check.Equal("48.000", read.Result.Value, "the read is through");

                cancel.Cancel();

                Check.Equal(ParameterStatus.Ok, read.Result.Status, "status");
                Check.Equal("48.000", rig.Parameters.Read("vbus_voltage", FieldProtocol.Odrive).Value, "the next read needs no sync");
                Check.Equal(0, rig.Board.WireLines.Count(l => l.StartsWith("f ", StringComparison.Ordinal)), "no sync line");
            }
        }

        // Whenever the cancel hits: the result says what happened on the wire.
        public static void Test_the_sent_state_matches_the_wire_whenever_the_cancel_comes()
        {
            using (var rig = new Rig())
            {
                var random = new Random(4711);
                for (int i = 0; i < 150; i++)
                {
                    string line = "w " + Path + " " + i;
                    int spins = random.Next(0, 60000);
                    using (var cancel = new CancellationTokenSource())
                    {
                        var canceller = Task.Run(() =>
                        {
                            Thread.SpinWait(spins);
                            cancel.Cancel();
                        });
                        var result = rig.Send(line, ReplyMode.SilenceIsOk, 5, cancellation: cancel.Token).Result;
                        canceller.Wait();
                        Check.Equal(rig.Board.CountLine(line) == 1, result.LineWritten,
                                    "run " + i + ": " + result.Outcome + ", " + result.Detail);
                    }
                }
            }
        }

        public static void Test_unexpected_error_in_the_worker_still_ends_the_running_command()
        {
            using (var rig = new Rig())
            {
                rig.Board.OnNextRead(() => throw new InvalidOperationException("driver bug"));
                var read = rig.Send("r vbus_voltage");

                Check.True(read.Wait(3000), "only the worker ends a running command, so it must not forget it");
                if (read.IsCompleted)
                {
                    Check.Equal(SerialOutcome.TransportError, read.Result.Outcome, "outcome");
                    Check.True(read.Result.LineWritten, "the line did go out");
                }
                Check.True(Wait.Until(() => rig.Service.GetStatus().ConnectGeneration == 2), "should reconnect");
            }
        }

        public static void Test_stuck_worker_does_not_claim_the_line_stayed_off_the_wire()
        {
            using (var rig = new Rig())
            {
                var hold = WorkerHold.AtNextRead(rig.Board);
                var write = Task.Run(() => rig.Parameters.Write(Path, "99", FieldProtocol.Odrive));
                Check.True(hold.Reached.Wait(3000), "the worker should be stuck");

                Check.Equal(ParameterStatus.Unavailable, write.Result.Status, "the blocking call gives up");
                Check.True(write.Result.Detail.Contains("may have gone out"), "and says what it can't know: " + write.Result.Detail);
                hold.Go.Set();
            }
        }
    }
}

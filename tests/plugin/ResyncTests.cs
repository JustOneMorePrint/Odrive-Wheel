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
                Check.True(watch.ElapsedMilliseconds < 800, "after a short try: " + watch.ElapsedMilliseconds + " ms");

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
}

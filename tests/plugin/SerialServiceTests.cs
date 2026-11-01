using System.Diagnostics;
using System.Threading;

namespace OdriveWheel.SimHubPlugin.Tests
{
    public static class OrderingTests
    {
        public static void Test_only_one_command_is_on_the_wire()
        {
            using (var rig = new Rig())
            {
                rig.Board.Mute = true;
                var first  = rig.Send("r vbus_voltage", timeoutMs: 150);
                var second = rig.Send("r axis0.error");

                Check.True(Wait.Until(() => rig.Board.Lines.Length == 1), "the first command should go out");
                Thread.Sleep(80);
                Check.Lines(new[] { "r vbus_voltage" }, rig.Board.Lines, "the second command must wait for the first");

                Check.Equal(SerialOutcome.Timeout, first.Result.Outcome, "first command, no reply");
                rig.Board.Mute = false;
                Check.Equal(SerialOutcome.Reply, second.Result.Outcome, "second command");
            }
        }

        public static void Test_each_command_gets_its_own_reply_in_order()
        {
            using (var rig = new Rig())
            {
                rig.Board.ReplyDelayMs = 5;
                rig.Board.SetValue("axis0.error", "11");
                rig.Board.SetValue("axis0.motor.error", "22");
                rig.Board.SetValue("axis0.encoder.error", "33");

                var a = rig.Send("r axis0.error");
                var b = rig.Send("r axis0.motor.error");
                var c = rig.Send("r axis0.encoder.error");

                Check.Equal("11", a.Result.Reply, "first reply");
                Check.Equal("22", b.Result.Reply, "second reply");
                Check.Equal("33", c.Result.Reply, "third reply");
                Check.Lines(new[] { "r axis0.error", "r axis0.motor.error", "r axis0.encoder.error" },
                            rig.Board.Lines, "order on the wire");
            }
        }

        public static void Test_interactive_overtakes_batch_and_background()
        {
            using (var rig = new Rig())
            {
                rig.Board.ReplyDelayMs = 40;
                var running = rig.Send("r axis0.error", priority: CommandPriority.Batch);
                Check.True(Wait.Until(() => rig.Board.Lines.Length == 1), "the batch line should go out");

                var background  = rig.Send("r vbus_voltage", priority: CommandPriority.Background);
                var batch       = rig.Send("r axis0.motor.error", priority: CommandPriority.Batch);
                var interactive = rig.Send("r axis0.encoder.error", priority: CommandPriority.Interactive);

                background.Wait(2000);
                batch.Wait(2000);
                interactive.Wait(2000);
                running.Wait(2000);

                Check.Lines(new[] { "r axis0.error", "r axis0.encoder.error", "r axis0.motor.error", "r vbus_voltage" },
                            rig.Board.Lines, "priority order");
            }
        }
    }

    public static class ReplyMatchingTests
    {
        public static void Test_stale_offb_reply_is_not_taken_for_the_answer()
        {
            using (var rig = new Rig())
            {
                rig.Board.ReplyDelayMs = 40;
                var read = rig.Send("axis.maxtorque?", ReplyMode.OffbEcho, echo: "[axis.maxtorque?");
                Check.True(Wait.Until(() => rig.Board.Lines.Length == 1), "the command should go out");

                rig.Board.Inject("[axis.range?|111]");

                Check.Equal("[axis.maxtorque?|1]", read.Result.Reply, "reply to the right command");
            }
        }

        public static void Test_offb_reply_cannot_answer_an_odrive_read()
        {
            using (var rig = new Rig())
            {
                rig.Board.ReplyDelayMs = 40;
                var read = rig.Send("r vbus_voltage");
                Check.True(Wait.Until(() => rig.Board.Lines.Length == 1), "the command should go out");

                rig.Board.Inject("[axis.range?|111]");

                Check.Equal("48.000", read.Result.Reply, "plain reply");
            }
        }

        public static void Test_late_reply_after_a_timeout_does_not_reach_the_next_command()
        {
            using (var rig = new Rig())
            {
                rig.Board.ReplyDelayMs = 115;
                var first = rig.Send("r axis0.error", timeoutMs: 100);
                Check.Equal(SerialOutcome.Timeout, first.Result.Outcome, "first command");

                rig.Board.ReplyDelayMs = 0;
                rig.Board.SetValue("vbus_voltage", "47.500");
                var second = rig.Send("r vbus_voltage");

                Check.Equal("47.500", second.Result.Reply, "second command gets its own reply");
            }
        }

        public static void Test_blank_lines_and_line_ends_are_ignored()
        {
            using (var rig = new Rig())
            {
                rig.Board.ReplyDelayMs = 30;
                rig.Board.ReplyOverride = line => "48.000\r";
                var read = rig.Send("r vbus_voltage");
                Check.True(Wait.Until(() => rig.Board.Lines.Length == 1), "the command should go out");
                rig.Board.Inject("");
                rig.Board.Inject("\r");

                Check.Equal("48.000", read.Result.Reply, "first real line, without its line end");
            }
        }

        public static void Test_offb_reply_may_span_lines()
        {
            using (var rig = new Rig())
            {
                rig.Board.ReplyOverride = line => "[sys.help?|a,0,3";
                var read = rig.Send("sys.help?", ReplyMode.OffbEcho, echo: "[sys.help?");
                Check.True(Wait.Until(() => rig.Board.Lines.Length == 1), "the command should go out");
                rig.Board.Inject("b,1,3]", 10);

                Check.Equal("[sys.help?|a,0,3\nb,1,3]", read.Result.Reply, "whole reply");
                Check.Equal("a,0,3\nb,1,3", OdriveProtocol.ExtractOffbValue(read.Result.Reply), "extracted value");
            }
        }

        public static void Test_silent_write_succeeds_after_the_window()
        {
            using (var rig = new Rig())
            {
                var watch = Stopwatch.StartNew();
                var write = rig.Send("w axis0.motor.config.current_lim 10", ReplyMode.SilenceIsOk, 60);

                Check.Equal(SerialOutcome.Silence, write.Result.Outcome, "outcome");
                Check.True(watch.ElapsedMilliseconds >= 55, "the window should be waited out");
                Check.Equal("10", rig.Board.Value("axis0.motor.config.current_lim"), "value on the board");
            }
        }
    }

    public static class TimeoutAndCancelTests
    {
        public static void Test_no_reply_times_out()
        {
            using (var rig = new Rig())
            {
                rig.Board.Mute = true;
                var read = rig.Send("r vbus_voltage", timeoutMs: 60);
                Check.Equal(SerialOutcome.Timeout, read.Result.Outcome, "outcome");
                Check.True(read.Result.LineWritten, "the line did go out");
            }
        }

        public static void Test_command_expires_in_the_queue_and_never_goes_out()
        {
            using (var rig = new Rig(o => o.MaxQueueWaitMs = 40))
            {
                rig.Board.Mute = true;
                var running = rig.Send("r axis0.error", timeoutMs: 150);
                var waiting = rig.Send("w axis0.motor.config.current_lim 99", ReplyMode.SilenceIsOk, 50);

                Check.Equal(SerialOutcome.Timeout, waiting.Result.Outcome, "queued command");
                Check.Equal("expired in the queue", waiting.Result.Detail, "reason");
                Check.Equal(0, rig.Board.CountLine("w axis0.motor.config.current_lim 99"), "never on the wire");
                running.Wait(1000);
            }
        }

        public static void Test_cancelling_a_queued_command_completes_it_at_once()
        {
            using (var rig = new Rig())
            using (var cancel = new CancellationTokenSource())
            {
                rig.Board.Mute = true;
                var running = rig.Send("r axis0.error", timeoutMs: 400);
                var queued  = rig.Send("r vbus_voltage", cancellation: cancel.Token);

                var watch = Stopwatch.StartNew();
                cancel.Cancel();
                Check.Equal(SerialOutcome.Cancelled, queued.Result.Outcome, "queued command");
                Check.True(watch.ElapsedMilliseconds < 100, "without waiting for the running command");
                Check.True(!queued.Result.LineWritten, "the line did not go out");

                running.Wait(1000);
                Check.Equal(0, rig.Board.CountLine("r vbus_voltage"), "never on the wire");
            }
        }

        public static void Test_cancelling_the_running_command_stops_the_wait()
        {
            using (var rig = new Rig())
            using (var cancel = new CancellationTokenSource())
            {
                rig.Board.Mute = true;
                var running = rig.Send("sys.save!", ReplyMode.OffbEcho, 2000, echo: "[sys.save!", cancellation: cancel.Token);
                Check.True(Wait.Until(() => rig.Board.Lines.Length == 1), "the command should go out");

                var watch = Stopwatch.StartNew();
                cancel.Cancel();
                Check.Equal(SerialOutcome.Cancelled, running.Result.Outcome, "outcome");
                Check.True(watch.ElapsedMilliseconds < 200, "the wait should end at once");
                Check.True(running.Result.LineWritten, "the line did go out");
            }
        }

        public static void Test_full_queue_refuses_more()
        {
            using (var rig = new Rig(o => o.MaxQueuedCommands = 2))
            {
                rig.Board.Mute = true;
                var running = rig.Send("r axis0.error", timeoutMs: 200);
                Check.True(Wait.Until(() => rig.Board.Lines.Length == 1), "the command should go out");

                var a = rig.Send("r vbus_voltage", timeoutMs: 50);
                var b = rig.Send("r vbus_voltage", timeoutMs: 50);
                var c = rig.Send("r vbus_voltage", timeoutMs: 50);

                Check.True(c.IsCompleted, "the third one is refused at once");
                Check.Equal(SerialOutcome.QueueFull, c.Result.Outcome, "outcome");
                running.Wait(1000); a.Wait(1000); b.Wait(1000);
            }
        }
    }

    public static class ConnectionTests
    {
        public static void Test_connects_when_the_board_appears()
        {
            using (var rig = new Rig(connect: false, prepare: b => b.Present = false))
            {
                Thread.Sleep(60);
                Check.Equal(SerialConnectionState.Searching, rig.Service.GetStatus().State, "without a board");
                Check.Equal(0, rig.Board.OpenCount, "nothing to open");

                rig.Board.Present = true;
                rig.WaitConnected();
                Check.Equal("COM99", rig.Service.GetStatus().PortName, "port name");
                Check.Equal(1, rig.Service.GetStatus().ConnectGeneration, "first connect");
            }
        }

        public static void Test_commands_fail_at_once_without_a_connection()
        {
            using (var rig = new Rig(connect: false, prepare: b => b.Present = false))
            {
                var watch = Stopwatch.StartNew();
                var read = rig.Send("r vbus_voltage");
                Check.Equal(SerialOutcome.NotConnected, read.Result.Outcome, "outcome");
                Check.True(watch.ElapsedMilliseconds < 50, "without any wait");
            }
        }

        public static void Test_read_failure_ends_the_command_and_reconnects()
        {
            using (var rig = new Rig())
            {
                rig.Board.ReplyDelayMs = 50;
                var read = rig.Send("r vbus_voltage");
                Check.True(Wait.Until(() => rig.Board.Lines.Length == 1), "the command should go out");
                rig.Board.FailNextRead = true;

                Check.Equal(SerialOutcome.TransportError, read.Result.Outcome, "outcome");
                Check.True(read.Result.LineWritten, "the line did go out");
                Check.True(Wait.Until(() => rig.Service.GetStatus().ConnectGeneration == 2), "should reconnect");
                Check.Equal(2, rig.Board.OpenCount, "port opened again");
                Check.True(rig.Board.CloseCount >= 1, "the broken port was closed");
            }
        }

        public static void Test_write_failure_ends_the_command_and_reconnects()
        {
            using (var rig = new Rig())
            {
                rig.Board.FailNextWrite = true;
                var read = rig.Send("r vbus_voltage");

                Check.Equal(SerialOutcome.TransportError, read.Result.Outcome, "outcome");
                Check.True(!read.Result.LineWritten, "the line did not go out");
                Check.True(Wait.Until(() => rig.Service.GetStatus().ConnectGeneration == 2), "should reconnect");
            }
        }

        public static void Test_nothing_is_replayed_after_a_reconnect()
        {
            using (var rig = new Rig())
            {
                rig.Board.Mute = true;
                var running = rig.Send("r axis0.error");
                var queued  = rig.Send("w axis0.motor.config.current_lim 99", ReplyMode.SilenceIsOk, 50);
                Check.True(Wait.Until(() => rig.Board.Lines.Length == 1), "the command should go out");

                rig.Board.FailNextRead = true;
                Check.Equal(SerialOutcome.TransportError, running.Result.Outcome, "running command");
                Check.Equal(SerialOutcome.NotConnected, queued.Result.Outcome, "queued command");

                rig.Board.Mute = false;
                Check.True(Wait.Until(() => rig.Service.GetStatus().ConnectGeneration == 2), "should reconnect");
                Thread.Sleep(50);
                Check.Equal(0, rig.Board.CountLine("w axis0.motor.config.current_lim 99"), "the queued write is gone");
            }
        }

        public static void Test_port_held_elsewhere_is_reported_and_retried()
        {
            using (var rig = new Rig(connect: false, prepare: b => b.RefuseOpenAsBusy = true))
            {
                Check.True(Wait.Until(() => rig.Service.GetStatus().State == SerialConnectionState.Busy), "should report busy");
                Check.Equal(SerialOutcome.PortBusy, rig.Send("r vbus_voltage").Result.Outcome, "command while busy");

                rig.Board.RefuseOpenAsBusy = false;
                rig.WaitConnected();
            }
        }

        public static void Test_failing_open_is_retried()
        {
            using (var rig = new Rig(connect: false, prepare: b => b.FailOpen = true))
            {
                Check.True(Wait.Until(() => rig.Service.GetStatus().LastError != null), "the failure is reported");
                Check.True(!rig.Service.GetStatus().IsConnected, "not connected");
                Check.Equal(SerialOutcome.NotConnected, rig.Send("r vbus_voltage").Result.Outcome, "command meanwhile");

                rig.Board.FailOpen = false;
                rig.WaitConnected();
                Check.Equal(null, rig.Service.GetStatus().LastError, "the error is gone");
            }
        }

        public static void Test_release_closes_the_port_and_stays_closed_until_takeover()
        {
            using (var rig = new Rig())
            {
                rig.Service.ReleasePort();
                Check.True(Wait.Until(() => rig.Service.GetStatus().State == SerialConnectionState.Released), "should be released");
                Check.True(!rig.Board.IsOpen, "port closed for other applications");
                Check.Equal(SerialOutcome.PortReleased, rig.Send("r vbus_voltage").Result.Outcome, "command while released");

                Thread.Sleep(100);
                Check.Equal(1, rig.Board.OpenCount, "no reconnect on its own");

                rig.Service.AcquirePort();
                rig.WaitConnected();
                Check.Equal(2, rig.Board.OpenCount, "reopened after takeover");
                Check.Equal(SerialOutcome.Reply, rig.Send("r vbus_voltage").Result.Outcome, "command after takeover");
            }
        }

        public static void Test_release_ends_a_running_command_at_once()
        {
            using (var rig = new Rig())
            {
                rig.Board.Mute = true;
                var running = rig.Send("sys.save!", ReplyMode.OffbEcho, 2000, echo: "[sys.save!");
                var queued  = rig.Send("r vbus_voltage");
                Check.True(Wait.Until(() => rig.Board.Lines.Length == 1), "the command should go out");

                var watch = Stopwatch.StartNew();
                rig.Service.ReleasePort();
                Check.Equal(SerialOutcome.PortReleased, running.Result.Outcome, "running command");
                Check.Equal(SerialOutcome.PortReleased, queued.Result.Outcome, "queued command");
                Check.True(watch.ElapsedMilliseconds < 200, "without waiting for the 2 s timeout");
            }
        }

        public static void Test_board_gone_is_noticed_by_the_port_scan()
        {
            using (var rig = new Rig())
            {
                rig.Board.Present = false;
                Check.True(Wait.Until(() => rig.Service.GetStatus().State == SerialConnectionState.Searching), "should search again");
                Check.True(!rig.Board.IsOpen, "port closed");

                rig.Board.Present = true;
                rig.WaitConnected();
                Check.Equal(2, rig.Service.GetStatus().ConnectGeneration, "counts as a new connection");
            }
        }

        public static void Test_failing_port_scan_keeps_the_connection()
        {
            using (var rig = new Rig())
            {
                rig.Board.LocatorThrows = true;
                Thread.Sleep(80);
                Check.True(rig.Service.GetStatus().IsConnected, "still connected");
                Check.Equal(SerialOutcome.Reply, rig.Send("r vbus_voltage").Result.Outcome, "commands still work");
                rig.Board.LocatorThrows = false;
            }
        }

        public static void Test_hanging_port_scan_blocks_nothing()
        {
            using (var rig = new Rig())
            {
                rig.Board.LocatorGate.Reset();
                Thread.Sleep(40);

                Check.Equal(SerialOutcome.Reply, rig.Send("r vbus_voltage").Result.Outcome, "command during the hang");
                var ticket = rig.Service.RequestStop();
                Check.True(ticket.WaitForResult(2000), "stop during the hang");
                Check.Equal(StopState.Confirmed, ticket.State, "stop result");
            }
        }

        public static void Test_write_timeout_keeps_the_connection_and_ends_the_partial_line()
        {
            using (var rig = new Rig(o => o.WriteTimeoutMs = 40))
            {
                rig.Board.StallWrites();
                var stalled = rig.Send("r axis0.error");
                Check.Equal(SerialOutcome.Timeout, stalled.Result.Outcome, "stalled write");
                Check.True(!stalled.Result.LineWritten, "the line did not go out");
                Check.True(rig.Service.GetStatus().IsConnected, "the link may still be fine");

                rig.Board.ReleaseWrites();
                var next = rig.Send("r vbus_voltage");

                // The empty line that ends a partial one is answered with "unknown command".
                Check.Equal("48.000", next.Result.Reply, "the next command gets its own reply");
            }
        }
    }
}

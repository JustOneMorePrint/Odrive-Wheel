using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OdriveWheel.SimHubPlugin.Tests
{
    public static class StopTests
    {
        private const string StopAxis0 = "w axis0.requested_state 1";
        private const string StopAxis1 = "w axis1.requested_state 1";

        public static void Test_stop_sets_both_axes_and_confirms_by_read_back()
        {
            using (var rig = new Rig())
            {
                var ticket = rig.Service.RequestStop();

                Check.True(ticket.WaitForResult(2000), "stop should finish");
                Check.Equal(StopState.Confirmed, ticket.State, "result");
                Check.Equal("serial read-back reports IDLE", ticket.Detail, "how it was confirmed");
                Check.Equal("1", rig.Board.Value("axis0.current_state"), "axis 0 state");
                Check.Equal(StopAxis0, rig.Board.Lines[0], "axis 0 comes first");
                Check.Equal(StopAxis1, rig.Board.Lines[1], "axis 1 follows");
                Check.Equal(1, rig.Board.CountLine(StopAxis0), "one attempt is enough");
                Check.True(ticket.WrittenAfterMs >= 0, "written time is known");
            }
        }

        public static void Test_hid_confirms_without_a_serial_reply()
        {
            using (var rig = new Rig())
            {
                rig.Board.Mute = true;
                rig.HidState = 1;
                var ticket = rig.Service.RequestStop();

                Check.True(ticket.WaitForResult(2000), "stop should finish");
                Check.Equal(StopState.Confirmed, ticket.State, "result");
                Check.Equal("HID reports IDLE", ticket.Detail, "how it was confirmed");
            }
        }

        public static void Test_stop_overtakes_everything_queued()
        {
            using (var rig = new Rig())
            {
                rig.Board.Mute = true;
                var running = rig.Send("sys.save!", ReplyMode.OffbEcho, 2000, echo: "[sys.save!");
                Check.True(Wait.Until(() => rig.Board.Lines.Length == 1), "the running command should go out");

                var queued = new[]
                {
                    rig.Send("axis.range=900", ReplyMode.OffbEcho, echo: "[axis.range=", priority: CommandPriority.Batch),
                    rig.Send("r vbus_voltage", priority: CommandPriority.Background),
                    rig.Send("axis.fxratio=1", ReplyMode.OffbEcho, echo: "[axis.fxratio=", priority: CommandPriority.Interactive),
                };

                rig.Board.Mute = false;
                var ticket = rig.Service.RequestStop();
                Check.True(ticket.WaitForResult(2000), "stop should finish");

                Check.Equal(StopAxis0, rig.Board.Lines[1], "the stop follows the running command directly");
                foreach (var command in queued)
                    Check.Equal(SerialOutcome.Cancelled, command.Result.Outcome, "queued command");
                Check.Equal(SerialOutcome.Cancelled, running.Result.Outcome, "running command");
                Check.Equal(0, rig.Board.CountLine("axis.range=900"), "queued write never went out");
                Check.Equal(0, rig.Board.CountLine("axis.fxratio=1"), "queued write never went out");
            }
        }

        public static void Test_waiting_for_a_reply_delays_the_stop_by_one_read_slice()
        {
            using (var rig = new Rig())
            {
                rig.Board.Mute = true;
                var running = rig.Send("sys.save!", ReplyMode.OffbEcho, 2000, echo: "[sys.save!");
                Check.True(Wait.Until(() => rig.Board.Lines.Length == 1), "the running command should go out");
                Thread.Sleep(20);

                var ticket = rig.Service.RequestStop();
                Check.True(Wait.Until(() => ticket.WrittenAfterMs >= 0, 1000), "the stop should go out");

                // Design value about 30 ms; the limit leaves room for a loaded CI machine.
                Check.True(ticket.WrittenAfterMs < 150, "written after " + ticket.WrittenAfterMs + " ms, not after the 2 s timeout");
            }
        }

        public static void Test_blocked_write_delays_the_stop_by_at_most_the_write_timeout()
        {
            using (var rig = new Rig())
            {
                rig.Board.StallWrites();
                var running = rig.Send("r axis0.error");
                Thread.Sleep(10);

                var ticket = rig.Service.RequestStop();
                Thread.Sleep(205);
                rig.Board.ReleaseWrites();

                Check.True(ticket.WaitForResult(2000), "stop should finish");
                Check.True(ticket.WrittenAfterMs >= 0, "the stop should go out");
                // 200 ms write timeout plus slice; the limit leaves room for a loaded CI machine.
                Check.True(ticket.WrittenAfterMs < 450, "written after " + ticket.WrittenAfterMs + " ms");
                running.Wait(1000);
            }
        }

        public static void Test_permanently_stalled_link_reports_not_delivered_in_time()
        {
            using (var rig = new Rig())
            {
                rig.Board.StallWrites();
                var watch = Stopwatch.StartNew();
                var ticket = rig.Service.RequestStop();

                Check.True(ticket.WaitForResult(3000), "stop should finish");
                Check.Equal(StopState.NotDelivered, ticket.State, "result");
                Check.True(watch.ElapsedMilliseconds < 1500, "after " + watch.ElapsedMilliseconds + " ms");
            }
        }

        public static void Test_request_stop_never_blocks()
        {
            using (var rig = new Rig(o => o.WriteTimeoutMs = 3000))
            {
                rig.Board.StallWrites();
                var running = rig.Send("r axis0.error");
                Thread.Sleep(30);

                var watch = Stopwatch.StartNew();
                var ticket = rig.Service.RequestStop();
                Check.True(watch.ElapsedMilliseconds < 50, "returned after " + watch.ElapsedMilliseconds + " ms");
                Check.Equal(StopState.Requested, ticket.State, "the worker is stuck in the write");
                rig.Board.ReleaseWrites();
                running.Wait(1000);
            }
        }

        public static void Test_stop_repeats_until_the_board_reports_idle()
        {
            using (var rig = new Rig())
            {
                rig.Board.IgnoreStop = true;
                rig.Board.ReplyOverride = line =>
                {
                    if (line == "r axis0.current_state") rig.Board.IgnoreStop = false;
                    return null;
                };

                var ticket = rig.Service.RequestStop();

                Check.True(ticket.WaitForResult(3000), "stop should finish");
                Check.Equal(StopState.Confirmed, ticket.State, "result");
                Check.Equal(2, rig.Board.CountLine(StopAxis0), "second attempt");
            }
        }

        public static void Test_board_that_stays_armed_is_reported_unconfirmed()
        {
            using (var rig = new Rig())
            {
                rig.Board.IgnoreStop = true;
                var ticket = rig.Service.RequestStop();

                Check.True(ticket.WaitForResult(3000), "stop should finish");
                Check.Equal(StopState.Unconfirmed, ticket.State, "result");
                Check.Equal(3, rig.Board.CountLine(StopAxis0), "all attempts used");
            }
        }

        public static void Test_single_late_reply_cannot_fake_the_confirmation()
        {
            using (var rig = new Rig())
            {
                rig.Board.IgnoreStop = true;
                int reads = 0;
                rig.Board.ReplyOverride = line =>
                {
                    // The first read-back gets a stale "1", the real state is still closed loop.
                    if (line == "r axis0.current_state" && Interlocked.Increment(ref reads) == 1) return "1";
                    return null;
                };

                var ticket = rig.Service.RequestStop();

                Check.True(ticket.WaitForResult(3000), "stop should finish");
                Check.Equal(StopState.Unconfirmed, ticket.State, "result");
            }
        }

        public static void Test_no_board_is_reported_not_delivered_at_once()
        {
            using (var rig = new Rig(connect: false, prepare: b => b.Present = false))
            {
                var watch = Stopwatch.StartNew();
                var ticket = rig.Service.RequestStop();

                Check.True(ticket.WaitForResult(1000), "stop should finish");
                Check.Equal(StopState.NotDelivered, ticket.State, "result");
                Check.True(watch.ElapsedMilliseconds < 200, "after " + watch.ElapsedMilliseconds + " ms");
            }
        }

        public static void Test_port_held_elsewhere_is_reported_not_delivered()
        {
            using (var rig = new Rig(connect: false, prepare: b => b.RefuseOpenAsBusy = true))
            {
                Check.True(Wait.Until(() => rig.Service.GetStatus().PortName != null), "the board should be found");
                var ticket = rig.Service.RequestStop();

                Check.True(ticket.WaitForResult(1000), "stop should finish");
                Check.Equal(StopState.NotDelivered, ticket.State, "result");
                Check.True(ticket.Detail.Contains("another application"), "reason: " + ticket.Detail);
            }
        }

        public static void Test_stop_while_released_uses_the_port_and_gives_it_back()
        {
            using (var rig = new Rig())
            {
                rig.Service.ReleasePort();
                Check.True(Wait.Until(() => rig.Service.GetStatus().State == SerialConnectionState.Released), "should be released");
                int generation = rig.Service.GetStatus().ConnectGeneration;

                var ticket = rig.Service.RequestStop();
                Check.True(ticket.WaitForResult(2000), "stop should finish");
                Check.Equal(StopState.Confirmed, ticket.State, "result");
                Check.Equal("1", rig.Board.Value("axis0.current_state"), "axis state");

                Check.True(Wait.Until(() => !rig.Board.IsOpen), "the port goes back");
                Check.Equal(SerialConnectionState.Released, rig.Service.GetStatus().State, "still released");
                Check.Equal(generation, rig.Service.GetStatus().ConnectGeneration, "not a new connection");
            }
        }

        public static void Test_lost_link_during_the_stop_is_reported()
        {
            using (var rig = new Rig())
            {
                rig.Board.FailNextWrite = true;
                var ticket = rig.Service.RequestStop();

                Check.True(ticket.WaitForResult(2000), "stop should finish");
                Check.Equal(StopState.NotDelivered, ticket.State, "the line never went out");
            }
        }

        public static void Test_stop_after_a_reconnect_works()
        {
            using (var rig = new Rig())
            {
                rig.Board.FailNextWrite = true;
                rig.Service.RequestStop().WaitForResult(2000);

                Check.True(Wait.Until(() => rig.Service.GetStatus().ConnectGeneration == 2), "should reconnect");
                var ticket = rig.Service.RequestStop();
                Check.True(ticket.WaitForResult(2000), "second stop should finish");
                Check.Equal(StopState.Confirmed, ticket.State, "second stop");
            }
        }

        public static void Test_second_press_during_a_stop_shares_its_ticket()
        {
            using (var rig = new Rig())
            {
                rig.Board.StallWrites();
                var first  = rig.Service.RequestStop();
                var second = rig.Service.RequestStop();
                Check.True(ReferenceEquals(first, second), "same stop in progress");

                rig.Board.ReleaseWrites();
                first.WaitForResult(3000);

                Check.True(Wait.Until(() =>
                {
                    var third = rig.Service.RequestStop();
                    return !ReferenceEquals(first, third);
                }), "a press after the end starts a new stop");
            }
        }

        public static void Test_stop_token_cancels_and_is_replaced()
        {
            using (var rig = new Rig())
            {
                var token = rig.Service.StopToken;
                Check.True(!token.IsCancellationRequested, "not cancelled yet");

                rig.Service.RequestStop().WaitForResult(2000);

                Check.True(token.IsCancellationRequested, "the old token is cancelled");
                Check.True(!rig.Service.StopToken.IsCancellationRequested, "a new job gets a fresh one");
            }
        }

        public static void Test_latch_blocks_what_can_start_the_motor_until_cleared()
        {
            using (var rig = new Rig())
            {
                rig.Service.RequestStop().WaitForResult(2000);
                Check.True(rig.Service.GetStatus().StopLatched, "latched");
                rig.Board.ClearLines();

                foreach (var line in new[] { "ss", "sr", "se", "sd", "w axis0.requested_state 8", "c 0 1.0",
                                             "sys.reboot!", "sys.format!", "axis.anticogcal!" })
                {
                    var result = rig.Send(line, ReplyMode.SilenceIsOk, 20);
                    Check.Equal(SerialOutcome.BlockedByStop, result.Result.Outcome, line);
                }
                Check.Equal(0, rig.Board.Lines.Length, "nothing reached the board");

                Check.Equal(SerialOutcome.Reply, rig.Send("r axis0.error").Result.Outcome, "reading is still allowed");
                Check.Equal(SerialOutcome.Silence, rig.Send("w axis0.requested_state 1", ReplyMode.SilenceIsOk, 20).Result.Outcome,
                            "another IDLE is still allowed");
                Check.True(rig.Parameters.Write("axis.range", "900", FieldProtocol.Openffb).IsOk, "parameters are still allowed");

                rig.Service.ClearStopLatch();
                Check.True(!rig.Service.GetStatus().StopLatched, "cleared");
                Check.Equal(SerialOutcome.Silence, rig.Send("ss", ReplyMode.SilenceIsOk, 20).Result.Outcome, "ss after clearing");
            }
        }

        public static void Test_latch_counts_from_the_request_not_from_the_delivery()
        {
            using (var rig = new Rig())
            {
                rig.Board.StallWrites();
                var ticket = rig.Service.RequestStop();
                Check.Equal(SerialOutcome.BlockedByStop, rig.Send("ss", ReplyMode.SilenceIsOk, 20).Result.Outcome, "ss right after the press");
                rig.Board.ReleaseWrites();
                ticket.WaitForResult(3000);
            }
        }

        public static void Test_reboot_queued_just_before_the_press_never_goes_out()
        {
            using (var rig = new Rig())
            {
                rig.Board.Mute = true;
                var running = rig.Send("r axis0.error", timeoutMs: 400);
                Check.True(Wait.Until(() => rig.Board.Lines.Length == 1), "the running command should go out");
                var reboot = rig.Send("ss", ReplyMode.SilenceIsOk, 20);

                rig.Board.Mute = false;
                rig.Service.RequestStop().WaitForResult(2000);

                Check.True(new[] { SerialOutcome.Cancelled, SerialOutcome.BlockedByStop }.Contains(reboot.Result.Outcome), "queued ss");
                Check.Equal(0, rig.Board.CountLine("ss"), "never on the wire");
                running.Wait(1000);
            }
        }
    }

    public static class GuardTests
    {
        public static void Test_guard_table()
        {
            var blocked = new[]
            {
                "ss", "sr", "se", "sd", "ssx", " ss ",
                "w axis0.requested_state 8", "w axis0.requested_state 3", "w axis1.requested_state  8",
                "c 0 1.5", "p 0 10", "q 0 1 1", "v 0 1", "t 0 1", "Y",
                "sys.reboot!", "sys.reboot?", "sys.format!", "axis.anticogcal!",
            };
            foreach (var line in blocked)
                Check.True(OdriveCommandGuard.IsBlockedWhileStopped(line), "should be blocked: " + line);

            var allowed = new[]
            {
                "", "  ", "sc", "r axis0.current_state", "w axis0.requested_state 1", "w axis1.requested_state 1",
                "w axis0.motor.config.current_lim 10", "sys.save!", "sys.build?", "axis.range=900", "axis.zeroenc!",
                "fx.master=255", "i", "f 0", "d",
            };
            foreach (var line in allowed)
                Check.True(!OdriveCommandGuard.IsBlockedWhileStopped(line), "should be allowed: " + line);
        }
    }

    public static class ShutdownTests
    {
        public static void Test_shutdown_when_idle_closes_everything()
        {
            var rig = new Rig();
            rig.Service.Shutdown(2000);

            Check.Equal(SerialConnectionState.ShutDown, rig.Service.GetStatus().State, "state");
            Check.True(!rig.Board.IsOpen, "port closed");
            Check.True(rig.Board.Disposed, "transport disposed");
        }

        public static void Test_shutdown_completes_running_and_queued_commands()
        {
            var rig = new Rig();
            rig.Board.Mute = true;
            var running = rig.Send("sys.save!", ReplyMode.OffbEcho, 5000, echo: "[sys.save!");
            var queued  = rig.Send("r vbus_voltage");
            Check.True(Wait.Until(() => rig.Board.Lines.Length == 1), "the command should go out");

            var watch = Stopwatch.StartNew();
            rig.Service.Shutdown(2000);

            Check.True(Task.WaitAll(new Task[] { running, queued }, 500), "every command completes");
            Check.Equal(SerialOutcome.ShuttingDown, running.Result.Outcome, "running command");
            Check.Equal(SerialOutcome.ShuttingDown, queued.Result.Outcome, "queued command");
            Check.True(watch.ElapsedMilliseconds < 500, "without waiting for the 5 s timeout");
        }

        public static void Test_shutdown_is_bounded_when_a_write_hangs()
        {
            var rig = new Rig(o => o.WriteTimeoutMs = 60000);
            rig.Board.StallWrites();
            var running = rig.Send("r axis0.error");
            var queued  = rig.Send("r vbus_voltage");
            Thread.Sleep(30);

            var watch = Stopwatch.StartNew();
            rig.Service.Shutdown(200);

            Check.True(watch.ElapsedMilliseconds < 800, "returned after " + watch.ElapsedMilliseconds + " ms");
            Check.True(queued.Wait(100), "the queued command completes");
            Check.Equal(SerialOutcome.ShuttingDown, queued.Result.Outcome, "queued command");
            rig.Board.ReleaseWrites();
            running.Wait(1000);
        }

        public static void Test_shutdown_is_bounded_when_close_hangs()
        {
            var rig = new Rig();
            rig.Board.CloseGate.Reset();

            var watch = Stopwatch.StartNew();
            rig.Service.Shutdown(200);

            Check.True(watch.ElapsedMilliseconds < 800, "returned after " + watch.ElapsedMilliseconds + " ms");
            rig.Board.CloseGate.Set();
        }

        public static void Test_shutdown_is_bounded_when_the_port_scan_hangs()
        {
            var rig = new Rig();
            rig.Board.LocatorGate.Reset();
            Thread.Sleep(40);

            var watch = Stopwatch.StartNew();
            rig.Service.Shutdown(1000);

            Check.True(watch.ElapsedMilliseconds < 800, "returned after " + watch.ElapsedMilliseconds + " ms");
            Check.Equal(SerialConnectionState.ShutDown, rig.Service.GetStatus().State, "state");
            rig.Board.LocatorGate.Set();
        }

        public static void Test_shutdown_during_a_stop_finishes_the_ticket()
        {
            var rig = new Rig();
            rig.Board.StallWrites();
            var ticket = rig.Service.RequestStop();

            rig.Service.Shutdown(1500);

            Check.True(ticket.WaitForResult(1000), "the ticket gets a result");
            Check.True(ticket.State == StopState.NotDelivered || ticket.State == StopState.Unconfirmed, "result: " + ticket.State);
        }

        public static void Test_after_shutdown_everything_is_refused()
        {
            var rig = new Rig();
            rig.Service.Shutdown(2000);

            Check.Equal(SerialOutcome.ShuttingDown, rig.Send("r vbus_voltage").Result.Outcome, "command");
            var ticket = rig.Service.RequestStop();
            Check.True(ticket.IsFinished, "stop answers at once");
            Check.Equal(StopState.NotDelivered, ticket.State, "stop");

            rig.Service.Shutdown(100);   // a second call is harmless
            Check.Equal(SerialConnectionState.ShutDown, rig.Service.GetStatus().State, "state");
        }

        public static void Test_stop_before_start_is_not_delivered()
        {
            var board = new FakeBoard();
            var service = new OdriveSerialService(board, board);
            var ticket = service.RequestStop();
            Check.Equal(StopState.NotDelivered, ticket.State, "stop");
            service.Shutdown(100);
            Check.Equal(SerialConnectionState.ShutDown, service.GetStatus().State, "state");
        }
    }
}

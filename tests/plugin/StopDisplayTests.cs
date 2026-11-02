namespace OdriveWheel.SimHubPlugin.Tests
{
    /// <summary>A confirmation belongs to its moment. Green must mean: the axis is IDLE now.</summary>
    public static class StopDisplayTests
    {
        private const int Idle = 1;
        private const int ClosedLoop = 8;

        private static StopTicket Finished(StopState state, string detail)
        {
            var ticket = new StopTicket();
            ticket.MarkWritten();
            ticket.Finish(state, detail);
            return ticket;
        }

        private static StopTicket Confirmed() => Finished(StopState.Confirmed, "HID reports IDLE");

        public static void Test_green_only_while_the_axis_is_idle_now()
        {
            var text = OdriveStatusText.Stop(Confirmed(), true, Idle);
            Check.Equal(StatusSeverity.Ok, text.Severity, "latched, HID says IDLE");
            Check.True(text.Text.Contains("IDLE confirmed"), text.Text);
        }

        public static void Test_axis_armed_again_contradicts_the_confirmation()
        {
            var text = OdriveStatusText.Stop(Confirmed(), true, ClosedLoop);
            Check.Equal(StatusSeverity.Danger, text.Severity, "HID says closed loop");
            Check.True(!text.Text.Contains("IDLE confirmed"), text.Text);
            Check.True(text.Text.Contains(OdriveErrorDecoder.DecodeAxisState(ClosedLoop)), "names the state: " + text.Text);
        }

        public static void Test_missing_feedback_is_not_a_confirmation()
        {
            var text = OdriveStatusText.Stop(Confirmed(), true, null);
            Check.Equal(StatusSeverity.Warning, text.Severity, "no HID reports");
            Check.True(!text.Text.Contains("IDLE confirmed"), text.Text);
            Check.True(text.Text.Contains("no current"), "says so: " + text.Text);

            var undefined = OdriveStatusText.Stop(Confirmed(), true, 0);
            Check.Equal(StatusSeverity.Warning, undefined.Severity, "state 0 means firmware without the field");
        }

        public static void Test_cleared_stop_is_history_and_not_green()
        {
            foreach (var hid in new int?[] { Idle, ClosedLoop, null })
            {
                var text = OdriveStatusText.Stop(Confirmed(), false, hid);
                Check.Equal(StatusSeverity.Neutral, text.Severity, "cleared, HID " + hid);
                Check.True(!text.Text.Contains("IDLE confirmed"), text.Text);
                Check.True(text.Text.Contains("Last"), "reads as history: " + text.Text);
            }

            var failed = OdriveStatusText.Stop(Finished(StopState.NotDelivered, "the board was not found"), false, ClosedLoop);
            Check.Equal(StatusSeverity.Neutral, failed.Severity, "a cleared failure does not stay an alarm");
        }

        public static void Test_new_connection_voids_the_confirmation()
        {
            foreach (var hid in new int?[] { Idle, null })
            {
                var text = OdriveStatusText.Stop(Confirmed(), true, hid, sameConnection: false);
                Check.Equal(StatusSeverity.Warning, text.Severity, "reconnected, HID " + hid);
                Check.True(!text.Text.Contains("IDLE confirmed"), text.Text);
                Check.True(text.Text.Contains("reconnected"), "says why: " + text.Text);
            }

            var armed = OdriveStatusText.Stop(Confirmed(), true, ClosedLoop, sameConnection: false);
            Check.Equal(StatusSeverity.Danger, armed.Severity, "reconnected and armed");

            var unconfirmed = OdriveStatusText.Stop(Finished(StopState.Unconfirmed, "nothing reported IDLE"), true, Idle, sameConnection: false);
            Check.True(unconfirmed.Severity != StatusSeverity.Ok, "the HID shortcut for an unconfirmed stop is void too");
        }

        public static void Test_status_snapshot_notices_a_reconnect()
        {
            using (var rig = new Rig())
            {
                rig.HidState = Idle;
                Check.True(rig.Service.RequestStop().WaitForResult(3000), "the stop should finish");
                Check.Equal(StatusSeverity.Ok, OdriveStatusText.Stop(rig.Service.GetStatus(), Idle).Severity, "same connection");

                int generation = rig.Service.GetStatus().ConnectGeneration;
                rig.Board.FailNextWrite = true;
                rig.Send("r vbus_voltage").Wait(1000);
                Check.True(Wait.Until(() => rig.Service.GetStatus().ConnectGeneration == generation + 1), "should reconnect");

                var text = OdriveStatusText.Stop(rig.Service.GetStatus(), Idle);
                Check.Equal(StatusSeverity.Warning, text.Severity, "after the reconnect");
                Check.True(!text.Text.Contains("IDLE confirmed"), text.Text);
            }
        }

        public static void Test_status_snapshot_notices_a_cleared_stop()
        {
            using (var rig = new Rig())
            {
                rig.HidState = Idle;
                Check.True(rig.Service.RequestStop().WaitForResult(3000), "the stop should finish");
                rig.Service.ClearStopLatch();

                var text = OdriveStatusText.Stop(rig.Service.GetStatus(), ClosedLoop);
                Check.Equal(StatusSeverity.Neutral, text.Severity, "cleared and armed again is normal operation");
            }
        }

        public static void Test_stop_opened_by_the_stop_itself_counts_as_the_same_connection()
        {
            using (var rig = new Rig(connect: false, prepare: b => b.FailOpen = true))
            {
                Check.True(Wait.Until(() => rig.Service.GetStatus().PortName != null), "the board should be found");
                rig.Board.FailOpen = false;
                rig.HidState = Idle;
                Check.True(rig.Service.RequestStop().WaitForResult(3000), "the stop should finish");
                Check.Equal(StopState.Confirmed, rig.Service.GetStatus().LastStop.State, "stop result");
                Check.Equal(StatusSeverity.Ok, OdriveStatusText.Stop(rig.Service.GetStatus(), Idle).Severity, "no reconnect since");
            }
        }

        public static void Test_running_stop_is_shown_regardless_of_history()
        {
            var running = new StopTicket();
            Check.Equal(StatusSeverity.Warning, OdriveStatusText.Stop(running, true, ClosedLoop).Severity, "requested");
            running.MarkWritten();
            Check.Equal(StatusSeverity.Warning, OdriveStatusText.Stop(running, true, ClosedLoop).Severity, "written");
        }
    }
}

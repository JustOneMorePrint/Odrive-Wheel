using System;
using System.IO;
using System.Linq;
using System.Threading;

namespace OdriveWheel.SimHubPlugin.Tests
{
    public static class ParameterTests
    {
        public static void Test_odrive_read()
        {
            using (var rig = new Rig())
            {
                var read = rig.Parameters.Read("vbus_voltage", FieldProtocol.Odrive);
                Check.True(read.IsOk, "status");
                Check.Equal("48.000", read.Value, "value");
                Check.Lines(new[] { "r vbus_voltage" }, rig.Board.Lines, "line");
            }
        }

        public static void Test_offb_read_write_exec()
        {
            using (var rig = new Rig())
            {
                rig.Board.SetValue("axis.range", "900");
                Check.Equal("900", rig.Parameters.Read("axis.range", FieldProtocol.Openffb).Value, "read");
                Check.Equal("1080", rig.Parameters.Write("axis.range", "1080", FieldProtocol.Openffb).Value, "write echo");
                Check.Equal("1080", rig.Board.Value("axis.range"), "value on the board");
                rig.Board.SetValue("sys.save", "x");
                Check.Equal("OK", rig.Parameters.ExecOffb("sys.save").Value, "exec");
                Check.Lines(new[] { "axis.range?", "axis.range=1080", "sys.save!" }, rig.Board.Lines, "lines");
            }
        }

        public static void Test_reads_build_id_with_spaces()
        {
            using (var rig = new Rig())
            {
                rig.Board.SetValue("sys.build", "4c693e1ce086 branch=pkg/0-dev-setup");
                Check.Equal("4c693e1ce086 branch=pkg/0-dev-setup",
                            rig.Parameters.Read("sys.build", FieldProtocol.Openffb).Value, "value");
            }
        }

        public static void Test_offb_err_and_not_found_are_rejected_not_values()
        {
            using (var rig = new Rig())
            {
                var unknown = rig.Parameters.Read("nosuch.value", FieldProtocol.Openffb);
                Check.Equal(ParameterStatus.Rejected, unknown.Status, "NOT_FOUND read");
                Check.Equal("NOT_FOUND", unknown.Detail, "detail");
                Check.Equal(null, unknown.Value, "no value");

                rig.Board.ReplyOverride = line => line == "axis.expo=7" ? "[axis.expo=7|ERR]" : null;
                var refused = rig.Parameters.Write("axis.expo", "7", FieldProtocol.Openffb);
                Check.Equal(ParameterStatus.Rejected, refused.Status, "ERR write");
            }
        }

        public static void Test_odrive_error_texts_are_rejected_not_values()
        {
            using (var rig = new Rig())
            {
                var unknown = rig.Parameters.Read("axis0.nosuch", FieldProtocol.Odrive);
                Check.Equal(ParameterStatus.Rejected, unknown.Status, "invalid property");

                // The firmware's read buffer is 10 bytes, bigger values come back like this.
                rig.Board.ReplyOverride = line => line == "r axis0.motor.error" ? "not implemented" : null;
                Check.Equal(ParameterStatus.Rejected, rig.Parameters.Read("axis0.motor.error", FieldProtocol.Odrive).Status,
                            "not implemented");
            }
        }

        public static void Test_odrive_write_silence_is_ok_and_a_reply_is_a_rejection()
        {
            using (var rig = new Rig())
            {
                var ok = rig.Parameters.Write("axis0.motor.config.current_lim", "12", FieldProtocol.Odrive);
                Check.True(ok.IsOk, "silence");
                Check.Equal("12", rig.Board.Value("axis0.motor.config.current_lim"), "value on the board");

                var refused = rig.Parameters.Write("axis0.nosuch", "1", FieldProtocol.Odrive);
                Check.Equal(ParameterStatus.Rejected, refused.Status, "reply");
                Check.Equal("invalid property", refused.Detail, "detail");
            }
        }

        // The old OdriveSerialCli reported success here, because silence and failure looked alike.
        public static void Test_odrive_write_without_a_board_is_not_a_success()
        {
            using (var rig = new Rig(connect: false, prepare: b => b.Present = false))
            {
                var write = rig.Parameters.Write("axis0.motor.config.current_lim", "12", FieldProtocol.Odrive);
                Check.Equal(ParameterStatus.Unavailable, write.Status, "status");
            }
        }

        public static void Test_odrive_write_with_a_failing_link_is_not_a_success()
        {
            using (var rig = new Rig())
            {
                rig.Board.FailNextWrite = true;
                var write = rig.Parameters.Write("axis0.motor.config.current_lim", "12", FieldProtocol.Odrive);
                Check.Equal(ParameterStatus.Unavailable, write.Status, "status");
            }
        }

        public static void Test_invalid_input_never_reaches_the_board()
        {
            using (var rig = new Rig())
            {
                foreach (var value in new[] { "1;sys.reboot!", "1\nss", "1 2", "", "a=b", "1*5", "?", "1!" , null })
                {
                    Check.Equal(ParameterStatus.InvalidInput,
                                rig.Parameters.Write("axis.range", value, FieldProtocol.Openffb).Status, "OpenFFBoard value: " + value);
                    Check.Equal(ParameterStatus.InvalidInput,
                                rig.Parameters.Write("axis0.motor.config.current_lim", value, FieldProtocol.Odrive).Status, "ODrive value: " + value);
                }
                foreach (var path in new[] { "a;b", "a b", "a\nb", "", "a=b", ".a", "a.", null })
                {
                    Check.Equal(ParameterStatus.InvalidInput, rig.Parameters.Read(path, FieldProtocol.Odrive).Status, "read path: " + path);
                    Check.Equal(ParameterStatus.InvalidInput, rig.Parameters.ExecOffb(path).Status, "exec path: " + path);
                }
                Check.Equal(0, rig.Board.Lines.Length, "nothing was sent");
            }
        }

        public static void Test_normal_values_pass_validation()
        {
            foreach (var value in new[] { "0", "1", "-12.5", "+3", "1e-3", "1.5E+2", "True", "0.05" })
                Check.True(OdriveProtocol.IsValidValue(value), value);
        }

        public static void Test_save_counts_as_ok_once_the_line_went_out()
        {
            using (var rig = new Rig())
            {
                Check.True(rig.Parameters.SaveOdrive().IsOk, "normal");

                // The board reboots right after saving, the link may die during the wait.
                rig.Board.FailNextRead = true;
                Check.True(rig.Parameters.SaveOdrive().IsOk, "link dies afterwards");
                Check.True(Wait.Until(() => rig.Service.GetStatus().IsConnected), "reconnects");

                rig.Board.FailNextWrite = true;
                Check.True(!rig.Parameters.SaveOdrive().IsOk, "line never went out");
            }
        }

        public static void Test_async_read_for_the_ui()
        {
            using (var rig = new Rig())
            {
                var read = rig.Parameters.ReadAsync("axis0.error", FieldProtocol.Odrive, CommandPriority.Background);
                Check.Equal("0", read.Result.Value, "value");
            }
        }
    }

    public static class CatalogTests
    {
        public static void Test_embedded_catalog_loads()
        {
            var catalog = OdriveParameterCatalog.LoadEmbedded();
            Check.True(catalog.Parameters.Count >= 10, "entries: " + catalog.Parameters.Count);

            var vbus = catalog.Find("vbus_voltage");
            Check.True(vbus != null, "vbus_voltage is present");
            Check.Equal(FieldProtocol.Odrive, vbus.Protocol, "protocol");
            Check.Equal("V", vbus.Unit, "unit");
            Check.Equal(ParameterAccess.Read, vbus.Access, "access");

            foreach (var path in new[] { "axis0.current_state", "axis0.error", "axis0.motor.error", "axis0.encoder.error", "axis0.controller.error" })
                Check.Equal("status", catalog.Find(path)?.Tab, path);

            Check.Equal(255.0, catalog.Find("fx.master").Max, "max");
            Check.Equal(FieldProtocol.Openffb, catalog.Find("fx.master").Protocol, "protocol");
        }

        public static void Test_tabs_match_the_web_client()
        {
            var known = new[] { "status", "odrv", "axis0", "motor", "encoder", "controller", "ffb-wheel", "ffb-effects", "ffb-filters", "inputs" };
            foreach (var parameter in OdriveParameterCatalog.LoadEmbedded().Parameters)
                Check.True(known.Contains(parameter.Tab), parameter.Path + " is in tab " + parameter.Tab);
        }

        public static void Test_malformed_catalogs_are_refused()
        {
            string Entry(string path = "a.b", string protocol = "odrive", string type = "float", string access = "read",
                         string more = "", string description = "d")
                => "{\"path\":\"" + path + "\",\"protocol\":\"" + protocol + "\",\"type\":\"" + type + "\",\"access\":\"" + access
                 + "\",\"group\":\"g\",\"tab\":\"t\",\"description\":\"" + description + "\"" + more + "}";
            string Wrap(params string[] entries) => "{\"parameters\":[" + string.Join(",", entries) + "]}";

            Check.Equal(1, OdriveParameterCatalog.Parse(Wrap(Entry())).Parameters.Count, "valid entry");

            var bad = new[]
            {
                "not json",
                "{}",
                Wrap(Entry(), Entry()),
                Wrap(Entry(path: "a;b")),
                Wrap(Entry(protocol: "can")),
                Wrap(Entry(type: "string")),
                Wrap(Entry(access: "none")),
                Wrap(Entry(description: "")),
                Wrap(Entry(more: ",\"min\":5,\"max\":1")),
            };
            foreach (var json in bad)
            {
                bool refused = false;
                try { OdriveParameterCatalog.Parse(json); }
                catch (InvalidDataException) { refused = true; }
                Check.True(refused, "should be refused: " + json);
            }
        }
    }

    public static class StatusTextTests
    {
        private static readonly ParameterDefinition Bits = new ParameterDefinition { Type = ParameterType.Bitmask };
        private static readonly ParameterDefinition Volts = new ParameterDefinition { Type = ParameterType.Float, Unit = "V" };
        private static readonly ParameterDefinition State = new ParameterDefinition { Type = ParameterType.AxisState };

        public static void Test_failed_read_never_looks_like_no_error()
        {
            Check.Equal("0 (no error)", OdriveStatusText.ReadValue(Bits, new ParameterResult(ParameterStatus.Ok, "0")), "real zero");
            Check.Equal("12", OdriveStatusText.ReadValue(Bits, new ParameterResult(ParameterStatus.Ok, "12")), "error bits");
            Check.Equal("not readable (not implemented)",
                        OdriveStatusText.ReadValue(Bits, new ParameterResult(ParameterStatus.Rejected, detail: "not implemented")), "refused");
            Check.Equal("no reply", OdriveStatusText.ReadValue(Bits, new ParameterResult(ParameterStatus.Timeout)), "timeout");
            Check.Equal("—", OdriveStatusText.ReadValue(Bits, new ParameterResult(ParameterStatus.Unavailable)), "no connection");
        }

        public static void Test_values_get_units_and_state_names()
        {
            Check.Equal("48.000 V", OdriveStatusText.ReadValue(Volts, new ParameterResult(ParameterStatus.Ok, "48.000")), "unit");
            Check.True(OdriveStatusText.ReadValue(State, new ParameterResult(ParameterStatus.Ok, "1")).Contains("IDLE"), "state name");
        }

        public static void Test_stop_text_warns_when_it_did_not_work()
        {
            var notDelivered = new StopTicket();
            notDelivered.Finish(StopState.NotDelivered, "the board was not found");
            var text = OdriveStatusText.Stop(notDelivered, true, null);
            Check.Equal(StatusSeverity.Danger, text.Severity, "not delivered");
            Check.True(text.Text.Contains("power cut-off"), text.Text);

            var unconfirmed = new StopTicket();
            unconfirmed.MarkWritten();
            unconfirmed.Finish(StopState.Unconfirmed, "nothing reported IDLE");
            Check.Equal(StatusSeverity.Danger, OdriveStatusText.Stop(unconfirmed, true, 8).Severity, "still armed");
            Check.Equal(StatusSeverity.Ok, OdriveStatusText.Stop(unconfirmed, true, 1).Severity, "HID sees IDLE");

            var confirmed = new StopTicket();
            confirmed.MarkWritten();
            confirmed.Finish(StopState.Confirmed, "HID reports IDLE");
            Check.Equal(StatusSeverity.Ok, OdriveStatusText.Stop(confirmed, true, 1).Severity, "confirmed");
        }

        public static void Test_stop_text_warns_when_the_worker_is_stuck()
        {
            var stuck = new StopTicket();
            Check.Equal(StatusSeverity.Warning, OdriveStatusText.Stop(stuck, true, null).Severity, "right after the press");
            Thread.Sleep(520);
            var text = OdriveStatusText.Stop(stuck, true, null);
            Check.Equal(StatusSeverity.Danger, text.Severity, "overdue");
            Check.True(text.Text.Contains("power cut-off"), text.Text);
        }

        public static void Test_connection_texts()
        {
            StatusLine Line(SerialConnectionState state) => OdriveStatusText.Connection(new SerialStatus(state, "COM7", 1, null, null, false));

            Check.Equal(StatusSeverity.Ok, Line(SerialConnectionState.Connected).Severity, "connected");
            Check.True(Line(SerialConnectionState.Connected).Text.Contains("COM7"), "port name");
            Check.Equal(StatusSeverity.Warning, Line(SerialConnectionState.Busy).Severity, "busy");
            Check.True(Line(SerialConnectionState.Busy).Text.Contains("another application"), "reason");
            Check.Equal(StatusSeverity.Warning, Line(SerialConnectionState.Released).Severity, "released");
        }
    }
}

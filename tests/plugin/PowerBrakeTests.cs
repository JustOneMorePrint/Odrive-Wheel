using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OdriveWheel.SimHubPlugin.Tests
{
    using R = OdrivePowerBrakeRules;

    /// <summary>A board in a valid state with both axes idle, and a reader that has read the group.</summary>
    internal sealed class PowerBrakeRig : IDisposable
    {
        public static readonly OdriveParameterCatalog Catalog = OdriveParameterCatalog.LoadEmbedded();

        public Rig Rig { get; }
        public FakeBoard Board => Rig.Board;
        public OdriveParameterReader Reader { get; }
        public OdriveRamApplier Applier { get; }
        /// <summary>Null unless the rig stands for current firmware.</summary>
        public OdriveCapabilityDetector Detector { get; }

        /// <param name="declared">The board declares its replies, and the rig has seen that before it reads.</param>
        public PowerBrakeRig(Func<Rig, IOdriveParameters> parameters = null, bool declared = false, Action<FakeBoard> prepare = null)
        {
            Rig = new Rig(prepare: board =>
            {
                PrepareBoard(board);
                prepare?.Invoke(board);
                if (declared) board.BecomeCurrentFirmware();
            });
            if (declared)
            {
                Detector = new OdriveCapabilityDetector(Rig.Service, Rig.Parameters);
                Detector.Start();
                Check.True(Wait.Until(() => Detector.Status.State == FirmwareCapabilityState.Declared), "the firmware should be recognised");
            }
            Reader = new OdriveParameterReader(
                (p, priority, cancellation) => Rig.Parameters.ReadAsync(p.Path, p.Protocol, priority, cancellation),
                Rig.Service.GetStatus,
                Detector != null ? Detector.For : (Func<int, FirmwareCapabilities>)null);
            Applier = new OdriveRamApplier(parameters?.Invoke(Rig) ?? Rig.Parameters, Rig.Service.GetStatus, () => Rig.HidState);
            ReadAll();
            Board.ClearLines();
        }

        public static void PrepareBoard(FakeBoard board)
        {
            board.SetValue(R.BrakeResistance, "2");
            board.SetValue(R.EnableBrake, "1");
            board.SetValue(R.UnderVoltage, "10");
            board.SetValue(R.OverVoltage, "56");
            board.SetValue(R.EnableRamp, "0");
            board.SetValue(R.RampStart, "58");
            board.SetValue(R.RampEnd, "60");
            board.SetValue(R.MaxPositive, "inf");
            board.SetValue(R.MaxNegative, "-0.1");
            board.SetValue(R.MaxRegen, "0");
            board.SetValue("vbus_voltage", "48");
            foreach (var axis in new[] { "axis0", "axis1" })
            {
                board.SetValue(axis + ".current_state", "1");
                board.SetValue(axis + ".requested_state", "0");
                board.SetValue(axis + ".motor.is_armed", "0");
            }
        }

        public void ReadAll()
        {
            var group = OdrivePowerBrakeEditor.EditableParameters(Catalog).ToList();
            group.Add(Catalog.Find("vbus_voltage"));
            var result = Reader.RefreshAsync(group, CommandPriority.Batch, CancellationToken.None).Result;
            Check.Equal(null, result.AbortReason, "the group should read");
        }

        public PowerBrakeReview Review(params (string Path, string Value)[] inputs)
        {
            var status = Rig.Service.GetStatus();
            return OdrivePowerBrakeEditor.Review(Catalog, Reader.Get, status,
                                                 inputs.ToDictionary(i => i.Path, i => i.Value, StringComparer.Ordinal),
                                                 Detector?.For(status));
        }

        public RamApplyResult Apply(PowerBrakeReview review)
        {
            var running = Applier.ApplyAsync(review, CancellationToken.None);
            Check.True(running.Wait(15000), "the apply should end");
            return running.Result;
        }

        /// <summary>The writes to the group. A software stop also sends "w" lines, to the axes.</summary>
        public string[] Writes => Board.Lines.Where(l => l.StartsWith("w config.", StringComparison.Ordinal)).ToArray();

        public void Dispose()
        {
            Detector?.Stop();
            Rig.Dispose();
        }
    }

    public static class PowerBrakeRuleTests
    {
        private static Dictionary<string, double> Valid() => new Dictionary<string, double>(StringComparer.Ordinal)
        {
            [R.BrakeResistance] = 2,
            [R.EnableBrake]     = 1,
            [R.UnderVoltage]    = 10,
            [R.OverVoltage]     = 56,
            [R.EnableRamp]      = 0,
            [R.RampStart]       = 58,
            [R.RampEnd]         = 60,
            [R.MaxPositive]     = double.PositiveInfinity,
            [R.MaxNegative]     = -0.1,
            [R.MaxRegen]        = 0,
        };

        private static string[] Ids(double busVoltage = 48, params (string Path, double Value)[] changes)
        {
            var values = Valid();
            foreach (var (path, value) in changes) values[path] = value;
            return R.Check(values, busVoltage).Select(v => v.Id).ToArray();
        }

        private static void Expect(string id, params (string, double)[] changes)
            => Check.True(Ids(48, changes).Contains(id), id + " expected, got: " + string.Join(",", Ids(48, changes)));

        public static void Test_valid_values_pass_and_no_general_limit_exists()
        {
            Check.Equal(0, Ids().Length, "valid");
            Check.Equal(0, Ids(48, (R.OverVoltage, 5000), (R.BrakeResistance, 0.001), (R.MaxPositive, 100000)).Length, "large values alone are no problem");
            Check.Equal(0, Ids(48, (R.MaxNegative, double.NegativeInfinity)).Length, "the firmware documents -inf to disable");
            Check.Equal(0, Ids(48, (R.EnableBrake, 0), (R.BrakeResistance, 0), (R.MaxRegen, -1)).Length, "these only count with the brake on");
        }

        public static void Test_relations_follow_the_firmware()
        {
            Expect("brake-resistance", (R.BrakeResistance, 0));
            Expect("brake-resistance", (R.BrakeResistance, -1));
            Expect("trip-order", (R.UnderVoltage, 60));
            Expect("trip-order", (R.UnderVoltage, 56));
            Expect("bus-under", (R.UnderVoltage, 50));
            Expect("bus-over", (R.OverVoltage, 40));
            Expect("positive-current", (R.MaxPositive, -1));
            Expect("negative-current", (R.MaxNegative, 0.5));
            Expect("regen-negative", (R.MaxRegen, -1));
            Expect("regen-magnitude", (R.MaxRegen, 1), (R.MaxNegative, -0.5));
            Expect("regen-magnitude", (R.MaxRegen, 1), (R.MaxNegative, -1));
            Check.Equal(0, Ids(48, (R.MaxRegen, 1), (R.MaxNegative, -2)).Length, "greater in magnitude");
        }

        // Why a rounded reply is no basis: %f makes -0.100000 of this limit, which would seem to leave room.
        public static void Test_a_rounded_limit_can_hide_a_broken_relation()
        {
            float real = -0.09999974f;
            float regen = 0.0999999f;
            Check.Equal("-0.100000", ((double)real).ToString("F6", CultureInfo.InvariantCulture), "the reply of older firmware");
            Check.Equal(0, Ids(48, (R.MaxNegative, -0.1), (R.MaxRegen, regen)).Length, "judged by the reply");
            Expect("regen-magnitude", (R.MaxNegative, real), (R.MaxRegen, regen));
        }

        public static void Test_the_ramp_counts_only_while_enabled()
        {
            Check.Equal(0, Ids(48, (R.RampStart, 60), (R.RampEnd, 50)).Length, "not enabled, so the values do not matter");

            Check.Equal(0, Ids(48, (R.EnableRamp, 1)).Length, "valid ramp");
            Expect("ramp-order", (R.EnableRamp, 1), (R.RampStart, 60), (R.RampEnd, 60));
            Expect("ramp-order", (R.EnableRamp, 1), (R.RampStart, 61), (R.RampEnd, 60));
            Expect("ramp-bus", (R.EnableRamp, 1), (R.RampStart, 48));
            Expect("ramp-bus", (R.EnableRamp, 1), (R.RampStart, 40));
            Expect("ramp-brake", (R.EnableRamp, 1), (R.EnableBrake, 0));
            Expect("ramp-resistance", (R.EnableRamp, 1), (R.BrakeResistance, 0));
        }

        public static void Test_notes_name_what_the_firmware_says_about_a_change()
        {
            Check.True(R.Notes(Valid(), new[] { R.EnableBrake }).Single().Contains("after a reboot"), "reboot note");
            Check.True(R.Notes(Valid(), new[] { R.RampStart }).Single().Contains("experimental"), "experimental note");
            Check.Equal(0, R.Notes(Valid(), new[] { R.MaxRegen }).Count, "no note");

            var ramp = Valid();
            ramp[R.EnableRamp] = 1;
            ramp[R.OverVoltage] = 59;
            Check.True(R.Notes(ramp, new string[0]).Single().Contains("before the ramp reaches full duty"), "trip before the end of the ramp");
        }
    }

    public static class PowerBrakeValueTests
    {
        private static ParameterDefinition Float => PowerBrakeRig.Catalog.Find(R.BrakeResistance);
        private static ParameterDefinition Boolean => PowerBrakeRig.Catalog.Find(R.EnableBrake);

        private static string Command(string text)
        {
            Check.True(OdriveConfigValue.TryParseInput(Float, text, out var command, out _, out var error), text + ": " + error);
            return command;
        }

        private static void Verdict(ReadbackVerdict expected, string command, string reply, ParameterDefinition parameter = null)
            => Check.Equal(expected, OdriveConfigValue.CompareReadback(parameter ?? Float, command, reply, out _), command + " read as " + reply);

        public static void Test_only_the_config_fields_of_the_group_are_editable()
        {
            var editable = PowerBrakeRig.Catalog.Parameters.Where(p => p.Editable).Select(p => p.Path).OrderBy(p => p, StringComparer.Ordinal).ToArray();
            var expected = new[] { R.BrakeResistance, R.EnableBrake, R.UnderVoltage, R.OverVoltage, R.EnableRamp, R.RampStart, R.RampEnd,
                                   R.MaxPositive, R.MaxNegative, R.MaxRegen }.OrderBy(p => p, StringComparer.Ordinal).ToArray();
            Check.Lines(expected, editable, "editable");
            Check.True(!PowerBrakeRig.Catalog.Find("sys.vbusdiv").Editable, "the voltage divider is a calibration of the measurement");
            Check.True(!PowerBrakeRig.Catalog.Find("config.enable_uart_a").Editable, "communication stays read-only");
        }

        public static void Test_a_float_input_must_be_finite_and_fit_a_float32()
        {
            foreach (var bad in new[] { "", "  ", "abc", "NaN", "Infinity", "-Infinity", "1e39", "-1e39", "1e-60", "2,5", "2;ss" })
                Check.True(!OdriveConfigValue.TryParseInput(Float, bad, out _, out _, out _), "refused: " + bad);
            Check.Equal("2.5", Command(" 2.5 "), "trimmed");
            Check.Equal("0", Command("-0"), "no negative zero");
        }

        public static void Test_catalog_limits_count()
        {
            var limited = PowerBrakeRig.Catalog.Find("axis0.motor.config.current_lim");
            Check.True(limited.Min == 0.0, "this one has a minimum");
            Check.True(!OdriveConfigValue.TryParseInput(limited, "-1", out _, out _, out _), "below the minimum");
            Check.True(OdriveConfigValue.TryParseInput(limited, "1", out _, out _, out _), "above the minimum");
        }

        public static void Test_a_boolean_input_is_0_or_1()
        {
            foreach (var bad in new[] { "", "2", "true", "True", "1.0" })
                Check.True(!OdriveConfigValue.TryParseInput(Boolean, bad, out _, out _, out _), "refused: " + bad);
            Check.True(OdriveConfigValue.TryParseInput(Boolean, "1", out var command, out double value, out _), "1");
            Check.Equal("1", command, "command");
            Check.Equal(1.0, value, "value");
        }

        public static void Test_the_command_is_what_the_firmware_will_store()
        {
            Check.Equal("2.1", Command("2.1"), "short decimals stay readable");
            Check.Equal((float)16777217.0, float.Parse(Command("16777217"), CultureInfo.InvariantCulture), "rounded to the float32 it will be");
            Check.True(Command("16777217") != "16777217", "and shown as such, not as typed");
            Check.True(OdriveProtocol.IsValidValue(Command("0.00001")), "scientific notation is a valid value: " + Command("0.00001"));
        }

        public static void Test_commands_are_invariant_in_any_culture()
        {
            var saved = Thread.CurrentThread.CurrentCulture;
            Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
            try
            {
                Check.Equal("2.5", Command("2.5"), "a point on the line");
                Check.True(!OdriveConfigValue.TryParseInput(Float, "2,5", out _, out _, out _), "a comma is no decimal separator");
                Check.Equal(ReadbackVerdict.Verified, OdriveConfigValue.CompareReadback(Float, "2.5", "2.5", out _), "reply parsed the same way");
                Check.True(!(2.5.ToString(CultureInfo.CurrentCulture) == "2.5"), "the culture really is another one");
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = saved;
            }
        }

        public static void Test_readback_compares_float32_values()
        {
            Verdict(ReadbackVerdict.Verified, "2.1", "2.0999999");        // %.9g of the float32 nearest to 2.1
            Verdict(ReadbackVerdict.Verified, "2.1", "2.1");
            Verdict(ReadbackVerdict.Verified, "0.1", "0.100000001");
            Verdict(ReadbackVerdict.Verified, "25", "25");
            Verdict(ReadbackVerdict.Verified, "1E-05", "9.99999975e-06");
            Verdict(ReadbackVerdict.Verified, "16777216", "16777216");
            Verdict(ReadbackVerdict.Verified, "123456792", "1.23456792e+08");
            Verdict(ReadbackVerdict.Differs, "2.1", "2.5");
            Verdict(ReadbackVerdict.Differs, "2.1", "2.10000038");        // the next float32 but one
            Verdict(ReadbackVerdict.Differs, "2.1", "nan");
            Verdict(ReadbackVerdict.Differs, "2.1", "inf");
            Verdict(ReadbackVerdict.NotVerified, "2.1", "hu");
        }

        // %f with six decimals in a 10-byte buffer: nothing here can identify a float32.
        public static void Test_a_reply_older_firmware_can_produce_verifies_nothing()
        {
            Verdict(ReadbackVerdict.NotVerified, "2.1", "2.100000");
            Verdict(ReadbackVerdict.NotVerified, "-2.1", "-2.100000");
            Verdict(ReadbackVerdict.NotVerified, "25", "25.000000");
            Verdict(ReadbackVerdict.NotVerified, "100", "100.00000");
            Verdict(ReadbackVerdict.NotVerified, "1E-07", "0.000000");    // rounded away
            Verdict(ReadbackVerdict.NotVerified, "123456792", "123456792");   // nine digits without a point
            Verdict(ReadbackVerdict.NotVerified, "1E+09", "100000000");   // cut: the real value is ten times that

            // Even with that uncertainty, this cannot be the written value.
            Verdict(ReadbackVerdict.Differs, "2.1", "2.200000");
            Verdict(ReadbackVerdict.Differs, "25", "26.000000");
        }

        public static void Test_booleans_compare_exactly()
        {
            Verdict(ReadbackVerdict.Verified, "1", "1", Boolean);
            Verdict(ReadbackVerdict.Differs, "1", "0", Boolean);
            Verdict(ReadbackVerdict.NotVerified, "1", "1.000000", Boolean);
        }
    }

    public static class PowerBrakeReviewTests
    {
        public static void Test_a_review_sends_nothing()
        {
            using (var rig = new PowerBrakeRig())
            {
                var review = rig.Review((R.BrakeResistance, "2.5"));
                Check.True(review.CanApply, string.Join(" ", review.Errors));
                Check.Equal(0, rig.Board.WireLines.Length, "no line");
            }
        }

        public static void Test_summary_names_old_and_new_values()
        {
            using (var rig = new PowerBrakeRig())
            {
                var text = OdriveStatusText.RamReview(rig.Review((R.BrakeResistance, "2.5"), (R.EnableBrake, "1")));
                Check.True(text.Contains(R.BrakeResistance + ": 2 ohm → 2.5 ohm"), text);
                Check.True(text.Contains(R.EnableBrake + ": already has this value"), text);
                Check.True(text.Contains("Nothing is saved to the board"), text);
                Check.True(text.Contains("does not lock out other command sources"), text);
            }
        }

        public static void Test_values_that_were_not_read_fail_the_review()
        {
            using (var rig = new Rig(prepare: PowerBrakeRig.PrepareBoard))
            {
                var reader = new OdriveParameterReader(
                    (p, priority, cancellation) => rig.Parameters.ReadAsync(p.Path, p.Protocol, priority, cancellation), rig.Service.GetStatus);
                var review = OdrivePowerBrakeEditor.Review(PowerBrakeRig.Catalog, reader.Get, rig.Service.GetStatus(),
                                                           new Dictionary<string, string> { [R.BrakeResistance] = "2.5" });
                Check.True(!review.CanApply, "nothing was read");
                Check.True(review.Errors.Any(e => e.Contains("press Refresh")), string.Join(" ", review.Errors));
                Check.True(review.Errors.Any(e => e.Contains("bus voltage")), "no bus voltage either");
            }
        }

        public static void Test_one_missing_value_of_the_group_fails_the_review()
        {
            using (var rig = new PowerBrakeRig())
            {
                rig.Reader.Invalidate(PowerBrakeRig.Catalog.Find(R.RampEnd));
                var review = rig.Review((R.BrakeResistance, "2.5"));
                Check.True(!review.CanApply, "a relation cannot be judged without it");
                Check.True(review.Errors.Single().Contains(R.RampEnd), string.Join(" ", review.Errors));
            }
        }

        public static void Test_unclear_values_fail_the_review()
        {
            // The last ones parse, and older firmware could have rounded or cut them. Current firmware can reply
            // "56.123450" as well, they cannot be told apart.
            foreach (var unclear in new[] { "123456789", "nan", "hu", "56.000000", "123.45678", "56.123450" })
            {
                using (var rig = new PowerBrakeRig())
                {
                    rig.Board.SetValue(R.OverVoltage, unclear);
                    rig.ReadAll();
                    var review = rig.Review((R.BrakeResistance, "2.5"));
                    Check.True(!review.CanApply, unclear + " is no starting point");
                    Check.True(review.Errors.Single().Contains(R.OverVoltage), string.Join(" ", review.Errors));
                }
            }
        }

        public static void Test_a_rounded_limit_is_no_basis_for_a_relation()
        {
            using (var rig = new PowerBrakeRig())
            {
                rig.Board.SetValue(R.MaxNegative, "-0.100000");
                rig.ReadAll();

                var review = rig.Review((R.MaxRegen, "0.0999999"));
                Check.True(!review.CanApply, "judged by the reply it would fit, on the board it does not");
                Check.True(review.Errors.Single().Contains(R.MaxNegative) && review.Errors.Single().Contains("unclear"), string.Join(" ", review.Errors));
            }
        }

        public static void Test_a_rounded_value_is_no_basis_for_the_write_order()
        {
            using (var rig = new PowerBrakeRig())
            {
                rig.Board.SetValue(R.EnableRamp, "1");
                rig.Board.SetValue(R.RampEnd, "60.000000");
                rig.ReadAll();

                // Which of the two may go first depends on what the end really is.
                var review = rig.Review((R.RampStart, "59.999998"), (R.RampEnd, "70"));
                Check.True(!review.CanApply, "no order from a rounded value");
                Check.True(review.Errors.Single().Contains(R.RampEnd) && review.Errors.Single().Contains("unclear"), string.Join(" ", review.Errors));
            }
        }

        public static void Test_a_rounded_value_is_not_taken_as_already_set()
        {
            using (var rig = new PowerBrakeRig())
            {
                rig.Board.SetValue(R.BrakeResistance, "2.500000");
                rig.ReadAll();

                var review = rig.Review((R.BrakeResistance, "2.5"));
                Check.Equal(0, review.Unchanged.Count, "the board may hold 2.4999995");
                Check.True(!review.CanApply, "unclear");
            }
        }

        // What %.9g makes of them. The same comparison as above, with values that are known.
        public static void Test_exact_replies_are_a_basis_for_relations()
        {
            using (var rig = new PowerBrakeRig())
            {
                string limit = "-0.100000001";
                rig.Board.ReplyOverride = line => line == "r " + R.MaxNegative ? limit : null;
                rig.ReadAll();
                Check.True(rig.Review((R.MaxRegen, "0.0999999")).CanApply, "this one fits");

                limit = "-0.0999997407";
                rig.ReadAll();
                var review = rig.Review((R.MaxRegen, "0.0999999"));
                Check.True(!review.CanApply, "this one does not");
                Check.True(review.Errors.Single().Contains("greater in magnitude"), string.Join(" ", review.Errors));
            }
        }

        public static void Test_outdated_values_fail_the_review()
        {
            using (var rig = new PowerBrakeRig())
            {
                rig.Board.Present = false;
                Check.True(Wait.Until(() => !rig.Rig.Service.GetStatus().IsConnected), "disconnected");
                rig.Board.Present = true;
                rig.Rig.WaitConnected();

                Check.True(!rig.Review((R.BrakeResistance, "2.5")).CanApply, "read before the board went away");
            }
        }

        public static void Test_other_parameters_are_refused()
        {
            using (var rig = new PowerBrakeRig())
            {
                foreach (var path in new[] { "sys.vbusdiv", "config.enable_uart_a", "axis0.motor.config.current_lim", "axis0.requested_state" })
                {
                    var review = rig.Review((path, "1"));
                    Check.True(!review.CanApply, path + " must not pass");
                    Check.True(review.Errors.Any(e => e.Contains(path + " cannot be edited")), string.Join(" ", review.Errors));
                }
            }
        }

        public static void Test_invalid_and_inconsistent_inputs_fail_the_review()
        {
            using (var rig = new PowerBrakeRig())
            {
                Check.True(!rig.Review().CanApply, "nothing selected");
                Check.True(!rig.Review((R.BrakeResistance, "")).CanApply, "selected without a value");
                Check.True(!rig.Review((R.BrakeResistance, "abc")).CanApply, "no number");
                Check.True(!rig.Review((R.EnableBrake, "2")).CanApply, "no boolean");
                Check.True(!rig.Review((R.BrakeResistance, "0")).CanApply, "brake on, no resistance");
                Check.True(!rig.Review((R.UnderVoltage, "60")).CanApply, "under above over");
                Check.True(!rig.Review((R.OverVoltage, "40")).CanApply, "below the bus voltage");
                Check.True(!rig.Review((R.EnableRamp, "1"), (R.RampStart, "40")).CanApply, "ramp starts below the bus voltage");
                Check.True(rig.Review((R.BrakeResistance, "0"), (R.EnableBrake, "0")).CanApply, "both together are fine");

                var nothing = rig.Review((R.BrakeResistance, "2"));
                Check.True(!nothing.CanApply, "the value it already has");
                Check.True(nothing.Errors.Single().Contains("nothing to apply"), nothing.Errors.Single());
            }
        }

        public static void Test_a_latched_stop_fails_the_review()
        {
            using (var rig = new PowerBrakeRig())
            {
                Check.True(rig.Rig.Service.RequestStop().WaitForResult(3000), "stop");
                Check.True(rig.Review((R.BrakeResistance, "2.5")).Errors.Any(e => e.Contains("Clear stop")), "says what to do");
            }
        }

        public static void Test_order_avoids_invalid_steps()
        {
            using (var rig = new PowerBrakeRig())
            {
                var enable = rig.Review((R.EnableBrake, "0"), (R.BrakeResistance, "0"));
                Check.Equal(R.EnableBrake, enable.Changes[0].Parameter.Path, "brake off before the resistance goes to 0");

                var regen = rig.Review((R.MaxRegen, "2"), (R.MaxNegative, "-5"));
                Check.Equal(R.MaxNegative, regen.Changes[0].Parameter.Path, "room for the regen current first");

                rig.Board.SetValue(R.EnableRamp, "1");
                rig.ReadAll();
                var ramp = rig.Review((R.RampStart, "70"), (R.RampEnd, "80"));
                Check.Equal(R.RampEnd, ramp.Changes[0].Parameter.Path, "the end moves up before the start");
                Check.True(rig.Review((R.RampStart, "49"), (R.RampEnd, "50")).Changes[0].Parameter.Path == R.RampStart, "and the start moves down before the end");
            }
        }
    }

    public static class PowerBrakeApplyTests
    {
        /// <summary>Acts on the board right around a write, at the exact point the service cannot be asked to.</summary>
        internal sealed class WriteHook : IOdriveParameters
        {
            private readonly Rig _rig;
            public Action<string> Before = _ => { };
            public Action<string, ParameterResult> After = (_, __) => { };
            public Action<string> BeforeRead = _ => { };
            public Action<string> AfterRead = _ => { };

            public WriteHook(Rig rig) => _rig = rig;

            public async Task<ParameterResult> WriteAsync(string path, string value, FieldProtocol protocol, CommandPriority priority = CommandPriority.Interactive, CancellationToken cancellation = default, SerialOperationContext context = null)
            {
                Before(path);
                var result = await _rig.Parameters.WriteAsync(path, value, protocol, priority, cancellation, context).ConfigureAwait(false);
                After(path, result);
                return result;
            }

            public async Task<ParameterResult> ReadAsync(string path, FieldProtocol protocol, CommandPriority priority = CommandPriority.Interactive, CancellationToken cancellation = default, SerialOperationContext context = null)
            {
                BeforeRead(path);
                var result = await _rig.Parameters.ReadAsync(path, protocol, priority, cancellation, context).ConfigureAwait(false);
                AfterRead(path);
                return result;
            }

            public Task<ParameterResult> ExecOffbAsync(string path, CommandPriority priority = CommandPriority.Interactive, CancellationToken cancellation = default)
                => throw new InvalidOperationException("the applier must not execute anything");
            public Task<ParameterResult> SaveOdriveAsync() => throw new InvalidOperationException("the applier must not save");
            public ParameterResult Read(string path, FieldProtocol protocol, CommandPriority priority = CommandPriority.Interactive, CancellationToken cancellation = default)
                => throw new InvalidOperationException("the applier must not block");
            public ParameterResult Write(string path, string value, FieldProtocol protocol, CommandPriority priority = CommandPriority.Interactive, CancellationToken cancellation = default)
                => throw new InvalidOperationException("the applier must not block");
            public ParameterResult ExecOffb(string path, CommandPriority priority = CommandPriority.Interactive, CancellationToken cancellation = default)
                => throw new InvalidOperationException("the applier must not execute anything");
            public ParameterResult SaveOdrive() => throw new InvalidOperationException("the applier must not save");
        }

        private static (string, string) Two => (R.BrakeResistance, "2.5");
        private static (string, string) Three => (R.MaxRegen, "0.05");

        private static void CheckNothingWritten(PowerBrakeRig rig, RamApplyResult result, string contains)
        {
            Check.True(result.AbortReason != null && result.AbortReason.Contains(contains), "reason: " + result.AbortReason);
            Check.Equal(0, rig.Writes.Length, "no write");
            Check.True(result.Writes.All(w => w.Outcome == RamWriteOutcome.NotSent), "all not sent");
        }

        internal static void Stop(PowerBrakeRig rig)
            => Check.True(rig.Rig.Service.RequestStop().WaitForResult(3000), "the stop should finish");

        internal static void StopAndClear(PowerBrakeRig rig)
        {
            Stop(rig);
            rig.Rig.Service.ClearStopLatch();
        }

        // Releases the port and takes it back, until the service really is on a newer connection.
        internal static void Reconnect(PowerBrakeRig rig)
        {
            var service = rig.Rig.Service;
            int before = service.GetStatus().ConnectGeneration;
            service.ReleasePort();
            Check.True(Wait.Until(() => service.GetStatus().State == SerialConnectionState.Released), "released");
            service.AcquirePort();
            Check.True(Wait.Until(() => service.GetStatus().IsConnected && service.GetStatus().ConnectGeneration > before), "a newer connection");
            rig.Board.ClearLines();
        }

        // Runs once, around the first read of this path after a write: its readback.
        internal static void AtReadback(WriteHook hook, string path, Action interfere, bool afterTheReply)
        {
            bool written = false;
            hook.After = (_, __) => written = true;
            Action<string> once = read =>
            {
                if (!written || read != path) return;
                written = false;
                interfere();
            };
            if (afterTheReply) hook.AfterRead = once;
            else hook.BeforeRead = once;
        }

        public static void Test_selected_values_are_written_and_read_back()
        {
            using (var rig = new PowerBrakeRig())
            {
                var result = rig.Apply(rig.Review(Two, Three));

                Check.True(result.AllVerified, OdriveStatusText.RamApply(result));
                Check.Lines(new[] { "w " + R.BrakeResistance + " 2.5", "w " + R.MaxRegen + " 0.05" }, rig.Writes, "exactly these writes");
                Check.Equal("2.5", rig.Board.Value(R.BrakeResistance), "on the board");
                Check.Equal("56", rig.Board.Value(R.OverVoltage), "the rest is untouched");
                Check.Equal(1, rig.Board.CountLine("w " + R.BrakeResistance + " 2.5"), "written once");

                var text = OdriveStatusText.RamApply(result);
                Check.True(text.Contains("verified by readback"), text);
                Check.True(text.Contains("nothing was saved to the board"), text);
            }
        }

        public static void Test_only_reads_and_the_selected_writes_go_out()
        {
            using (var rig = new PowerBrakeRig())
            {
                rig.Apply(rig.Review(Two));
                foreach (var line in rig.Board.WireLines)
                {
                    bool allowed = line == "w " + R.BrakeResistance + " 2.5" || line.StartsWith("r ", StringComparison.Ordinal) || line.StartsWith("f ", StringComparison.Ordinal);
                    Check.True(allowed, "unexpected line: " + line);
                }
                Check.Equal(0, rig.Board.CountLine("ss"), "no save");
                Check.Equal("1", rig.Board.Value("axis0.current_state"), "no state change");
            }
        }

        public static void Test_the_axes_are_checked_right_before_each_write()
        {
            using (var rig = new PowerBrakeRig())
            {
                rig.Apply(rig.Review(Two, Three));
                var lines = rig.Board.Lines;
                foreach (var write in lines.Where(l => l.StartsWith("w ", StringComparison.Ordinal)))
                {
                    var before = lines.Take(Array.IndexOf(lines, write)).Reverse().Take(6).ToArray();
                    foreach (var axis in new[] { "axis0", "axis1" })
                        foreach (var property in new[] { ".current_state", ".motor.is_armed", ".requested_state" })
                            Check.True(before.Contains("r " + axis + property), axis + property + " is read right before " + write);
                }
            }
        }

        public static void Test_active_or_unclear_axes_block_every_write()
        {
            var cases = new (string Path, string Value, string Reason)[]
            {
                ("axis0.current_state", "8", "axis0.current_state is 8"),
                ("axis1.current_state", "3", "axis1.current_state is 3"),
                ("axis0.current_state", "0", "axis0.current_state is 0"),
                ("axis0.motor.is_armed", "1", "axis0.motor.is_armed is 1"),
                ("axis1.motor.is_armed", "1", "axis1.motor.is_armed is 1"),
                ("axis0.requested_state", "8", "axis0.requested_state is 8"),
                ("axis1.requested_state", "1", "axis1.requested_state is 1"),
                ("axis0.current_state", "hu", "axis0.current_state is hu"),
            };
            foreach (var (path, value, reason) in cases)
            {
                using (var rig = new PowerBrakeRig())
                {
                    var review = rig.Review(Two);
                    rig.Board.SetValue(path, value);
                    CheckNothingWritten(rig, rig.Apply(review), reason);
                }
            }
        }

        public static void Test_missing_state_replies_block_every_write()
        {
            foreach (var path in new[] { "axis0.current_state", "axis1.motor.is_armed", "axis1.requested_state" })
            {
                using (var rig = new PowerBrakeRig())
                {
                    var review = rig.Review(Two);
                    rig.Board.ReplyOverride = line => line == "r " + path ? "invalid property" : null;
                    CheckNothingWritten(rig, rig.Apply(review), path + " could not be read");
                }
            }
        }

        public static void Test_a_silent_board_gets_no_write()
        {
            using (var rig = new PowerBrakeRig())
            {
                var review = rig.Review(Two);
                rig.Board.MuteOdriveChannel = true;
                CheckNothingWritten(rig, rig.Apply(review), "could not be read again");
            }
        }

        public static void Test_hid_contradicting_the_serial_replies_blocks_every_write()
        {
            using (var rig = new PowerBrakeRig())
            {
                var review = rig.Review(Two);
                rig.Rig.HidState = 8;
                CheckNothingWritten(rig, rig.Apply(review), "HID reports 8");
            }
            using (var rig = new PowerBrakeRig())
            {
                rig.Rig.HidState = 0;   // a board without the field says nothing
                Check.True(rig.Apply(rig.Review(Two)).AllVerified, "state 0 is no information");
            }
            using (var rig = new PowerBrakeRig())
            {
                rig.Rig.HidState = 1;
                Check.True(rig.Apply(rig.Review(Two)).AllVerified, "HID says IDLE too");
            }
        }

        public static void Test_a_stop_after_the_review_blocks_every_write()
        {
            using (var rig = new PowerBrakeRig())
            {
                var review = rig.Review(Two);
                Check.True(rig.Rig.Service.RequestStop().WaitForResult(3000), "stop");
                rig.Board.ClearLines();
                CheckNothingWritten(rig, rig.Apply(review), "software stop");

                rig.Rig.Service.ClearStopLatch();
                CheckNothingWritten(rig, rig.Apply(review), "software stop");
            }
        }

        public static void Test_a_new_connection_after_the_review_blocks_every_write()
        {
            using (var rig = new PowerBrakeRig())
            {
                var review = rig.Review(Two);
                rig.Board.Present = false;
                Check.True(Wait.Until(() => !rig.Rig.Service.GetStatus().IsConnected), "disconnected");
                CheckNothingWritten(rig, rig.Apply(review), "searching");

                rig.Board.Present = true;
                rig.Rig.WaitConnected();
                rig.Board.ClearLines();
                CheckNothingWritten(rig, rig.Apply(review), "connection changed");
            }
        }

        public static void Test_a_released_port_blocks_every_write()
        {
            using (var rig = new PowerBrakeRig())
            {
                var review = rig.Review(Two);
                rig.Rig.Service.ReleasePort();
                Check.True(Wait.Until(() => rig.Rig.Service.GetStatus().State == SerialConnectionState.Released), "released");
                CheckNothingWritten(rig, rig.Apply(review), "released");
            }
        }

        public static void Test_a_value_changed_since_the_review_blocks_every_write()
        {
            using (var rig = new PowerBrakeRig())
            {
                var review = rig.Review(Two);
                rig.Board.SetValue(R.OverVoltage, "50");    // not even one of the selected
                CheckNothingWritten(rig, rig.Apply(review), R.OverVoltage + " changed since the review");
            }
        }

        public static void Test_a_bus_voltage_outside_the_new_limits_blocks_every_write()
        {
            using (var rig = new PowerBrakeRig())
            {
                var review = rig.Review((R.OverVoltage, "50"));
                rig.Board.SetValue("vbus_voltage", "51");
                CheckNothingWritten(rig, rig.Apply(review), "above dc_bus_overvoltage_trip_level");
            }
        }

        public static void Test_a_change_that_is_not_editable_is_refused_even_if_handed_over()
        {
            using (var rig = new PowerBrakeRig())
            {
                var valid = rig.Review(Two);
                var forged = new PowerBrakeReview(new string[0],
                    new[] { new PowerBrakeChange(PowerBrakeRig.Catalog.Find("sys.vbusdiv"), "19", "11", 11) },
                    new string[0], new string[0], valid.ReviewedReplies, valid.Group, valid.ConnectGeneration, valid.LastStop);

                CheckNothingWritten(rig, rig.Apply(forged), "cannot be edited");
                Check.Equal(0, rig.Board.Lines.Length, "nothing at all");
            }
        }

        public static void Test_a_stop_between_two_writes_ends_it_and_names_what_went_out()
        {
            WriteHook hook = null;
            using (var rig = new PowerBrakeRig(r => hook = new WriteHook(r)))
            {
                hook.After = (path, result) => rig.Rig.Service.RequestStop().WaitForResult(3000);
                var result2 = rig.Apply(rig.Review(Two, Three));

                Check.Equal("software stop", result2.AbortReason, "reason");
                Check.Lines(new[] { "w " + R.BrakeResistance + " 2.5" }, rig.Writes, "the second never went out");
                Check.Equal(RamWriteOutcome.NotVerified, result2.Writes[0].Outcome, "sent, and no readback after the stop");
                Check.Equal(RamWriteOutcome.NotSent, result2.Writes[1].Outcome, "not sent");

                var text = OdriveStatusText.RamApply(result2);
                Check.True(text.Contains("Already transferred, and still in place: " + R.BrakeResistance), text);
                Check.True(text.Contains(R.MaxRegen + " = 0.05 Amps: not sent"), text);
            }
        }

        // Past the last check of the applier, before the line is queued: only the serial path can still say no.
        public static void Test_a_stop_finished_just_before_the_write_keeps_the_line_back()
        {
            foreach (var latched in new[] { true, false })
            {
                WriteHook hook = null;
                using (var rig = new PowerBrakeRig(r => hook = new WriteHook(r)))
                {
                    hook.Before = path =>
                    {
                        if (latched) Stop(rig);
                        else StopAndClear(rig);
                    };
                    var result = rig.Apply(rig.Review(Two, Three));

                    CheckNothingWritten(rig, result, "software stop");
                    Check.True(!OdriveStatusText.RamApply(result).Contains("Already transferred"), OdriveStatusText.RamApply(result));
                }
            }
        }

        public static void Test_a_new_connection_just_before_the_write_reaches_nothing()
        {
            WriteHook hook = null;
            using (var rig = new PowerBrakeRig(r => hook = new WriteHook(r)))
            {
                hook.Before = path => Reconnect(rig);
                var result = rig.Apply(rig.Review(Two, Three));

                CheckNothingWritten(rig, result, "connection changed");
                Check.Equal(0, rig.Board.WireLines.Length, "nothing reached the new connection, not even a sync line");
                Check.Equal("2", rig.Board.Value(R.BrakeResistance), "unchanged");
            }
        }

        // The board holds the written value, so the reply on the new connection would look like a success.
        public static void Test_a_new_connection_before_the_readback_confirms_nothing()
        {
            WriteHook hook = null;
            using (var rig = new PowerBrakeRig(r => hook = new WriteHook(r)))
            {
                var review = rig.Review(Two);
                AtReadback(hook, R.BrakeResistance, () => Reconnect(rig), afterTheReply: false);
                var result = rig.Apply(review);

                Check.Equal("2.5", rig.Board.Value(R.BrakeResistance), "a read would return the expected value");
                Check.Equal(RamWriteOutcome.NotVerified, result.Writes[0].Outcome, "not for the old operation");
                Check.True(!result.AllVerified, "no success");
                Check.True(result.AbortReason != null && result.AbortReason.Contains("connection changed"), "reason: " + result.AbortReason);
                Check.Equal(0, rig.Board.WireLines.Length, "the read never reached the new connection");
                Check.True(OdriveStatusText.RamApply(result).Contains("Already transferred"), OdriveStatusText.RamApply(result));
            }
        }

        public static void Test_a_stop_or_new_connection_around_the_readback_confirms_nothing()
        {
            var interferences = new (string Name, Action<PowerBrakeRig> Act, string Reason)[]
            {
                ("stop", Stop, "software stop"),
                ("cleared stop", StopAndClear, "software stop"),
                ("new connection", Reconnect, "connection changed"),
            };
            foreach (var afterTheReply in new[] { false, true })
            {
                foreach (var (name, act, reason) in interferences)
                {
                    var what = name + (afterTheReply ? " after the reply" : " before the read");

                    // The last field, where nothing comes afterwards that could still end it.
                    WriteHook hook = null;
                    using (var rig = new PowerBrakeRig(r => hook = new WriteHook(r)))
                    {
                        var review = rig.Review(Two);
                        AtReadback(hook, R.BrakeResistance, () => act(rig), afterTheReply);
                        var result = rig.Apply(review);

                        Check.Equal(RamWriteOutcome.NotVerified, result.Writes[0].Outcome, what);
                        Check.True(!result.AllVerified, what + ": no success");
                        Check.True(result.AbortReason != null && result.AbortReason.Contains(reason), what + ", reason: " + result.AbortReason);
                    }

                    // And not the last one, where the next write must stay back as well.
                    using (var rig = new PowerBrakeRig(r => hook = new WriteHook(r)))
                    {
                        var review = rig.Review(Two, Three);
                        AtReadback(hook, R.BrakeResistance, () => act(rig), afterTheReply);
                        var result = rig.Apply(review);

                        Check.Equal(RamWriteOutcome.NotVerified, result.Writes[0].Outcome, what);
                        Check.Equal(RamWriteOutcome.NotSent, result.Writes[1].Outcome, what + ": the next one");
                        Check.True(rig.Board.Value(R.MaxRegen) != "0.05", what + ": the next value stays out");
                    }
                }
            }
        }

        public static void Test_a_port_release_between_two_writes_ends_it()
        {
            WriteHook hook = null;
            using (var rig = new PowerBrakeRig(r => hook = new WriteHook(r)))
            {
                hook.After = (path, result) =>
                {
                    rig.Rig.Service.ReleasePort();
                    Check.True(Wait.Until(() => rig.Rig.Service.GetStatus().State == SerialConnectionState.Released), "released");
                };
                var result2 = rig.Apply(rig.Review(Two, Three));

                Check.True(result2.AbortReason.Contains("released"), result2.AbortReason);
                Check.Equal(1, rig.Writes.Length, "one write");
                Check.Equal(RamWriteOutcome.NotSent, result2.Writes[1].Outcome, "not sent");
            }
        }

        public static void Test_a_connection_lost_after_a_write_leaves_it_unconfirmed()
        {
            using (var rig = new PowerBrakeRig())
            {
                var review = rig.Review(Two, Three);
                rig.Board.ReplyOverride = line =>
                {
                    if (line.StartsWith("w " + R.BrakeResistance, StringComparison.Ordinal)) rig.Board.FailNextRead = true;
                    return null;
                };
                var result = rig.Apply(review);

                Check.True(result.AbortReason != null, "ended");
                Check.Equal(RamWriteOutcome.NotVerified, result.Writes[0].Outcome, "the line went out, nobody knows more");
                Check.Equal(RamWriteOutcome.NotSent, result.Writes[1].Outcome, "not sent");
                Check.Equal(1, rig.Writes.Length, "no retry");
                Check.True(OdriveStatusText.RamApply(result).Contains("Already transferred"), OdriveStatusText.RamApply(result));

                rig.Rig.WaitConnected();
                Thread.Sleep(100);
                Check.Equal(1, rig.Writes.Length, "nothing is made up for after reconnecting");
            }
        }

        public static void Test_a_write_that_never_left_counts_as_not_sent()
        {
            WriteHook hook = null;
            using (var rig = new PowerBrakeRig(r => hook = new WriteHook(r)))
            {
                hook.Before = path => rig.Board.FailNextWrite = true;
                var result = rig.Apply(rig.Review(Two, Three));

                Check.True(result.AbortReason != null, "ended");
                Check.Equal(RamWriteOutcome.NotSent, result.Writes[0].Outcome, "never left");
                Check.True(!OdriveStatusText.RamApply(result).Contains("Already transferred"), OdriveStatusText.RamApply(result));
            }
        }

        public static void Test_a_refused_write_ends_it_without_a_retry()
        {
            using (var rig = new PowerBrakeRig())
            {
                var review = rig.Review(Two, Three);
                rig.Board.ReplyOverride = line => line.StartsWith("w " + R.BrakeResistance, StringComparison.Ordinal) ? "not implemented" : null;
                var result = rig.Apply(review);

                Check.Equal(RamWriteOutcome.RefusedByFirmware, result.Writes[0].Outcome, "refused");
                Check.Equal("not implemented", result.Writes[0].Detail, "detail");
                Check.Equal(RamWriteOutcome.NotSent, result.Writes[1].Outcome, "not sent");
                Check.Equal(1, rig.Writes.Length, "no retry");
            }
        }

        public static void Test_a_failed_readback_leaves_it_unverified_and_ends_it()
        {
            WriteHook hook = null;
            using (var rig = new PowerBrakeRig(r => hook = new WriteHook(r)))
            {
                hook.After = (path, result) => rig.Board.MuteOdriveChannel = true;
                var result2 = rig.Apply(rig.Review(Two, Three));

                Check.Equal(RamWriteOutcome.NotVerified, result2.Writes[0].Outcome, "not verified");
                Check.True(result2.Writes[0].Detail.Contains("readback failed"), result2.Writes[0].Detail);
                Check.Equal(RamWriteOutcome.NotSent, result2.Writes[1].Outcome, "not sent");
                Check.Equal(1, rig.Writes.Length, "no retry");
            }
        }

        public static void Test_a_different_readback_ends_it_without_writing_back()
        {
            using (var rig = new PowerBrakeRig())
            {
                var review = rig.Review(Two, Three);
                bool written = false;
                rig.Board.ReplyOverride = line =>
                {
                    if (line.StartsWith("w " + R.BrakeResistance, StringComparison.Ordinal)) written = true;
                    return written && line == "r " + R.BrakeResistance ? "2.75" : null;
                };
                var result = rig.Apply(review);

                Check.Equal(RamWriteOutcome.ReadbackDiffers, result.Writes[0].Outcome, "differs");
                Check.True(result.Writes[0].Detail.Contains("2.75"), result.Writes[0].Detail);
                Check.Equal(RamWriteOutcome.NotSent, result.Writes[1].Outcome, "not sent");
                Check.Lines(new[] { "w " + R.BrakeResistance + " 2.5" }, rig.Writes, "no writing back");
                Check.True(!result.AllVerified, "no success");
            }
        }

        // The next step must not rest on a change that is only assumed.
        public static void Test_older_firmware_replies_are_not_verified_and_end_it()
        {
            using (var rig = new PowerBrakeRig())
            {
                var review = rig.Review(Two, Three);
                rig.Board.ReplyOverride = line =>
                    line == "r " + R.BrakeResistance && rig.Board.CountLine("w " + R.BrakeResistance + " 2.5") > 0 ? "2.500000" : null;
                var result = rig.Apply(review);

                Check.True(result.AbortReason != null && result.AbortReason.Contains(R.BrakeResistance), "reason: " + result.AbortReason);
                Check.True(!result.AllVerified, "no success");
                Check.Equal(RamWriteOutcome.NotVerified, result.Writes[0].Outcome, "not verified");
                Check.Equal(RamWriteOutcome.NotSent, result.Writes[1].Outcome, "not sent");
                Check.Lines(new[] { "w " + R.BrakeResistance + " 2.5" }, rig.Writes, "no further write and no retry");

                var text = OdriveStatusText.RamApply(result);
                Check.True(text.Contains("sent, not verified (the reply 2.500000 may be rounded or cut off by older firmware)"), text);
                Check.True(text.Contains("Already transferred, and still in place: " + R.BrakeResistance), text);
                Check.True(text.Contains(R.MaxRegen + " = 0.05 Amps: not sent"), text);
            }
        }

        public static void Test_an_unconfirmed_readback_ends_it()
        {
            foreach (var reply in new[] { "hu", "2.500000", "123456789", "invalid property" })
            {
                using (var rig = new PowerBrakeRig())
                {
                    var review = rig.Review(Two, Three);
                    rig.Board.ReplyOverride = line =>
                        line == "r " + R.BrakeResistance && rig.Board.CountLine("w " + R.BrakeResistance + " 2.5") > 0 ? reply : null;
                    var result = rig.Apply(review);

                    Check.True(result.AbortReason != null, reply + ": ended");
                    Check.Equal(RamWriteOutcome.NotVerified, result.Writes[0].Outcome, reply);
                    Check.Equal(RamWriteOutcome.NotSent, result.Writes[1].Outcome, reply + ": the next one");
                    Check.Equal(1, rig.Writes.Length, reply + ": no further write and no retry");
                }
            }
        }

        public static void Test_an_unconfirmed_boolean_readback_ends_it()
        {
            using (var rig = new PowerBrakeRig())
            {
                var review = rig.Review((R.EnableBrake, "0"), (R.BrakeResistance, "0"));
                rig.Board.ReplyOverride = line =>
                    line == "r " + R.EnableBrake && rig.Board.CountLine("w " + R.EnableBrake + " 0") > 0 ? "0.000000" : null;
                var result = rig.Apply(review);

                Check.True(result.AbortReason != null, "ended");
                Check.Equal(RamWriteOutcome.NotVerified, result.Writes[0].Outcome, "not verified");
                Check.Equal(RamWriteOutcome.NotSent, result.Writes[1].Outcome, "the resistance stays, the brake may still be on");
                Check.Equal("2", rig.Board.Value(R.BrakeResistance), "unchanged");
            }
        }

        public static void Test_an_unconfirmed_last_readback_is_named_as_well()
        {
            using (var rig = new PowerBrakeRig())
            {
                var review = rig.Review(Two);
                rig.Board.ReplyOverride = line =>
                    line == "r " + R.BrakeResistance && rig.Board.CountLine("w " + R.BrakeResistance + " 2.5") > 0 ? "hu" : null;
                var result = rig.Apply(review);

                Check.True(result.AbortReason != null, "says so");
                Check.True(!result.AllVerified, "no success");
                Check.True(OdriveStatusText.RamApply(result).Contains("sent, not verified (unexpected reply (hu))"), OdriveStatusText.RamApply(result));
            }
        }

        // -0.09999974f on the board, -0.100000 in the reply of older firmware. See the relation in the rule tests.
        private const string RoundedLimit = "-0.100000";
        private const string RegenAboveTheRealLimit = "0.0999999";

        // Reading the same text again removes no doubt, so this check cannot rely on the review having caught it.
        public static void Test_a_rounded_limit_is_no_basis_for_the_fresh_check()
        {
            using (var rig = new PowerBrakeRig())
            {
                var valid = rig.Review(Two);
                var replies = valid.ReviewedReplies.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
                replies[R.MaxNegative] = RoundedLimit;
                rig.Board.SetValue(R.MaxNegative, RoundedLimit);
                var regen = new PowerBrakeChange(PowerBrakeRig.Catalog.Find(R.MaxRegen), "0 Amps", RegenAboveTheRealLimit, 0.0999999f);
                var handedOver = new PowerBrakeReview(new string[0], new[] { regen }, new string[0], new string[0],
                                                      replies, valid.Group, valid.ConnectGeneration, valid.LastStop);

                CheckNothingWritten(rig, rig.Apply(handedOver), R.MaxNegative + " is unclear");
            }
        }

        // A measurement moves by far more than its last digit, and any reading can look like this.
        public static void Test_a_bus_voltage_in_the_doubtful_format_is_still_a_measurement()
        {
            using (var rig = new PowerBrakeRig())
            {
                rig.Board.SetValue("vbus_voltage", "48.123451");
                rig.ReadAll();
                Check.True(rig.Apply(rig.Review(Two)).AllVerified, "verified");
            }
        }

        public static void Test_real_float32_rounding_verifies()
        {
            using (var rig = new PowerBrakeRig())
            {
                var review = rig.Review((R.BrakeResistance, "2.1"));
                rig.Board.ReplyOverride = line =>
                    line == "r " + R.BrakeResistance && rig.Board.CountLine("w " + R.BrakeResistance + " 2.1") > 0 ? "2.0999999" : null;
                Check.True(rig.Apply(review).AllVerified, "what %.9g makes of the float32 nearest to 2.1");
            }
        }

        public static void Test_commands_on_the_line_are_invariant()
        {
            var saved = CultureInfo.DefaultThreadCurrentCulture;
            var savedThread = Thread.CurrentThread.CurrentCulture;
            CultureInfo.DefaultThreadCurrentCulture = new CultureInfo("de-DE");
            Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
            try
            {
                using (var rig = new PowerBrakeRig())
                {
                    Check.True(rig.Apply(rig.Review(Two)).AllVerified, "verified");
                    Check.Lines(new[] { "w " + R.BrakeResistance + " 2.5" }, rig.Writes, "a point on the line");
                }
            }
            finally
            {
                CultureInfo.DefaultThreadCurrentCulture = saved;
                Thread.CurrentThread.CurrentCulture = savedThread;
            }
        }
    }

    public static class SerialOperationContextTests
    {
        private const string Path = "config.brake_resistance";

        private static SerialOperationContext Now(Rig rig)
        {
            var status = rig.Service.GetStatus();
            return new SerialOperationContext(status.ConnectGeneration, status.LastStop);
        }

        private static void Reconnect(Rig rig)
        {
            int before = rig.Service.GetStatus().ConnectGeneration;
            rig.Board.Present = false;
            Check.True(Wait.Until(() => !rig.Service.GetStatus().IsConnected), "disconnected");
            rig.Board.Present = true;
            Check.True(Wait.Until(() => rig.Service.GetStatus().IsConnected && rig.Service.GetStatus().ConnectGeneration > before), "a newer connection");
            rig.Board.ClearLines();
        }

        private static void CheckHeldBack(Rig rig, SerialOperationContext context, ParameterStatus status, string detail)
        {
            var write = rig.Parameters.WriteAsync(Path, "2.5", FieldProtocol.Odrive, context: context).Result;
            var read = rig.Parameters.ReadAsync(Path, FieldProtocol.Odrive, context: context).Result;
            var offb = rig.Parameters.ReadAsync("sys.swver", FieldProtocol.Openffb, context: context).Result;
            foreach (var result in new[] { write, read, offb })
            {
                Check.Equal(status, result.Status, "held back");
                Check.True(!result.LineWritten, "not written");
                Check.True(result.Detail.Contains(detail), result.Detail);
            }
            Check.Equal(0, rig.Board.WireLines.Count(l => l.Contains(Path) || l.Contains("sys.swver")), "nothing of it on the wire");
        }

        public static void Test_a_bound_request_goes_out_while_its_context_holds()
        {
            using (var rig = new Rig())
            {
                var context = Now(rig);
                Check.True(rig.Parameters.WriteAsync(Path, "2.5", FieldProtocol.Odrive, context: context).Result.IsOk, "write");
                Check.Equal("2.5", rig.Parameters.ReadAsync(Path, FieldProtocol.Odrive, context: context).Result.Value, "read");
                Check.Equal("1.17.0", rig.Parameters.ReadAsync("sys.swver", FieldProtocol.Openffb, context: context).Result.Value, "the other channel");
            }
        }

        public static void Test_a_bound_request_stays_back_after_a_stop()
        {
            using (var rig = new Rig())
            {
                var context = Now(rig);
                Check.True(rig.Service.RequestStop().WaitForResult(3000), "stop");
                rig.Board.ClearLines();
                CheckHeldBack(rig, context, ParameterStatus.BlockedByStop, "software stop");

                rig.Service.ClearStopLatch();
                CheckHeldBack(rig, context, ParameterStatus.BlockedByStop, "software stop");

                // Bound after the stop, it stands again.
                Check.True(rig.Parameters.WriteAsync(Path, "2.5", FieldProtocol.Odrive, context: Now(rig)).Result.IsOk, "a new context");
            }
        }

        public static void Test_a_bound_request_stays_back_on_a_newer_connection()
        {
            using (var rig = new Rig())
            {
                var context = Now(rig);
                Reconnect(rig);
                CheckHeldBack(rig, context, ParameterStatus.Unavailable, "the connection changed");
                Check.Equal(0, rig.Board.WireLines.Length, "the new connection saw nothing, not even a sync line");

                Check.Equal(rig.Board.Value(Path), rig.Parameters.ReadAsync(Path, FieldProtocol.Odrive).Result.Value, "the stream is fine for everyone else");
            }
        }

        // What profiles and the panel use: only the existing rules apply to it.
        public static void Test_a_request_without_a_context_behaves_as_before()
        {
            using (var rig = new Rig())
            {
                Check.True(rig.Service.RequestStop().WaitForResult(3000), "stop");
                Check.True(rig.Parameters.WriteAsync(Path, "2.5", FieldProtocol.Odrive).Result.IsOk, "not blocked by the latch, this write cannot arm");
                rig.Service.ClearStopLatch();
                Reconnect(rig);
                Check.True(rig.Parameters.WriteAsync(Path, "3", FieldProtocol.Odrive).Result.IsOk, "goes out on the new connection");
                Check.Equal("3", rig.Board.Value(Path), "on the board");
            }
        }
    }

    public static class PowerBrakeWindowTests
    {
        private static void Press(WindowTests.WindowRig rig, Func<OdriveConfigWindow, System.Windows.Controls.Button> button)
            => rig.Host.Invoke(w => button(w).RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)));

        private static WindowTests.WindowRig NewRig() => new WindowTests.WindowRig(PowerBrakeRig.PrepareBoard, null);

        private static void ReadValues(WindowTests.WindowRig rig)
        {
            Press(rig, w => w.RefreshButton("odrv", "editor"));
            Check.True(Wait.Until(() => rig.On(w => w.RefreshSummary("odrv")).StartsWith("Read at", StringComparison.Ordinal), 5000), rig.On(w => w.RefreshSummary("odrv")));
            Check.True(Wait.Until(() => rig.On(w => w.DisplayedValue(R.BrakeResistance)) == "2 ohm"
                                     && rig.On(w => w.DisplayedValue("vbus_voltage")) == "48 V"), "values shown");
        }

        private static string[] Writes(WindowTests.WindowRig rig)
            => rig.Rig.Board.Lines.Where(l => l.StartsWith("w ", StringComparison.Ordinal)).ToArray();

        public static void Test_only_the_ten_fields_have_an_input()
        {
            using (var rig = NewRig())
            {
                var paths = rig.On(w => w.PowerBrakeFieldPaths.OrderBy(p => p, StringComparer.Ordinal).ToArray());
                var expected = PowerBrakeRig.Catalog.Parameters.Where(p => p.Editable).Select(p => p.Path).OrderBy(p => p, StringComparer.Ordinal).ToArray();
                Check.Lines(expected, paths, "inputs");
                Check.Equal(10, paths.Length, "ten");
                Check.True(!paths.Contains("sys.vbusdiv"), "no voltage divider");
            }
        }

        public static void Test_typing_selecting_and_reviewing_send_nothing()
        {
            using (var rig = NewRig())
            {
                ReadValues(rig);
                rig.Rig.Board.ClearLines();

                rig.Host.Invoke(w =>
                {
                    w.TypePowerBrakeValue(R.BrakeResistance, "2.5");
                    w.SelectPowerBrakeField(R.BrakeResistance, true);
                    w.TypePowerBrakeValue(R.EnableBrake, "0");
                });
                Press(rig, w => w.ReviewButton);

                Check.True(rig.On(w => w.ApplyButton.IsEnabled), rig.On(w => w.ReviewText.Text));
                Check.True(rig.On(w => w.ReviewText.Text).Contains("2 ohm → 2.5 ohm"), rig.On(w => w.ReviewText.Text));
                Check.True(!rig.On(w => w.ReviewText.Text).Contains(R.EnableBrake), "typed, but not selected");

                Thread.Sleep(200);
                Check.Equal(0, Writes(rig).Length, "no write");
                Check.Equal("2", rig.Rig.Board.Value(R.BrakeResistance), "unchanged");
            }
        }

        public static void Test_apply_needs_a_review_of_exactly_these_values()
        {
            using (var rig = NewRig())
            {
                ReadValues(rig);
                Check.True(!rig.On(w => w.ApplyButton.IsEnabled), "not without a review");

                rig.Host.Invoke(w =>
                {
                    w.TypePowerBrakeValue(R.BrakeResistance, "2.5");
                    w.SelectPowerBrakeField(R.BrakeResistance, true);
                });
                Press(rig, w => w.ReviewButton);
                Check.True(rig.On(w => w.ApplyButton.IsEnabled), "after the review");

                rig.Host.Invoke(w => w.TypePowerBrakeValue(R.BrakeResistance, "3"));
                Check.True(!rig.On(w => w.ApplyButton.IsEnabled), "another value means another review");
                Check.Equal("", rig.On(w => w.ReviewText.Text), "and the old summary is gone");

                Press(rig, w => w.ReviewButton);
                Check.True(rig.On(w => w.ApplyButton.IsEnabled), "reviewed again");
                rig.Host.Invoke(w => w.SelectPowerBrakeField(R.MaxRegen, true));
                Check.True(!rig.On(w => w.ApplyButton.IsEnabled), "the selection counts as well");

                rig.Host.Invoke(w => w.SelectPowerBrakeField(R.MaxRegen, false));
                Press(rig, w => w.ReviewButton);
                Check.True(rig.On(w => w.ApplyButton.IsEnabled), "reviewed again");
                Check.True(rig.Rig.Service.RequestStop().WaitForResult(3000), "stop");
                Check.True(Wait.Until(() => !rig.On(w => w.ApplyButton.IsEnabled)), "a stop drops the review");

                Check.Equal(0, Writes(rig).Count(l => l.Contains("config.")), "nothing was applied");
            }
        }

        public static void Test_an_invalid_review_cannot_be_applied()
        {
            using (var rig = NewRig())
            {
                ReadValues(rig);
                rig.Host.Invoke(w =>
                {
                    w.TypePowerBrakeValue(R.BrakeResistance, "0");
                    w.SelectPowerBrakeField(R.BrakeResistance, true);
                });
                Press(rig, w => w.ReviewButton);

                Check.True(!rig.On(w => w.ApplyButton.IsEnabled), "no apply");
                Check.True(rig.On(w => w.ReviewText.Text).StartsWith("Cannot be applied", StringComparison.Ordinal), rig.On(w => w.ReviewText.Text));
            }
        }

        public static void Test_apply_writes_the_selected_values_and_reads_them_again()
        {
            using (var rig = NewRig())
            {
                ReadValues(rig);
                rig.Host.Invoke(w =>
                {
                    w.TypePowerBrakeValue(R.BrakeResistance, "2.5");
                    w.SelectPowerBrakeField(R.BrakeResistance, true);
                    w.TypePowerBrakeValue(R.MaxRegen, "0.05");   // typed, not selected
                });
                Press(rig, w => w.ReviewButton);
                Press(rig, w => w.ApplyButton);

                Check.True(Wait.Until(() => rig.On(w => w.ApplyText.Text).Contains("verified by readback"), 5000), rig.On(w => w.ApplyText.Text));
                Check.Lines(new[] { "w " + R.BrakeResistance + " 2.5" }, Writes(rig), "only the selected one");
                Check.True(Wait.Until(() => rig.On(w => w.DisplayedValue(R.BrakeResistance)) == "2.5 ohm"), "the list shows the new value");
                Check.True(rig.On(w => w.ApplyText.Text).Contains("nothing was saved to the board"), rig.On(w => w.ApplyText.Text));
                Check.Equal(0, rig.Rig.Board.CountLine("ss"), "no save");
                Check.True(!rig.On(w => w.ApplyButton.IsEnabled), "a review is needed again");
            }
        }

        public static void Test_stop_button_works_during_apply()
        {
            using (var rig = NewRig())
            {
                ReadValues(rig);
                rig.Host.Invoke(w =>
                {
                    w.TypePowerBrakeValue(R.BrakeResistance, "2.5");
                    w.SelectPowerBrakeField(R.BrakeResistance, true);
                    w.TypePowerBrakeValue(R.MaxRegen, "0.05");
                    w.SelectPowerBrakeField(R.MaxRegen, true);
                    w.TypePowerBrakeValue(R.MaxPositive, "30");
                    w.SelectPowerBrakeField(R.MaxPositive, true);
                });
                Press(rig, w => w.ReviewButton);
                Check.True(rig.On(w => w.ApplyButton.IsEnabled), rig.On(w => w.ReviewText.Text));

                rig.Rig.Board.ReplyDelayMs = 20;
                Press(rig, w => w.ApplyButton);
                Check.True(rig.On(w => w.IsApplying), "applying");
                Check.True(Wait.Until(() => Writes(rig).Length >= 1, 8000), "the first write went out");

                rig.Host.Invoke(w => WindowTests.Click(w.StopButton));
                Check.True(Wait.Until(() => rig.Rig.Service.GetStatus().LastStop?.IsFinished == true), "the stop should finish");
                Check.Equal(StopState.Confirmed, rig.Rig.Service.GetStatus().LastStop.State, "stop result");

                Check.True(Wait.Until(() => !rig.On(w => w.IsApplying), 5000), "the apply should end");
                Check.True(rig.On(w => w.ApplyText.Text).Contains("software stop"), rig.On(w => w.ApplyText.Text));
                Check.True(rig.On(w => w.ApplyText.Text).Contains("not sent"), rig.On(w => w.ApplyText.Text));
                Check.True(rig.On(w => w.ApplyText.Text).Contains("Already transferred"), rig.On(w => w.ApplyText.Text));

                var configWrites = Writes(rig).Where(l => l.Contains(" config.")).ToArray();
                Check.True(configWrites.Length < 3, "writes: " + string.Join(" | ", configWrites));
                var stopAt = Array.IndexOf(rig.Rig.Board.Lines, "w axis0.requested_state 1");
                Check.Equal(0, rig.Rig.Board.Lines.Skip(stopAt).Count(l => l.Contains(" config.") && l.StartsWith("w ", StringComparison.Ordinal)), "no config write after the stop");
            }
        }

        public static void Test_port_button_works_during_apply()
        {
            using (var rig = NewRig())
            {
                ReadValues(rig);
                rig.Host.Invoke(w =>
                {
                    w.TypePowerBrakeValue(R.BrakeResistance, "2.5");
                    w.SelectPowerBrakeField(R.BrakeResistance, true);
                    w.TypePowerBrakeValue(R.MaxRegen, "0.05");
                    w.SelectPowerBrakeField(R.MaxRegen, true);
                });
                Press(rig, w => w.ReviewButton);

                rig.Rig.Board.ReplyDelayMs = 20;
                Press(rig, w => w.ApplyButton);
                Check.True(Wait.Until(() => Writes(rig).Length >= 1, 8000), "the first write went out");

                rig.Host.Invoke(w => WindowTests.Click(w.PortButton));
                Check.True(Wait.Until(() => rig.Rig.Service.GetStatus().State == SerialConnectionState.Released, 2000), "released");
                Check.True(Wait.Until(() => !rig.On(w => w.IsApplying), 5000), "the apply should end");
                Check.True(Writes(rig).Length < 2, "no further write");
            }
        }
    }
}

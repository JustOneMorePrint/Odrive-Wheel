using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;

namespace OdriveWheel.SimHubPlugin.Tests
{
    public static class PowerSupplyTests
    {
        private static PowerSupplyInput Valid() => new PowerSupplyInput
        {
            DcOutputVoltage = "48",
            ContinuousCurrent = "20",
            CurrentHeadroomPercent = "10",
        };

        private static IReadOnlyList<string> ErrorsOf(PowerSupplyInput input)
        {
            PowerSupplyValidation.Parse(input, out var errors);
            return errors;
        }

        public static void Test_empty_values_stay_unknown()
        {
            var data = PowerSupplyValidation.Parse(new PowerSupplyInput(), out var errors);

            Check.Equal(0, errors.Count, "nothing to complain about");
            Check.Equal(null, data.DcOutputVoltage, "voltage");
            Check.Equal(null, data.ContinuousCurrent, "continuous current");
            Check.Equal(null, data.CurrentHeadroomPercent, "headroom");
            Check.True(!data.ShortTimeOverloadSupported, "the overload option is off by default");
            Check.True(!new PowerSupplyData().ShortTimeOverloadSupported, "also in new settings");
            Check.Equal(null, data.UsableContinuousCurrent(), "nothing is made up");
        }

        public static void Test_usable_current_is_the_continuous_current_minus_the_headroom()
        {
            Check.Equal(18.0, new PowerSupplyData { ContinuousCurrent = 20, CurrentHeadroomPercent = 10 }.UsableContinuousCurrent(), "10 %");
            Check.Equal(20.0, new PowerSupplyData { ContinuousCurrent = 20, CurrentHeadroomPercent = 0 }.UsableContinuousCurrent(), "no headroom");
            Check.Equal(null, new PowerSupplyData { ContinuousCurrent = 20 }.UsableContinuousCurrent(), "an unknown headroom is not 0");
            Check.Equal(null, new PowerSupplyData { CurrentHeadroomPercent = 10 }.UsableContinuousCurrent(), "unknown current");
            Check.Equal(null, new PowerSupplyData { ContinuousCurrent = 20, CurrentHeadroomPercent = 100 }.UsableContinuousCurrent(), "100 % is invalid");
            Check.Equal(null, new PowerSupplyData { ContinuousCurrent = double.NaN, CurrentHeadroomPercent = 10 }.UsableContinuousCurrent(), "a stored NaN");
            Check.Equal(null, new PowerSupplyData { ContinuousCurrent = -5, CurrentHeadroomPercent = 10 }.UsableContinuousCurrent(), "a stored negative");
        }

        public static void Test_invalid_inputs_are_refused()
        {
            Check.Equal(0, ErrorsOf(Valid()).Count, "valid");

            foreach (var bad in new[] { "0", "-1", "NaN", "Infinity", "-Infinity", "abc", "48,5", "1e999" })
            {
                var voltage = Valid();
                voltage.DcOutputVoltage = bad;
                Check.True(ErrorsOf(voltage).Count > 0, "voltage " + bad);

                var current = Valid();
                current.ContinuousCurrent = bad;
                Check.True(ErrorsOf(current).Count > 0, "current " + bad);
            }

            foreach (var bad in new[] { "-0.1", "100", "100.5", "NaN", "abc" })
            {
                var headroom = Valid();
                headroom.CurrentHeadroomPercent = bad;
                Check.True(ErrorsOf(headroom).Count > 0, "headroom " + bad);
            }
            foreach (var good in new[] { "0", "99.9" })
            {
                var headroom = Valid();
                headroom.CurrentHeadroomPercent = good;
                Check.Equal(0, ErrorsOf(headroom).Count, "headroom " + good);
            }
        }

        public static void Test_short_time_values_count_only_with_the_option_on()
        {
            var off = Valid();
            off.ShortTimeCurrent = "5";     // below the continuous current
            off.MaximumDurationMs = "-1";
            var data = PowerSupplyValidation.Parse(off, out var offErrors);
            Check.Equal(0, offErrors.Count, "not in use, so no objection");
            Check.Equal(null, data.ShortTimeCurrentInUse(), "not used for calculations");
            Check.Equal(null, data.MaximumDurationInUse(), "not used for calculations");

            off.ShortTimeCurrent = "not a number";
            Check.Equal(0, ErrorsOf(off).Count, "text in a field that is not in use holds nothing up");

            var on = Valid();
            on.ShortTimeOverloadSupported = true;
            Check.Equal(0, ErrorsOf(on).Count, "unknown short-time values may stay empty");

            on.ShortTimeCurrent = "30";
            on.MaximumDurationMs = "500";
            data = PowerSupplyValidation.Parse(on, out var onErrors);
            Check.Equal(0, onErrors.Count, "valid");
            Check.Equal(30.0, data.ShortTimeCurrentInUse(), "in use");
            Check.Equal(500.0, data.MaximumDurationInUse(), "in use");
            Check.Equal(18.0, data.UsableContinuousCurrent(), "the short-time current does not change the continuous reference");

            on.ShortTimeCurrent = "20";
            Check.Equal(0, ErrorsOf(on).Count, "equal to the continuous current is allowed");
            on.ShortTimeCurrent = "19.9";
            Check.True(ErrorsOf(on).Count > 0, "below the continuous current");
            on.ShortTimeCurrent = "30";
            on.MaximumDurationMs = "0";
            Check.True(ErrorsOf(on).Count > 0, "duration must be positive");
            on.MaximumDurationMs = "500";
            on.ShortTimeCurrent = "NaN";
            Check.True(ErrorsOf(on).Count > 0, "short-time current must be finite");
        }

        public static void Test_a_comma_is_an_error_in_every_culture()
        {
            var saved = Thread.CurrentThread.CurrentCulture;
            Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
            try
            {
                var input = Valid();
                input.DcOutputVoltage = "48,5";
                Check.True(ErrorsOf(input).Count > 0, "48,5 must not become 485 or 48.5");

                input.DcOutputVoltage = "48.5";
                Check.Equal(48.5, PowerSupplyValidation.Parse(input, out _).DcOutputVoltage, "a point works");
                Check.Equal("48.5", PowerSupplyValidation.ToInputText(48.5), "and goes back with a point");
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = saved;
            }
        }

        public static void Test_settings_keep_the_data_and_older_settings_stay_valid()
        {
            var older = JsonConvert.DeserializeObject<OdrivePluginSettings>(
                "{\"ProfilesFolder\":\"C:\\\\profiles\",\"AutoLoadByGame\":true,\"SaveToFlashOnAutoLoad\":true,\"ClipFraction\":0.9}");
            Check.Equal("C:\\profiles", older.ProfilesFolder, "existing setting");
            Check.True(older.AutoLoadByGame, "existing setting");
            Check.True(older.SaveToFlashOnAutoLoad, "existing setting");
            Check.Equal(0.9, older.ClipFraction, "existing setting");
            Check.True(older.PowerSupply != null, "new block is there");
            Check.Equal(null, older.PowerSupply.DcOutputVoltage, "and empty");

            older.PowerSupply = new PowerSupplyData
            {
                DcOutputVoltage = 48.5,
                ContinuousCurrent = 20,
                CurrentHeadroomPercent = 10,
                ShortTimeOverloadSupported = true,
                ShortTimeCurrent = 30,
                MaximumDurationMs = 500,
            };
            var again = JsonConvert.DeserializeObject<OdrivePluginSettings>(JsonConvert.SerializeObject(older));

            Check.Equal("C:\\profiles", again.ProfilesFolder, "existing setting survives");
            Check.Equal(48.5, again.PowerSupply.DcOutputVoltage, "voltage");
            Check.Equal(20.0, again.PowerSupply.ContinuousCurrent, "current");
            Check.Equal(10.0, again.PowerSupply.CurrentHeadroomPercent, "headroom");
            Check.True(again.PowerSupply.ShortTimeOverloadSupported, "option");
            Check.Equal(30.0, again.PowerSupply.ShortTimeCurrent, "short-time current");
            Check.Equal(500.0, again.PowerSupply.MaximumDurationMs, "duration");
        }

        public static void Test_the_store_writes_through_the_plugin_settings()
        {
            var settings = new OdrivePluginSettings { ProfilesFolder = "keep" };
            int persisted = 0;
            var store = new PluginSettingsPowerSupplyStore(() => settings, () => persisted++);

            Check.Equal(null, store.Load().DcOutputVoltage, "empty at first");
            store.Save(new PowerSupplyData { DcOutputVoltage = 48 });

            Check.Equal(1, persisted, "saved once");
            Check.Equal(48.0, settings.PowerSupply.DcOutputVoltage, "in the settings");
            Check.Equal(48.0, store.Load().DcOutputVoltage, "loads again");
            Check.Equal("keep", settings.ProfilesFolder, "the rest stays");

            settings.PowerSupply = null;
            Check.True(store.Load() != null, "a settings file with null is no problem");
        }

        public static void Test_the_data_is_not_part_of_profiles()
        {
            var words = new[] { "psu", "supply", "headroom", "overload", "shorttime" };
            foreach (var field in OdriveProfileFields.All)
                foreach (var word in words)
                    Check.True(field.Path.IndexOf(word, StringComparison.OrdinalIgnoreCase) < 0, field.Path + " is no power supply field");

            using (var rig = new ProfileRig())
            {
                rig.Settings.PowerSupply = new PowerSupplyData { DcOutputVoltage = 48, ContinuousCurrent = 20 };
                var captured = rig.Profiles.CaptureCurrentAsProfile();
                foreach (var key in captured.Keys)
                    foreach (var word in words)
                        Check.True(key.IndexOf(word, StringComparison.OrdinalIgnoreCase) < 0, key + " in a profile");
            }
        }
    }

    public static class PowerSupplyWindowTests
    {
        private sealed class CountingStore : IPowerSupplyStore
        {
            public PowerSupplyData Data = new PowerSupplyData();
            public int Saves;
            public PowerSupplyData Load() => Data;
            public void Save(PowerSupplyData data)
            {
                Data = data;
                Interlocked.Increment(ref Saves);
            }
        }

        private static readonly HashSet<string> PolledLines = new HashSet<string>(
            OdriveParameterCatalog.LoadEmbedded().Parameters.Where(p => p.Polled)
                .Select(p => p.Protocol == FieldProtocol.Odrive ? OdriveProtocol.OdriveRead(p.Path) : OdriveProtocol.OffbRead(p.Path)));

        private static void Press(WindowTests.WindowRig rig, Func<OdriveConfigWindow, System.Windows.Controls.Button> button)
            => rig.Host.Invoke(w => button(w).RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)));

        public static void Test_entering_and_saving_sends_nothing_to_the_board()
        {
            var store = new CountingStore();
            using (var rig = new WindowTests.WindowRig(null, store))
            {
                rig.Rig.Board.ClearLines();
                rig.Host.Invoke(w =>
                {
                    w.PsuVoltageBox.Text = "48";
                    w.PsuContinuousBox.Text = "20";
                    w.PsuHeadroomBox.Text = "10";
                    w.PsuOverloadBox.IsChecked = true;
                    w.PsuShortTimeBox.Text = "30";
                    w.PsuDurationBox.Text = "500";
                });
                Check.True(rig.On(w => w.PsuUsableText.Text).Contains("18 A"), "reference value: " + rig.On(w => w.PsuUsableText.Text));
                Check.Equal(0, store.Saves, "typing does not save");

                Press(rig, w => w.PsuSaveButton);
                Check.Equal(1, store.Saves, "saved");
                Check.Equal(48.0, store.Data.DcOutputVoltage, "stored");
                Check.True(rig.On(w => w.PsuStatusText.Text).Contains("Nothing was sent to the board"), rig.On(w => w.PsuStatusText.Text));

                System.Threading.Thread.Sleep(300);
                foreach (var line in rig.Rig.Board.Lines)
                    Check.True(PolledLines.Contains(line), "only the status poll reaches the board: " + line);
            }
        }

        public static void Test_stored_values_come_back_without_a_board_command()
        {
            var store = new CountingStore
            {
                Data = new PowerSupplyData { DcOutputVoltage = 48.5, ContinuousCurrent = 20, CurrentHeadroomPercent = 25, ShortTimeCurrent = 30 },
            };
            using (var rig = new WindowTests.WindowRig(null, store))
            {
                Check.Equal("48.5", rig.On(w => w.PsuVoltageBox.Text), "voltage");
                Check.True(rig.On(w => w.PsuUsableText.Text).Contains("15 A"), "reference value");
                Check.True(!rig.On(w => w.PsuShortTimeBox.IsEnabled), "short-time fields are off while the option is off");
                Check.Equal("30", rig.On(w => w.PsuShortTimeBox.Text), "but not lost");
                Check.Equal(0, store.Saves, "opening saves nothing");
                Check.Equal(0, rig.Rig.Board.Lines.Count(l => l.StartsWith("w ", StringComparison.Ordinal)), "no write");
            }
        }

        public static void Test_invalid_values_are_not_saved()
        {
            var store = new CountingStore();
            using (var rig = new WindowTests.WindowRig(null, store))
            {
                rig.Host.Invoke(w => w.PsuHeadroomBox.Text = "100");
                Press(rig, w => w.PsuSaveButton);

                Check.Equal(0, store.Saves, "not saved");
                Check.True(rig.On(w => w.PsuStatusText.Text).StartsWith("Not saved.", StringComparison.Ordinal), rig.On(w => w.PsuStatusText.Text));
            }
        }
    }
}

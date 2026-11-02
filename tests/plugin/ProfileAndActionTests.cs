using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace OdriveWheel.SimHubPlugin.Tests
{
    /// <summary>Profile service on a started rig, the initial read already done.</summary>
    internal sealed class ProfileRig : IDisposable
    {
        public Rig Rig { get; }
        public OdrivePluginSettings Settings { get; } = new OdrivePluginSettings();
        public OdriveProfileService Profiles { get; }
        public FakeBoard Board => Rig.Board;
        public string Folder { get; }

        public ProfileRig(Action<FakeBoard> prepare = null, Func<Rig, IOdriveParameters> parameters = null)
        {
            Rig = new Rig(prepare: prepare);
            Folder = Path.Combine(Path.GetTempPath(), "odw-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Folder);
            Settings.ProfilesFolder = Folder;

            Profiles = new OdriveProfileService(Rig.Service, parameters?.Invoke(Rig) ?? Rig.Parameters, () => Settings, _ => { });
            Profiles.Start();
            var last = "r config.brake_resistance";
            Check.True(Wait.Until(() => Board.CountLine(last) > 0 && Profiles.LastRefreshUtc != DateTime.MinValue, 5000),
                       "the initial read should finish");
            Board.ClearLines();
        }

        public static OdriveProfileFile.LoadedProfile Profile(params (string path, string value)[] values)
        {
            var profile = new OdriveProfileFile.LoadedProfile { Name = "test", Kind = OdriveProfileFile.ExpectedKind };
            foreach (var (path, value) in values) profile.Values[path] = value;
            return profile;
        }

        public void Dispose()
        {
            Profiles.Stop();
            Rig.Dispose();
            try { Directory.Delete(Folder, true); } catch (IOException) { }
        }
    }

    public static class ProfileTests
    {
        public static void Test_apply_writes_all_fields_in_the_old_order_and_format()
        {
            using (var rig = new ProfileRig())
            {
                var values = OdriveProfileFields.All.Select(f => (f.Path, f.Type == FieldType.Boolean ? "True" : "7")).ToArray();
                var result = rig.Profiles.ApplyProfile(ProfileRig.Profile(values));

                Check.Equal(33, OdriveProfileFields.All.Count, "the scope of a profile");
                Check.Equal(33, result.Ok, "ok");
                Check.Equal(0, result.Fail, "failed");
                Check.True(!result.Aborted, "not aborted");

                var lines = rig.Board.Lines;
                // Literal on purpose: this is what the board received before the rework.
                Check.Lines(new[]
                {
                    "w axis0.motor.config.current_lim 7",
                    "w axis0.motor.config.current_control_bandwidth 7",
                    "w axis0.encoder.config.bandwidth 7",
                    "axis.range=7",
                    "axis.maxtorque=7",
                    "axis.fxratio=7",
                    "axis.invert=1",
                }, lines.Take(7).ToArray(), "first lines");
                Check.Equal("axis.eqroad=7", lines.Last(), "last line");

                var expected = OdriveProfileFields.All.Select(f =>
                {
                    var v = f.Type == FieldType.Boolean ? "1" : "7";
                    return f.Protocol == FieldProtocol.Odrive ? "w " + f.Path + " " + v : f.Path + "=" + v;
                }).ToArray();
                Check.Lines(expected, lines, "all lines");
            }
        }

        public static void Test_apply_updates_the_cache()
        {
            using (var rig = new ProfileRig())
            {
                rig.Profiles.ApplyProfile(ProfileRig.Profile(("axis.range", "1080"), ("axis.invert", "True")));
                Check.Equal("1080", rig.Profiles.GetRaw("axis.range"), "number");
                Check.Equal("1", rig.Profiles.GetRaw("axis.invert"), "boolean as written");
            }
        }

        public static void Test_apply_normalises_booleans()
        {
            using (var rig = new ProfileRig())
            {
                rig.Profiles.ApplyProfile(ProfileRig.Profile(("axis.invert", "True"), ("axis.ffbinvert", "False")));
                Check.Lines(new[] { "axis.invert=1", "axis.ffbinvert=0" }, rig.Board.Lines, "lines");
            }
        }

        public static void Test_apply_skips_keys_outside_the_scope()
        {
            using (var rig = new ProfileRig())
            {
                var result = rig.Profiles.ApplyProfile(ProfileRig.Profile(("axis.range", "900"), ("something.else", "1")));
                Check.Equal(1, result.Ok, "ok");
                Check.Equal(0, result.Fail, "failed");
                Check.Lines(new[] { "axis.range=900" }, rig.Board.Lines, "lines");
            }
        }

        public static void Test_apply_counts_firmware_rejections_and_keeps_going()
        {
            using (var rig = new ProfileRig())
            {
                var before = rig.Profiles.GetRaw("axis.range");
                rig.Board.ReplyOverride = line => line.StartsWith("axis.range=") ? "[" + line + "|ERR]" : null;

                var result = rig.Profiles.ApplyProfile(ProfileRig.Profile(("axis.range", "5"), ("axis.fxratio", "0.5")));

                Check.Equal(1, result.Ok, "ok");
                Check.Equal(1, result.Fail, "failed");
                Check.True(result.Errors[0].StartsWith("axis.range"), result.Errors[0]);
                Check.Equal(before, rig.Profiles.GetRaw("axis.range"), "the cache keeps the old value");
                Check.Equal("0.5", rig.Profiles.GetRaw("axis.fxratio"), "the next field is still written");
            }
        }

        public static void Test_apply_without_a_connection_writes_nothing_and_says_so()
        {
            using (var rig = new ProfileRig())
            {
                rig.Rig.Service.ReleasePort();
                Check.True(Wait.Until(() => rig.Rig.Service.GetStatus().State == SerialConnectionState.Released), "released");

                var result = rig.Profiles.ApplyProfile(ProfileRig.Profile(("axis.range", "900")));

                Check.True(result.Aborted, "aborted");
                Check.Equal(0, result.Ok, "ok");
                Check.True(result.Errors[0].Contains("could not open port"), result.Errors[0]);
                Check.Equal(0, rig.Board.Lines.Length, "nothing sent");
            }
        }

        public static void Test_failing_link_during_apply_does_not_fake_success()
        {
            using (var rig = new ProfileRig())
            {
                var before = rig.Profiles.GetRaw("axis0.motor.config.current_lim");
                rig.Board.FailNextWrite = true;

                var result = rig.Profiles.ApplyProfile(ProfileRig.Profile(("axis0.motor.config.current_lim", "3")));

                Check.Equal(0, result.Ok, "ok");
                Check.Equal(1, result.Fail, "failed");
                Check.Equal(before, rig.Profiles.GetRaw("axis0.motor.config.current_lim"), "the cache keeps the old value");
            }
        }

        public static void Test_stop_ends_a_running_profile()
        {
            using (var rig = new ProfileRig())
            {
                rig.Board.ReplyDelayMs = 15;
                var values = OdriveProfileFields.All.Select(f => (f.Path, "1")).ToArray();
                var apply = Task.Run(() => rig.Profiles.ApplyProfile(ProfileRig.Profile(values)));
                Check.True(Wait.Until(() => rig.Board.Lines.Length >= 6), "the profile should be running");

                var ticket = rig.Rig.Service.RequestStop();
                Check.True(ticket.WaitForResult(3000), "stop should finish");
                Check.True(apply.Wait(3000), "the profile should end");

                Check.True(apply.Result.Aborted, "aborted");
                Check.True(apply.Result.Ok < 33, "not all fields: " + apply.Result.Ok);

                var lines = rig.Board.Lines;
                int stopAt = Array.IndexOf(lines, "w axis0.requested_state 1");
                Check.True(stopAt > 0, "the stop went out");
                var after = lines.Skip(stopAt).Where(l => l.Contains("=") || (l.StartsWith("w ") && !l.Contains("requested_state"))).ToArray();
                Check.Equal(0, after.Length, "no profile line after the stop: " + string.Join(" | ", after));
            }
        }

        public static void Test_read_all_fills_the_cache_and_skips_failures()
        {
            using (var rig = new ProfileRig())
            {
                rig.Board.SetValue("axis.range", "1234");
                rig.Board.ReplyOverride = line =>
                    line == "axis.expo?" ? "[axis.expo?|NOT_FOUND]" :
                    line == "r config.brake_resistance" ? "invalid property" : null;
                var oldExpo = rig.Profiles.GetRaw("axis.expo");

                int ok = rig.Profiles.ReadAllOnce();

                Check.Equal(33, ok, "35 fields, 2 refused");
                Check.Equal("1234", rig.Profiles.GetRaw("axis.range"), "new value");
                Check.Equal(oldExpo, rig.Profiles.GetRaw("axis.expo"), "NOT_FOUND does not reach the cache");
            }
        }

        public static void Test_reconnect_reads_everything_again()
        {
            using (var rig = new ProfileRig())
            {
                rig.Board.SetValue("axis.range", "777");
                rig.Board.Present = false;
                Check.True(Wait.Until(() => !rig.Rig.Service.GetStatus().IsConnected), "disconnects");
                rig.Board.Present = true;

                Check.True(Wait.Until(() => rig.Profiles.GetRaw("axis.range") == "777", 5000), "read again after the reconnect");
            }
        }

        public static void Test_auto_apply_does_not_block_the_data_thread()
        {
            using (var rig = new ProfileRig())
            {
                rig.Settings.AutoLoadByGame = true;
                OdriveProfileFile.Save(Path.Combine(rig.Folder, "Assetto.json"), "Assetto",
                                       new Dictionary<string, string> { ["axis.range"] = "540", ["axis.fxratio"] = "0.5" });
                rig.Board.ReplyDelayMs = 100;

                var watch = Stopwatch.StartNew();
                rig.Profiles.MaybeAutoLoadForGame("Assetto");
                Check.True(watch.ElapsedMilliseconds < 50, "returned after " + watch.ElapsedMilliseconds + " ms");

                Check.True(rig.Profiles.PendingAutoApply.Wait(3000), "the apply should finish");
                Check.Lines(new[] { "axis.range=540", "axis.fxratio=0.5" }, rig.Board.Lines, "lines");

                rig.Board.ClearLines();
                rig.Profiles.MaybeAutoLoadForGame("Assetto");
                rig.Profiles.PendingAutoApply.Wait(1000);
                Check.Equal(0, rig.Board.Lines.Length, "the same game does not apply twice");
            }
        }

        public static void Test_auto_apply_is_off_by_default_and_needs_a_matching_file()
        {
            using (var rig = new ProfileRig())
            {
                OdriveProfileFile.Save(Path.Combine(rig.Folder, "Assetto.json"), "Assetto",
                                       new Dictionary<string, string> { ["axis.range"] = "540" });
                rig.Profiles.MaybeAutoLoadForGame("Assetto");
                rig.Profiles.PendingAutoApply.Wait(1000);
                Check.Equal(0, rig.Board.Lines.Length, "setting is off");

                rig.Settings.AutoLoadByGame = true;
                rig.Profiles.MaybeAutoLoadForGame("OtherGame");
                rig.Profiles.PendingAutoApply.Wait(1000);
                Check.Equal(0, rig.Board.Lines.Length, "no file for that game");
            }
        }

        public static void Test_game_changes_apply_in_order()
        {
            using (var rig = new ProfileRig())
            {
                rig.Settings.AutoLoadByGame = true;
                OdriveProfileFile.Save(Path.Combine(rig.Folder, "A.json"), "A", new Dictionary<string, string> { ["axis.range"] = "111" });
                OdriveProfileFile.Save(Path.Combine(rig.Folder, "B.json"), "B", new Dictionary<string, string> { ["axis.range"] = "222" });
                rig.Board.ReplyDelayMs = 30;

                rig.Profiles.MaybeAutoLoadForGame("A");
                rig.Profiles.MaybeAutoLoadForGame("B");
                Check.True(rig.Profiles.PendingAutoApply.Wait(3000), "both should finish");

                Check.Lines(new[] { "axis.range=111", "axis.range=222" }, rig.Board.Lines, "order");
                Check.Equal("222", rig.Profiles.GetRaw("axis.range"), "the last game wins");
            }
        }

        public static void Test_broken_profile_file_is_retried_at_the_next_visit()
        {
            using (var rig = new ProfileRig())
            {
                rig.Settings.AutoLoadByGame = true;
                var file = Path.Combine(rig.Folder, "Broken.json");
                File.WriteAllText(file, "{ not json");

                rig.Profiles.MaybeAutoLoadForGame("Broken");
                rig.Profiles.PendingAutoApply.Wait(1000);
                Check.Equal(0, rig.Board.Lines.Length, "nothing to apply");

                OdriveProfileFile.Save(file, "Broken", new Dictionary<string, string> { ["axis.range"] = "333" });
                rig.Profiles.MaybeAutoLoadForGame("Broken");
                rig.Profiles.PendingAutoApply.Wait(1000);
                Check.Lines(new[] { "axis.range=333" }, rig.Board.Lines, "fixed file");
            }
        }

        public static void Test_auto_apply_saves_only_after_a_clean_apply()
        {
            using (var rig = new ProfileRig())
            {
                rig.Settings.AutoLoadByGame = true;
                rig.Settings.SaveToFlashOnAutoLoad = true;
                rig.Board.SetValue("sys.save", "x");
                OdriveProfileFile.Save(Path.Combine(rig.Folder, "Good.json"), "Good", new Dictionary<string, string> { ["axis.range"] = "540" });
                OdriveProfileFile.Save(Path.Combine(rig.Folder, "Bad.json"), "Bad", new Dictionary<string, string> { ["axis.range"] = "541" });

                rig.Profiles.MaybeAutoLoadForGame("Good");
                rig.Profiles.PendingAutoApply.Wait(2000);
                Check.Lines(new[] { "axis.range=540", "sys.save!", "ss" }, rig.Board.Lines, "clean apply saves");

                rig.Board.ClearLines();
                rig.Board.ReplyOverride = line => line.StartsWith("axis.range=") ? "[" + line + "|ERR]" : null;
                rig.Profiles.MaybeAutoLoadForGame("Bad");
                rig.Profiles.PendingAutoApply.Wait(2000);
                Check.Lines(new[] { "axis.range=541" }, rig.Board.Lines, "a rejected field does not save");
            }
        }

        // The old code saved here too: a failed start left Fail at 0.
        public static void Test_auto_apply_does_not_save_without_a_connection()
        {
            using (var rig = new ProfileRig())
            {
                rig.Settings.AutoLoadByGame = true;
                rig.Settings.SaveToFlashOnAutoLoad = true;
                OdriveProfileFile.Save(Path.Combine(rig.Folder, "Game.json"), "Game", new Dictionary<string, string> { ["axis.range"] = "540" });

                rig.Rig.Service.ReleasePort();
                Check.True(Wait.Until(() => rig.Rig.Service.GetStatus().State == SerialConnectionState.Released), "released");

                rig.Profiles.MaybeAutoLoadForGame("Game");
                rig.Profiles.PendingAutoApply.Wait(1000);

                Check.Equal(0, rig.Board.Lines.Length, "nothing sent");
            }
        }

        private abstract class ForwardingParameters : IOdriveParameters
        {
            protected readonly Rig Rig;

            protected ForwardingParameters(Rig rig) => Rig = rig;

            public virtual ParameterResult Write(string path, string value, FieldProtocol protocol, CommandPriority priority = CommandPriority.Interactive, CancellationToken cancellation = default)
                => Rig.Parameters.Write(path, value, protocol, priority, cancellation);
            public ParameterResult Read(string path, FieldProtocol protocol, CommandPriority priority = CommandPriority.Interactive, CancellationToken cancellation = default)
                => Rig.Parameters.Read(path, protocol, priority, cancellation);
            public virtual ParameterResult ExecOffb(string path, CommandPriority priority = CommandPriority.Interactive, CancellationToken cancellation = default)
                => Rig.Parameters.ExecOffb(path, priority, cancellation);
            public virtual ParameterResult SaveOdrive() => Rig.Parameters.SaveOdrive();
            public Task<ParameterResult> ReadAsync(string path, FieldProtocol protocol, CommandPriority priority = CommandPriority.Interactive, CancellationToken cancellation = default)
                => Rig.Parameters.ReadAsync(path, protocol, priority, cancellation);
            public Task<ParameterResult> WriteAsync(string path, string value, FieldProtocol protocol, CommandPriority priority = CommandPriority.Interactive, CancellationToken cancellation = default)
                => Rig.Parameters.WriteAsync(path, value, protocol, priority, cancellation);
            public Task<ParameterResult> ExecOffbAsync(string path, CommandPriority priority = CommandPriority.Interactive, CancellationToken cancellation = default)
                => Rig.Parameters.ExecOffbAsync(path, priority, cancellation);
            public Task<ParameterResult> SaveOdriveAsync() => Rig.Parameters.SaveOdriveAsync();
        }

        /// <summary>Presses the stop right after a write has succeeded, between two lines of a
        /// profile. A stop during a line is the easy case, that line fails and nothing saves.</summary>
        private sealed class StopBetweenLines : ForwardingParameters
        {
            private int _writesLeft;

            public StopBetweenLines(Rig rig, int writes) : base(rig)
            {
                _writesLeft = writes;
            }

            public override ParameterResult Write(string path, string value, FieldProtocol protocol,
                                                  CommandPriority priority = CommandPriority.Interactive,
                                                  CancellationToken cancellation = default)
            {
                var result = base.Write(path, value, protocol, priority, cancellation);
                if (--_writesLeft == 0) Rig.Service.RequestStop().WaitForResult(3000);
                return result;
            }
        }

        /// <summary>Counts the ss attempts. Can press the stop right after sys.save! has answered,
        /// between the two lines of a save.</summary>
        private sealed class SaveWatch : ForwardingParameters
        {
            public int SaveOdriveCalls;
            public bool StopAfterExec;

            public SaveWatch(Rig rig) : base(rig) { }

            public override ParameterResult ExecOffb(string path, CommandPriority priority = CommandPriority.Interactive, CancellationToken cancellation = default)
            {
                var result = base.ExecOffb(path, priority, cancellation);
                if (StopAfterExec) Rig.Service.RequestStop().WaitForResult(3000);
                return result;
            }

            public override ParameterResult SaveOdrive()
            {
                Interlocked.Increment(ref SaveOdriveCalls);
                return base.SaveOdrive();
            }
        }

        public static void Test_stop_between_two_lines_of_an_auto_apply_saves_nothing()
        {
            using (var rig = new ProfileRig(parameters: r => new StopBetweenLines(r, writes: 4)))
            {
                rig.Settings.AutoLoadByGame = true;
                rig.Settings.SaveToFlashOnAutoLoad = true;
                rig.Board.SetValue("sys.save", "x");
                var values = OdriveProfileFields.All.ToDictionary(f => f.Path, f => "1");
                OdriveProfileFile.Save(Path.Combine(rig.Folder, "Game.json"), "Game", values);

                rig.Profiles.MaybeAutoLoadForGame("Game");
                Check.True(rig.Profiles.PendingAutoApply.Wait(3000), "the apply should end");

                Check.Equal(0, rig.Board.CountLine("sys.save!"), "no save after an aborted apply");
                Check.Equal(0, rig.Board.CountLine("ss"), "no reboot after an aborted apply");
                Check.Equal(0, rig.Board.CountLine("axis.maxtorque=1"), "the fifth line never went out");
            }
        }

        // The scenario behind the latch: the stop disarms, so ss would really save and
        // reboot, and startup_closed_loop_control would arm the motor again.
        public static void Test_game_change_after_a_stop_cannot_reboot_the_board()
        {
            using (var rig = new ProfileRig())
            {
                rig.Settings.AutoLoadByGame = true;
                rig.Settings.SaveToFlashOnAutoLoad = true;
                rig.Board.SetValue("sys.save", "x");
                OdriveProfileFile.Save(Path.Combine(rig.Folder, "Game.json"), "Game", new Dictionary<string, string> { ["axis.range"] = "540" });

                rig.Rig.Service.RequestStop().WaitForResult(2000);
                rig.Board.ClearLines();

                rig.Profiles.MaybeAutoLoadForGame("Game");
                rig.Profiles.PendingAutoApply.Wait(2000);

                Check.Equal(0, rig.Board.CountLine("ss"), "no ss after the stop");
            }
        }

        public static void Test_save_to_flash_sends_both_and_reports_the_latch()
        {
            using (var rig = new ProfileRig())
            {
                rig.Board.SetValue("sys.save", "x");
                Check.Equal(null, rig.Profiles.SaveToFlash(), "normal save");
                Check.Lines(new[] { "sys.save!", "ss" }, rig.Board.Lines, "lines");

                rig.Rig.Service.RequestStop().WaitForResult(2000);
                rig.Board.ClearLines();

                var error = rig.Profiles.SaveToFlash();
                Check.True(error != null && error.Contains("software stop"), "reported: " + error);
                Check.Equal(0, rig.Board.CountLine("ss"), "ss is blocked");
            }
        }

        // The old code sent ss whatever sys.save! had answered, and ss saves and reboots.
        public static void Test_save_to_flash_sends_no_ss_when_the_board_refuses_the_save()
        {
            foreach (var reply in new[] { "ERR", "NOT_FOUND", "FAIL" })
            {
                SaveWatch watch = null;
                using (var rig = new ProfileRig(parameters: r => watch = new SaveWatch(r)))
                {
                    rig.Board.ReplyOverride = line => line == "sys.save!" ? "[sys.save!|" + reply + "]" : null;

                    var error = rig.Profiles.SaveToFlash();

                    Check.True(error != null && error.StartsWith("sys.save!") && error.Contains(reply), reply + " reported: " + error);
                    Check.Equal(0, watch.SaveOdriveCalls, reply + ": ss not attempted");
                    Check.Lines(new[] { "sys.save!" }, rig.Board.Lines, reply + ": lines");
                }
            }
        }

        public static void Test_save_to_flash_sends_no_ss_after_a_timeout()
        {
            SaveWatch watch = null;
            using (var rig = new ProfileRig(parameters: r => watch = new SaveWatch(r)))
            {
                rig.Board.SetValue("sys.save", "x");
                rig.Board.MuteOffbChannel = true;

                var error = rig.Profiles.SaveToFlash();

                Check.True(error != null && error.StartsWith("sys.save!"), "reported: " + error);
                Check.Equal(0, watch.SaveOdriveCalls, "ss not attempted");
                Check.Lines(new[] { "sys.save!" }, rig.Board.Lines, "lines");
            }
        }

        public static void Test_save_to_flash_sends_no_ss_after_a_link_failure()
        {
            var failures = new Action<ProfileRig>[]
            {
                rig => rig.Board.FailNextWrite = true,
                rig => rig.Board.ReplyOverride = line =>
                {
                    if (line == "sys.save!") rig.Board.FailNextRead = true;
                    return null;
                },
                rig =>
                {
                    rig.Rig.Service.ReleasePort();
                    Check.True(Wait.Until(() => rig.Rig.Service.GetStatus().State == SerialConnectionState.Released), "released");
                },
            };
            foreach (var fail in failures)
            {
                SaveWatch watch = null;
                using (var rig = new ProfileRig(parameters: r => watch = new SaveWatch(r)))
                {
                    rig.Board.SetValue("sys.save", "x");
                    fail(rig);

                    var error = rig.Profiles.SaveToFlash();

                    Check.True(error != null && error.StartsWith("sys.save!"), "reported: " + error);
                    Check.Equal(0, watch.SaveOdriveCalls, "ss not attempted");
                    Check.Equal(0, rig.Board.CountLine("ss"), "no ss on the wire");
                }
            }
        }

        public static void Test_stop_during_the_save_prevents_ss()
        {
            SaveWatch watch = null;
            using (var rig = new ProfileRig(parameters: r => watch = new SaveWatch(r)))
            {
                rig.Board.SetValue("sys.save", "x");
                rig.Board.MuteOffbChannel = true;
                var saving = Task.Run(() => rig.Profiles.SaveToFlash());
                Check.True(Wait.Until(() => rig.Board.CountLine("sys.save!") == 1), "the save should go out");

                rig.Rig.Service.RequestStop().WaitForResult(3000);

                Check.True(saving.Wait(5000), "the save should end");
                Check.True(saving.Result != null && saving.Result.StartsWith("sys.save!") && saving.Result.Contains("software stop"), "reported: " + saving.Result);
                Check.Equal(0, watch.SaveOdriveCalls, "ss not attempted");
                Check.Equal(0, rig.Board.CountLine("ss"), "no ss on the wire");
            }
        }

        public static void Test_stop_between_the_save_and_ss_blocks_ss()
        {
            SaveWatch watch = null;
            using (var rig = new ProfileRig(parameters: r => watch = new SaveWatch(r)))
            {
                rig.Board.SetValue("sys.save", "x");
                watch.StopAfterExec = true;

                var error = rig.Profiles.SaveToFlash();

                Check.True(error != null && error.StartsWith("ss:") && error.Contains("software stop"), "reported: " + error);
                Check.Equal(0, rig.Board.CountLine("ss"), "no ss on the wire");
            }
        }

        public static void Test_auto_apply_sends_no_ss_after_a_refused_save()
        {
            using (var rig = new ProfileRig())
            {
                rig.Settings.AutoLoadByGame = true;
                rig.Settings.SaveToFlashOnAutoLoad = true;
                rig.Board.ReplyOverride = line => line == "sys.save!" ? "[sys.save!|ERR]" : null;
                OdriveProfileFile.Save(Path.Combine(rig.Folder, "Game.json"), "Game", new Dictionary<string, string> { ["axis.range"] = "540" });

                rig.Profiles.MaybeAutoLoadForGame("Game");
                Check.True(rig.Profiles.PendingAutoApply.Wait(3000), "the apply should end");

                Check.Lines(new[] { "axis.range=540", "sys.save!" }, rig.Board.Lines, "lines");
            }
        }

        public static void Test_capture_writes_booleans_like_the_web_client()
        {
            using (var rig = new ProfileRig())
            {
                rig.Profiles.PokeCache("axis.invert", "1");
                rig.Profiles.PokeCache("axis.ffbinvert", "0");
                rig.Profiles.PokeCache("axis.range", "900");

                var captured = rig.Profiles.CaptureCurrentAsProfile();

                Check.Equal("True", captured["axis.invert"], "true");
                Check.Equal("False", captured["axis.ffbinvert"], "false");
                Check.Equal("900", captured["axis.range"], "number");
                Check.True(!captured.ContainsKey("config.brake_resistance"), "extras stay out of a profile");
            }
        }

        public static void Test_profile_file_format_is_unchanged()
        {
            var folder = Path.Combine(Path.GetTempPath(), "odw-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                var file = Path.Combine(folder, "p.json");
                OdriveProfileFile.Save(file, "My profile", new Dictionary<string, string> { ["axis.range"] = "900" });

                var root = JObject.Parse(File.ReadAllText(file));
                Check.Equal("odrive-wheel-profile", root.Value<string>("_type"), "_type");
                Check.Equal(1, root.Value<int>("_version"), "_version");
                Check.Equal("My profile", root.Value<string>("name"), "name");
                Check.Equal("900", root["values"].Value<string>("axis.range"), "value");

                var loaded = OdriveProfileFile.Load(file);
                Check.True(loaded.IsBrandedProfile, "recognised");
                Check.Equal("900", loaded.Values["axis.range"], "value after loading");
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }
    }

    public static class ActionTests
    {
        private static OdriveActionLogic Actions(ProfileRig rig, List<string> log = null)
            => new OdriveActionLogic(rig.Rig.Parameters, rig.Profiles, m => log?.Add(m));

        public static void Test_int_action_uses_the_cache_clamps_and_updates_it()
        {
            using (var rig = new ProfileRig())
            {
                var actions = Actions(rig);
                rig.Profiles.PokeCache("fx.master", "250");

                actions.IncrementInt("fx.master", +25, 0, 255, "FxMasterUpCoarse");

                Check.Lines(new[] { "fx.master=255" }, rig.Board.Lines, "clamped write, no read");
                Check.Equal("255", rig.Profiles.GetRaw("fx.master"), "cache");
            }
        }

        public static void Test_int_action_at_the_limit_writes_nothing()
        {
            using (var rig = new ProfileRig())
            {
                var log = new List<string>();
                rig.Profiles.PokeCache("fx.master", "255");

                Actions(rig, log).IncrementInt("fx.master", +10, 0, 255, "FxMasterUpFine");

                Check.Equal(0, rig.Board.Lines.Length, "no write");
                Check.True(log[0].Contains("already at limit"), log[0]);
            }
        }

        public static void Test_int_action_accepts_decimals_from_the_device()
        {
            using (var rig = new ProfileRig())
            {
                rig.Profiles.PokeCache("fx.master", "100.000000");
                Actions(rig).IncrementInt("fx.master", -10, 0, 255, "FxMasterDownFine");
                Check.Lines(new[] { "fx.master=90" }, rig.Board.Lines, "write");
            }
        }

        public static void Test_float_action_formats_with_a_dot()
        {
            using (var rig = new ProfileRig())
            {
                rig.Profiles.PokeCache("axis.fxratio", "0.80");
                Actions(rig).IncrementFloat("axis.fxratio", +0.05, 0.0, 1.0, "F2", "FxRatioUpFine");
                Check.Lines(new[] { "axis.fxratio=0.85" }, rig.Board.Lines, "write");
                Check.Equal("0.85", rig.Profiles.GetRaw("axis.fxratio"), "cache");

                rig.Board.ClearLines();
                rig.Profiles.PokeCache("axis.eqroad", "11.5");
                Actions(rig).IncrementFloat("axis.eqroad", +2.0, -12.0, 12.0, "F1", "EqRoadUpCoarse");
                Check.Lines(new[] { "axis.eqroad=12.0" }, rig.Board.Lines, "clamped");
            }
        }

        public static void Test_action_reads_once_if_the_cache_is_empty()
        {
            using (var rig = new ProfileRig())
            {
                rig.Board.SetValue("axis.nocache", "10");
                Actions(rig).IncrementInt("axis.nocache", +1, 0, 255, "Test");
                Check.Lines(new[] { "axis.nocache?", "axis.nocache=11" }, rig.Board.Lines, "read, then write");
            }
        }

        public static void Test_rejected_write_keeps_the_cache()
        {
            using (var rig = new ProfileRig())
            {
                var log = new List<string>();
                rig.Profiles.PokeCache("fx.master", "100");
                rig.Board.ReplyOverride = line => line.StartsWith("fx.master=") ? "[" + line + "|ERR]" : null;

                Actions(rig, log).IncrementInt("fx.master", +10, 0, 255, "FxMasterUpFine");

                Check.Equal("100", rig.Profiles.GetRaw("fx.master"), "cache unchanged");
                Check.True(log[0].Contains("write rejected"), log[0]);
            }
        }

        public static void Test_action_without_a_value_writes_nothing()
        {
            using (var rig = new ProfileRig())
            {
                var log = new List<string>();
                rig.Rig.Service.ReleasePort();
                Check.True(Wait.Until(() => rig.Rig.Service.GetStatus().State == SerialConnectionState.Released), "released");

                Actions(rig, log).IncrementInt("axis.nocache", +1, 0, 255, "Test");

                Check.Equal(0, rig.Board.Lines.Length, "nothing sent");
                Check.True(log[0].Contains("no value known"), log[0]);
            }
        }

        public static void Test_zero_wheel_sends_the_exec()
        {
            using (var rig = new ProfileRig())
            {
                var log = new List<string>();
                rig.Board.SetValue("axis.zeroenc", "x");
                Actions(rig, log).ZeroWheelPosition();
                Check.Lines(new[] { "axis.zeroenc!" }, rig.Board.Lines, "line");
                Check.True(log[0].Contains("ZeroWheelPosition"), log[0]);
            }
        }

        public static void Test_action_gets_through_during_a_running_profile()
        {
            using (var rig = new ProfileRig())
            {
                rig.Board.ReplyDelayMs = 10;
                rig.Profiles.PokeCache("fx.master", "100");
                var values = OdriveProfileFields.All.Where(f => f.Protocol == FieldProtocol.Openffb && f.Path != "fx.master")
                                                    .Select(f => (f.Path, "1")).ToArray();
                var apply = Task.Run(() => rig.Profiles.ApplyProfile(ProfileRig.Profile(values)));
                Check.True(Wait.Until(() => rig.Board.Lines.Length >= 3), "the profile should be running");

                Actions(rig).IncrementInt("fx.master", +10, 0, 255, "FxMasterUpFine");

                var lines = rig.Board.Lines;
                Check.True(apply.Wait(3000), "the profile should finish");
                Check.True(Array.IndexOf(lines, "fx.master=110") >= 0, "the action went out while the profile ran");
                Check.True(rig.Board.Lines.Length > lines.Length, "the profile went on afterwards");
                Check.Equal(values.Length, apply.Result.Ok, "profile unaffected");
            }
        }
    }
}

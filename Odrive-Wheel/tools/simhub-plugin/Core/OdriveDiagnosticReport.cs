using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace OdriveWheel.SimHubPlugin
{
    /// <summary>
    /// A compact report of one catalog snapshot, to read or to paste into an issue. It only says what the snapshot holds,
    /// and the known limits of the firmware's sensors and replies next to the values. It names no board type, as the
    /// firmware reports none, and it never says that a sensor is connected: the firmware cannot tell.
    /// </summary>
    public static class OdriveDiagnosticReport
    {
        private const string FetPrefix   = "axis0.motor.fet_thermistor.";
        private const string MotorPrefix = "axis0.motor.motor_thermistor.";

        private static readonly string[] ErrorPaths = { "error", "axis0.error", "axis0.motor.error", "axis0.encoder.error", "axis0.controller.error" };
        private static readonly string[] AxisPaths =
        {
            "axis0.current_state", "axis0.requested_state", "axis0.motor.is_calibrated", "axis0.motor.is_armed",
            "axis0.encoder.is_ready", "axis0.encoder.index_found",
        };

        public static string FileName(DateTime utc)
            => "odrive_report_" + utc.ToString("yyyy-MM-dd'T'HH-mm-ss-fff'Z'", CultureInfo.InvariantCulture) + ".md";

        /// <param name="comparison">A comparison whose right side is this snapshot, or null.</param>
        public static string Build(CatalogSnapshot snapshot, SnapshotComparison comparison, string pluginVersion, DateTime nowUtc)
        {
            var report = new Report(snapshot);
            var text = new StringBuilder();
            text.AppendLine("# Odrive-Wheel diagnostic report");
            text.AppendLine();
            text.AppendLine("Written " + OdriveSnapshotText.Time(nowUtc) + " by the SimHub plugin " + pluginVersion
                          + ". Read-only: nothing was written to the board for it.");

            Section(text, "Capture");
            Item(text, OdriveSnapshotText.WhatItIs);
            Item(text, "Read between " + OdriveSnapshotText.Time(snapshot.StartedUtc) + " and " + OdriveSnapshotText.Time(snapshot.EndedUtc) + ". "
                     + OdriveSnapshotText.TimingNote);
            Item(text, "Completeness: " + OdriveSnapshotFile.CompletenessText(snapshot.Completeness)
                     + (snapshot.AbortReason != null ? " (" + snapshot.AbortReason + ")" : "")
                     + ", " + snapshot.ReadCount + " of " + snapshot.Expected + " catalog paths read.");
            Item(text, "Port " + (snapshot.PortName ?? "unknown") + ", connection " + snapshot.ConnectGeneration + ". Software stop latch: "
                     + (snapshot.StopLatched ? "set" : "not set")
                     + (snapshot.LastStop.HasValue ? ", last stop " + snapshot.LastStop.Value.ToString().ToLowerInvariant() : "") + ".");
            Item(text, "Not included: " + string.Join("; ", OdriveSnapshotText.NotIncluded) + ".");

            Section(text, "Firmware");
            Item(text, "Build (sys.build): " + report.Raw("sys.build"));
            Item(text, "ODrive firmware version: " + report.Version("fw_version_major", "fw_version_minor", "fw_version_revision")
                     + ". Hardware version as reported: " + report.Version("hw_version_major", "hw_version_minor", "hw_version_variant")
                     + ". It does not identify the board: the supported boards all report the same.");
            Item(text, "Serial number: " + report.Value("serial_number") + ". User config loaded: " + report.Value("user_config_loaded") + ".");
            Item(text, "Replies: " + OdriveSnapshotText.Precision(snapshot.Capabilities) + ".");

            Section(text, "Errors");
            foreach (var path in ErrorPaths) Item(text, path + ": " + report.Value(path));

            Section(text, "Axis");
            foreach (var path in AxisPaths) Item(text, path + ": " + report.Value(path));

            Section(text, "Bus voltage");
            Item(text, "vbus_voltage (measured): " + report.Value("vbus_voltage") + ". It depends on sys.vbusdiv = " + report.Value("sys.vbusdiv")
                     + ", which the plugin cannot check.");
            Item(text, "ibus: " + report.Value("ibus") + ", brake_resistor_current: " + report.Value("brake_resistor_current") + ".");
            Item(text, "Trip levels set: under " + report.Value("config.dc_bus_undervoltage_trip_level") + ", over "
                     + report.Value("config.dc_bus_overvoltage_trip_level") + ". These are settings, not a rating of the board.");

            Section(text, "Temperatures");
            Temperature(text, report, "FET thermistor (on the board)", FetPrefix, withPin: false);
            Temperature(text, report, "Motor thermistor (external)", MotorPrefix, withPin: true);
            Item(text, "Limits of these readings: the firmware cannot tell a missing sensor, without a sensor or a pull-up a plausible number can appear. "
                     + "After an invalid reading its filter returns 0, so 0 is ambiguous. A disabled sensor is still evaluated. "
                     + "Check a sensor once at a known temperature.");

            Section(text, "Values without a usable reply");
            var failed = snapshot.Entries.Where(e => !e.HasValue).ToList();
            if (failed.Count == 0) Item(text, "None.");
            foreach (var group in failed.GroupBy(e => OdriveSnapshotText.Outcome(e)))
            {
                var paths = group.Select(e => e.Path).ToList();
                Item(text, group.Key + ": " + string.Join(", ", paths.Take(12)) + (paths.Count > 12 ? " and " + (paths.Count - 12) + " more" : ""));
            }
            if (snapshot.UncertainCount > 0)
                Item(text, snapshot.UncertainCount + " values may be rounded or cut off, the metadata of an export lists them.");

            Section(text, "Differences");
            if (comparison == null)
            {
                Item(text, "No comparison with a file was made for this snapshot.");
            }
            else
            {
                Item(text, "Left: " + comparison.Left.Name + ", precision " + comparison.Left.Precision + ".");
                foreach (var note in comparison.Left.Notes) Item(text, note + ".");
                Item(text, "Right: this snapshot.");
                Differences(text, "Settings", comparison.Configuration.ToList(), v => v.IsDifference);
                Differences(text, "Status values, which change on their own", comparison.Status.ToList(),
                            v => v.Verdict == ComparisonVerdict.Differs || v.Verdict == ComparisonVerdict.Unclear);
                Item(text, "Same text without proven precision means equal as text only.");
            }
            return text.ToString();
        }

        private static void Temperature(StringBuilder text, Report report, string title, string prefix, bool withPin)
        {
            var reading = report.Entry(prefix + "temperature");
            var enabled = report.Entry(prefix + "config.enabled");
            var line = new StringBuilder(title + ": " + report.Value(prefix + "temperature") + ", enabled " + report.Value(prefix + "config.enabled"));
            if (withPin) line.Append(", gpio_pin " + report.Value(prefix + "config.gpio_pin"));
            line.Append(", limits " + report.Value(prefix + "config.temp_limit_lower") + " / " + report.Value(prefix + "config.temp_limit_upper"));

            var upper = report.Entry(prefix + "config.temp_limit_upper");
            if (upper != null && upper.HasValue && OdriveFloatText.TryParseFloat32(upper.Reply, out float limit) && !float.IsNaN(limit) && !float.IsInfinity(limit))
                line.Append(": the current limit is zero at the upper limit, the firmware stops at "
                          + ((double)limit + 5.0).ToString("0.###", CultureInfo.InvariantCulture) + " °C");
            line.Append('.');

            if (reading != null && reading.HasValue && OdriveFloatText.TryParseFloat32(reading.Reply, out float value))
            {
                if (float.IsNaN(value)) line.Append(" No valid reading yet: the firmware starts with nan.");
                else if (value == 0.0f) line.Append(" 0 is ambiguous: it is also what the firmware reports after an invalid reading.");
            }
            if (enabled != null && enabled.HasValue && enabled.Reply == "0")
                line.Append(" Disabled: not used for limiting, and the number says nothing about a sensor.");
            Item(text, line.ToString());
        }

        private static void Differences(StringBuilder text, string title, List<ComparedValue> values, Func<ComparedValue, bool> listed)
        {
            Item(text, title + ": " + OdriveSnapshotText.Counts(values) + ".");
            foreach (var value in values.Where(listed))
                text.AppendLine("  - `" + OdriveSnapshotText.Line(value) + "`");
        }

        private static void Section(StringBuilder text, string title)
        {
            text.AppendLine();
            text.AppendLine("## " + title);
            text.AppendLine();
        }

        private static void Item(StringBuilder text, string line) => text.AppendLine("- " + line);

        private sealed class Report
        {
            private readonly CatalogSnapshot _snapshot;

            public Report(CatalogSnapshot snapshot)
            {
                _snapshot = snapshot;
            }

            public SnapshotEntry Entry(string path) => _snapshot.Find(path);

            /// <summary>The value as the window shows it, decoded where the reply allows it, or why there is none.</summary>
            public string Value(string path)
            {
                var entry = Entry(path);
                if (entry == null) return "not in the catalog";
                if (!entry.HasValue) return OdriveSnapshotText.Outcome(entry);
                var shown = OdriveParameterFormat.Format(entry.Definition, entry.Reply, _snapshot.Capabilities.AsciiFull).Text;
                return entry.Certainty == ReplyCertainty.Exact ? shown : shown + " (" + OdriveSnapshotText.CertaintyName(entry.Certainty) + ")";
            }

            public string Raw(string path)
            {
                var entry = Entry(path);
                if (entry == null) return "not in the catalog";
                return entry.HasValue ? "`" + entry.Reply + "`" : OdriveSnapshotText.Outcome(entry);
            }

            public string Version(params string[] paths)
            {
                var entries = paths.Select(Entry).ToList();
                return entries.All(e => e != null && e.HasValue)
                    ? string.Join(".", entries.Select(e => e.Reply))
                    : "not read";
            }
        }
    }
}

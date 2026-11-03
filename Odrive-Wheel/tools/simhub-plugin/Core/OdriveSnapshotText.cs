using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace OdriveWheel.SimHubPlugin
{
    /// <summary>The wording of snapshots, files and comparisons, shared by the window, the metadata and the report.</summary>
    public static class OdriveSnapshotText
    {
        public const string WhatItIs =
            "Catalog snapshot: the values of the plugin's parameter catalog, read one after the other from the RAM of the board. "
          + "It is not a complete NVM image, not a calibration record and not a restore backup.";

        public const string TimingNote =
            "Each value was read at its own moment between start and end, not all at once. Nothing was written to the board, "
          + "and nothing here says that these values are stored in flash.";

        /// <summary>What the catalog, and so every snapshot, leaves out.</summary>
        public static readonly IReadOnlyList<string> NotIncluded = new[]
        {
            "FFB wheel settings other than axis.maxtorque: range, fxratio, inversion, the axis effects, the endstop, slew rate and expo",
            "FFB effect gains other than fx.master, the FFB filters and the EQ",
            "Inputs and GPIO modes (the web client's Inputs tab)",
            "Profiles, the plugin settings and the power supply data on this PC",
            "The anticogging map and anything else that is no single catalog parameter",
            "axis1",
            "The flash contents themselves: every value comes from RAM",
        };

        public static string OutcomeName(SnapshotReadOutcome outcome)
        {
            switch (outcome)
            {
                case SnapshotReadOutcome.Read:         return "read";
                case SnapshotReadOutcome.NotSupported: return "not supported";
                case SnapshotReadOutcome.Refused:      return "refused";
                case SnapshotReadOutcome.NoReply:      return "no reply";
                case SnapshotReadOutcome.Unexpected:   return "unexpected reply";
                default:                               return "not read";
            }
        }

        public static string CertaintyName(ReplyCertainty certainty)
        {
            switch (certainty)
            {
                case ReplyCertainty.Exact:        return "exact";
                case ReplyCertainty.MayBeRounded: return "may be rounded";
                default:                          return "may be cut off";
            }
        }

        /// <summary>Why an entry has no value, in words.</summary>
        public static string Outcome(SnapshotEntry entry)
        {
            switch (entry.Outcome)
            {
                case SnapshotReadOutcome.Read:         return "read";
                case SnapshotReadOutcome.NotSupported: return "not supported by this firmware (" + entry.Detail + ")";
                case SnapshotReadOutcome.Unexpected:   return "unexpected reply (" + entry.Reply + "), " + entry.Detail;
                default:                               return OutcomeName(entry.Outcome) + (entry.Detail != null && entry.Detail != OutcomeName(entry.Outcome) ? " (" + entry.Detail + ")" : "");
            }
        }

        public static string Precision(CapabilityEvidence evidence)
            => evidence.Checked
                ? Precision(evidence.State, evidence.Declaration, evidence.AsciiFull, evidence.Float32RoundTrip)
                : "not proven: there was no firmware check, floats may be rounded and long numbers cut off";

        public static string Precision(FirmwareCapabilityState state, string declaration, bool asciiFull, bool float32RoundTrip)
        {
            if (state != FirmwareCapabilityState.Declared)
                return "not proven: the firmware declared nothing, floats may be rounded and long numbers cut off";
            if (asciiFull && float32RoundTrip)
                return "exact: the firmware declared '" + declaration + "' on that connection";
            return "not proven: the firmware declared '" + declaration + "', "
                 + (asciiFull ? "whole numbers but no exact floats" : "no whole numbers");
        }

        public static string MetadataNote(LoadedSnapshotFile file)
        {
            switch (file.Metadata)
            {
                case SnapshotMetadataState.Matches: return "metadata matches";
                case SnapshotMetadataState.Missing: return "no metadata (" + file.MetadataDetail + "), so nothing is known about its precision";
                default:                            return "metadata not used: " + file.MetadataDetail;
            }
        }

        public static string Time(DateTime utc) => utc.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + " UTC";

        /// <summary>What the window shows after a capture.</summary>
        public static string Summary(CatalogSnapshot snapshot)
        {
            var text = new StringBuilder();
            switch (snapshot.Completeness)
            {
                case SnapshotCompleteness.Complete:
                    text.AppendLine("Catalog snapshot complete: " + snapshot.ReadCount + " of " + snapshot.Expected + " paths read.");
                    break;
                case SnapshotCompleteness.WithFailures:
                    text.AppendLine("Catalog snapshot ended with failed values: " + snapshot.ReadCount + " of " + snapshot.Expected + " paths read, "
                                  + (snapshot.Expected - snapshot.ReadCount) + " without a usable value.");
                    break;
                default:
                    text.AppendLine("PARTIAL catalog snapshot: " + snapshot.AbortReason + ". " + snapshot.ReadCount + " of " + snapshot.Expected
                                  + " paths read, the rest was not. This is no complete record.");
                    break;
            }
            text.AppendLine("Read from RAM between " + Time(snapshot.StartedUtc) + " and " + snapshot.EndedUtc.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)
                          + ", one value after the other, on " + (snapshot.PortName ?? "the port") + " (connection " + snapshot.ConnectGeneration + ")"
                          + (snapshot.StopLatched ? ", with the software stop latch set." : "."));
            text.AppendLine("Precision: " + Precision(snapshot.Capabilities) + "."
                          + (snapshot.UncertainCount > 0 ? " " + snapshot.UncertainCount + " values may be rounded or cut off." : ""));
            foreach (var entry in snapshot.Entries.Where(e => !e.HasValue && e.Outcome != SnapshotReadOutcome.NotRead))
                text.AppendLine("  " + entry.Path + ": " + Outcome(entry));
            text.Append("Not a complete NVM image and no calibration or restore backup. Nothing was written to the board.");
            return text.ToString();
        }

        /// <summary>The catalog's coverage, for the window.</summary>
        public static string Scope(OdriveParameterCatalog catalog)
        {
            int configuration = catalog.Parameters.Count(p => OdriveSnapshotCapture.SectionOf(p) == SnapshotSection.Configuration);
            return "A snapshot reads all " + catalog.Parameters.Count + " paths of the catalog: " + configuration + " settings of the tabs PSU / Brake, Axis, "
                 + "Motor, Encoder and Controller, sys.vbusdiv, axis.maxtorque and fx.master, and " + (catalog.Parameters.Count - configuration)
                 + " status values. Not included: " + string.Join("; ", NotIncluded) + ".";
        }

        public static string VerdictName(ComparisonVerdict verdict)
        {
            switch (verdict)
            {
                case ComparisonVerdict.Same:        return "same";
                case ComparisonVerdict.SameText:    return "same text";
                case ComparisonVerdict.Differs:     return "DIFFERS";
                case ComparisonVerdict.Unclear:     return "unclear";
                case ComparisonVerdict.OnlyLeft:    return "only left";
                case ComparisonVerdict.OnlyRight:   return "only right";
                default:                            return "not compared";
            }
        }

        /// <summary>A comparison as text: settings first, status values apart.</summary>
        public static string Comparison(SnapshotComparison comparison, bool listEqual = false)
        {
            var text = new StringBuilder();
            text.AppendLine("Left:  " + comparison.Left.Name + ". Precision " + comparison.Left.Precision + ".");
            foreach (var note in comparison.Left.Notes) text.AppendLine("       " + note);
            text.AppendLine("Right: " + comparison.Right.Name + ". Precision " + comparison.Right.Precision + ".");
            foreach (var note in comparison.Right.Notes) text.AppendLine("       " + note);

            AppendSection(text, "Settings", comparison.Configuration.ToList(), v => listEqual || v.IsDifference || v.Note != null && v.Verdict == ComparisonVerdict.Same);
            // A file of the web client has no status values at all, so only what both sides have is listed.
            AppendSection(text, "Status values (these change on their own, a difference is no change of the setup)", comparison.Status.ToList(),
                          v => listEqual || v.Verdict == ComparisonVerdict.Differs || v.Verdict == ComparisonVerdict.Unclear);
            text.Append("Same text without proven precision means equal as text only. Nothing was written, no file key was sent to the board.");
            return text.ToString();
        }

        private static void AppendSection(StringBuilder text, string title, List<ComparedValue> values, Func<ComparedValue, bool> listed)
        {
            text.AppendLine();
            text.AppendLine(title + ": " + Counts(values) + ".");
            foreach (var value in values.Where(listed))
                text.AppendLine("  " + Line(value));
        }

        public static string Counts(IReadOnlyCollection<ComparedValue> values)
            => values.Count == 0 ? "none" : string.Join(", ", values.GroupBy(v => v.Verdict).OrderBy(g => g.Key).Select(g => g.Count() + " " + VerdictName(g.Key)));

        public static string Line(ComparedValue value)
        {
            return value.Path + ": " + (value.Left ?? "—") + " | " + (value.Right ?? "—") + "  " + VerdictName(value.Verdict)
                 + (value.Note != null ? " (" + value.Note + ")" : "");
        }

        public static string LoadedFile(LoadedSnapshotFile file)
        {
            if (!file.Loaded) return "Cannot use " + file.ValuesPath + ": " + file.Error;
            var text = new StringBuilder();
            text.Append(System.IO.Path.GetFileName(file.ValuesPath) + ": " + file.Values.Count + " values, " + MetadataNote(file) + ".");
            foreach (var problem in file.Problems) text.Append(" " + problem + ".");
            return text.ToString();
        }
    }
}

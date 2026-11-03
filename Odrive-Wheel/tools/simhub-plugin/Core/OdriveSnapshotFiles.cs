using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace OdriveWheel.SimHubPlugin
{
    public enum SnapshotMetadataState
    {
        /// <summary>Found, readable, and its hash is that of the values file.</summary>
        Matches,
        Missing,
        /// <summary>Not valid JSON, not this kind of file, or a field is missing or of the wrong kind.</summary>
        Unreadable,
        /// <summary>A newer or unknown version of the metadata.</summary>
        UnknownVersion,
        /// <summary>Readable, but it belongs to other content: the values file was changed or replaced.</summary>
        NotForThisFile,
    }

    public sealed class SnapshotExportResult
    {
        internal SnapshotExportResult(string valuesPath, string metadataPath, string sha256, int valueCount, string error)
        {
            ValuesPath   = valuesPath;
            MetadataPath = metadataPath;
            Sha256       = sha256;
            ValueCount   = valueCount;
            Error        = error;
        }

        public string ValuesPath { get; }
        public string MetadataPath { get; }
        public string Sha256 { get; }
        public int ValueCount { get; }
        /// <summary>Null only if both files were written and read back as written.</summary>
        public string Error { get; }
        public bool Succeeded => Error == null;
    }

    /// <summary>What the metadata of a file says. Only filled from metadata that matches its values file.</summary>
    public sealed class SnapshotFileInfo
    {
        internal SnapshotFileInfo() { }

        public FirmwareCapabilityState CapabilityState { get; internal set; }
        public string Declaration { get; internal set; }
        public bool AsciiFull { get; internal set; }
        public bool Float32RoundTrip { get; internal set; }
        public string Completeness { get; internal set; }
        public string AbortReason { get; internal set; }
        public string StartedUtc { get; internal set; }
        public string EndedUtc { get; internal set; }
        public string Build { get; internal set; }
        public IReadOnlyDictionary<string, string> StatusValues { get; internal set; }
        public IReadOnlyDictionary<string, string> Failed { get; internal set; }
    }

    public sealed class LoadedSnapshotFile
    {
        internal LoadedSnapshotFile(string valuesPath, string error)
        {
            ValuesPath = valuesPath;
            Error      = error;
            Values     = new ReadOnlyCollection<KeyValuePair<string, string>>(new KeyValuePair<string, string>[0]);
            Problems   = new ReadOnlyCollection<string>(new string[0]);
            Metadata   = SnapshotMetadataState.Missing;
        }

        internal LoadedSnapshotFile(string valuesPath, IList<KeyValuePair<string, string>> values, IList<string> problems,
                                    string metadataPath, SnapshotMetadataState metadata, string metadataDetail, SnapshotFileInfo info)
        {
            ValuesPath     = valuesPath;
            Values         = new ReadOnlyCollection<KeyValuePair<string, string>>(values.ToList());
            Problems       = new ReadOnlyCollection<string>(problems.ToList());
            MetadataPath   = metadataPath;
            Metadata       = metadata;
            MetadataDetail = metadataDetail;
            Info           = info;
        }

        public string ValuesPath { get; }
        /// <summary>Why nothing could be loaded, null if the values file was read.</summary>
        public string Error { get; }
        public bool Loaded => Error == null;
        /// <summary>The string values of the flat file, in file order.</summary>
        public IReadOnlyList<KeyValuePair<string, string>> Values { get; }
        /// <summary>Entries of the file that are no value, each said in words.</summary>
        public IReadOnlyList<string> Problems { get; }
        public string MetadataPath { get; }
        public SnapshotMetadataState Metadata { get; }
        public string MetadataDetail { get; }
        /// <summary>Null unless the metadata matches.</summary>
        public SnapshotFileInfo Info { get; }

        /// <summary>The precision of this file rests on its metadata alone, and only on metadata that matches.</summary>
        public SnapshotSide ToSide(string name)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in Values) values[pair.Key] = pair.Value;
            var notRead = new Dictionary<string, string>(StringComparer.Ordinal);
            var notes = new List<string>(Problems);

            bool exactFlagsHold = Info != null && Info.CapabilityState == FirmwareCapabilityState.Declared;
            if (Info != null)
            {
                foreach (var pair in Info.StatusValues)
                    if (!values.ContainsKey(pair.Key)) values[pair.Key] = pair.Value;
                foreach (var pair in Info.Failed) notRead[pair.Key] = pair.Value;
                if (Info.Completeness != OdriveSnapshotFile.CompletenessText(SnapshotCompleteness.Complete))
                    notes.Add("the capture of this file was " + Info.Completeness + (Info.AbortReason != null ? ": " + Info.AbortReason : ""));
            }
            else
            {
                notes.Add(OdriveSnapshotText.MetadataNote(this));
            }

            string precision = Info == null
                ? "not proven: " + OdriveSnapshotText.MetadataNote(this)
                : OdriveSnapshotText.Precision(Info.CapabilityState, Info.Declaration, Info.AsciiFull, Info.Float32RoundTrip);
            return new SnapshotSide(name, values, notRead, exactFlagsHold && Info.AsciiFull, exactFlagsHold && Info.Float32RoundTrip, precision, notes);
        }
    }

    /// <summary>
    /// The files of a catalog snapshot. The values file is the flat "path": "string" JSON of the web client's export
    /// (exportJSON in odrive-wheel.html), with the replies exactly as they came. Next to it a metadata file says what the
    /// values are and are not, bound to the values file by its SHA-256. A values file without matching metadata is still
    /// readable, but nothing about its precision is taken for granted.
    /// </summary>
    public static class OdriveSnapshotFile
    {
        public const string MetadataType = "odrive-wheel-catalog-snapshot";
        public const int MetadataVersion = 1;
        public const string MetadataSuffix = ".meta.json";
        /// <summary>A backup of the catalog is a few kilobytes. Anything this large is not one.</summary>
        public const long MaxBytes = 4 * 1024 * 1024;

        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        /// <summary>The web client's file name: odrive_config_ and the ISO time with ':' and '.' replaced.</summary>
        public static string FileName(DateTime utc, SnapshotCompleteness completeness)
            => "odrive_config_" + utc.ToString("yyyy-MM-dd'T'HH-mm-ss-fff'Z'", CultureInfo.InvariantCulture)
             + (completeness == SnapshotCompleteness.Partial ? "_partial" : "") + ".json";

        public static string MetadataPathFor(string valuesPath)
        {
            var directory = Path.GetDirectoryName(valuesPath) ?? "";
            return Path.Combine(directory, Path.GetFileNameWithoutExtension(valuesPath) + MetadataSuffix);
        }

        /// <summary>Like JSON.stringify(obj, null, 2): two spaces, one entry a line, "\n" line ends.</summary>
        public static string ValuesText(IEnumerable<KeyValuePair<string, string>> values)
        {
            var root = new JObject();
            foreach (var pair in values) root.Add(pair.Key, new JValue(pair.Value));
            return Serialize(root);
        }

        private static string Serialize(JToken token)
        {
            using (var text = new StringWriter(CultureInfo.InvariantCulture) { NewLine = "\n" })
            {
                using (var writer = new JsonTextWriter(text) { Formatting = Formatting.Indented, Indentation = 2 })
                    token.WriteTo(writer);
                return text.ToString();
            }
        }

        internal static string CompletenessText(SnapshotCompleteness completeness)
        {
            switch (completeness)
            {
                case SnapshotCompleteness.Complete:     return "complete";
                case SnapshotCompleteness.WithFailures: return "complete with failed values";
                default:                                return "partial";
            }
        }

        private static string StateText(FirmwareCapabilityState state)
        {
            switch (state)
            {
                case FirmwareCapabilityState.Declared:    return "declared";
                case FirmwareCapabilityState.NotDeclared: return "not declared";
                default:                                  return "unknown";
            }
        }

        private static string Iso(DateTime utc) => utc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

        internal static JObject MetadataJson(CatalogSnapshot snapshot, string valuesFileName, byte[] valuesBytes, int valueCount, string pluginVersion)
        {
            var capabilities = snapshot.Capabilities;
            var status = new JObject();
            foreach (var pair in snapshot.StatusValues()) status.Add(pair.Key, pair.Value);

            var failed = new JArray();
            foreach (var entry in snapshot.Entries.Where(e => !e.HasValue))
            {
                var item = new JObject
                {
                    ["path"]    = entry.Path,
                    ["section"] = entry.Section == SnapshotSection.Status ? "status" : "configuration",
                    ["outcome"] = OdriveSnapshotText.OutcomeName(entry.Outcome),
                    ["detail"]  = entry.Detail,
                };
                if (entry.Reply != null) item["reply"] = entry.Reply;
                failed.Add(item);
            }

            var uncertain = new JArray();
            foreach (var entry in snapshot.Entries.Where(e => e.HasValue && e.Certainty != ReplyCertainty.Exact))
                uncertain.Add(new JObject { ["path"] = entry.Path, ["certainty"] = OdriveSnapshotText.CertaintyName(entry.Certainty) });

            return new JObject
            {
                ["_type"]    = MetadataType,
                ["_version"] = MetadataVersion,
                ["what"]     = OdriveSnapshotText.WhatItIs,
                ["values_file"] = new JObject
                {
                    ["name"]    = valuesFileName,
                    ["sha256"]  = OdriveParameterCatalog.Sha256Hex(valuesBytes),
                    ["bytes"]   = valuesBytes.Length,
                    ["entries"] = valueCount,
                },
                ["completeness"] = CompletenessText(snapshot.Completeness),
                ["abort_reason"] = snapshot.AbortReason,
                ["values_exact"] = capabilities.FloatsAreExact,
                ["capabilities"] = new JObject
                {
                    ["state"]       = capabilities.Checked ? StateText(capabilities.State) : "not checked",
                    ["declaration"] = capabilities.Declaration,
                    ["ascii_full"]  = capabilities.AsciiFull,
                    ["float32_rt"]  = capabilities.Float32RoundTrip,
                    ["detail"]      = capabilities.Detail,
                },
                ["capture"] = new JObject
                {
                    ["started_utc"]  = Iso(snapshot.StartedUtc),
                    ["ended_utc"]    = Iso(snapshot.EndedUtc),
                    ["source"]       = "RAM of the board, read over the serial connection",
                    ["simultaneous"] = false,
                    ["port"]         = snapshot.PortName,
                    ["connection"]   = snapshot.ConnectGeneration,
                    ["note"]         = OdriveSnapshotText.TimingNote,
                },
                ["stop"] = new JObject
                {
                    ["latch_set"] = snapshot.StopLatched,
                    ["last_stop"] = snapshot.LastStop?.ToString(),
                },
                ["firmware"] = new JObject { ["build"] = snapshot.Find("sys.build")?.Reply },
                ["catalog"] = new JObject
                {
                    ["version"]             = snapshot.CatalogVersion,
                    ["sha256"]              = snapshot.CatalogSha256,
                    ["expected_paths"]      = snapshot.Expected,
                    ["configuration_paths"] = snapshot.Entries.Count(e => e.Section == SnapshotSection.Configuration),
                    ["status_paths"]        = snapshot.Entries.Count(e => e.Section == SnapshotSection.Status),
                    ["read"]                = snapshot.ReadCount,
                },
                ["not_included"]  = new JArray(OdriveSnapshotText.NotIncluded.Cast<object>().ToArray()),
                ["status_values"] = status,
                ["failed"]        = failed,
                ["uncertain"]     = uncertain,
                ["plugin"]        = new JObject { ["version"] = pluginVersion },
            };
        }

        /// <summary>
        /// Writes the values file and its metadata. Nothing existing is replaced. Both are written to temporary files,
        /// read back, moved into place and then loaded again as any file would be: success means that load found the
        /// metadata matching and the values as captured. On any failure what this export wrote is removed again.
        /// </summary>
        public static SnapshotExportResult Export(CatalogSnapshot snapshot, string valuesPath, string pluginVersion)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            string metadataPath = null;
            var written = new List<string>();
            try
            {
                if (string.IsNullOrWhiteSpace(valuesPath)) return Failed(valuesPath, null, "no file name");
                valuesPath = Path.GetFullPath(valuesPath);
                if (!valuesPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || valuesPath.EndsWith(MetadataSuffix, StringComparison.OrdinalIgnoreCase))
                    return Failed(valuesPath, null, "the values file must end in .json, and not in " + MetadataSuffix);
                metadataPath = MetadataPathFor(valuesPath);
                foreach (var path in new[] { valuesPath, metadataPath })
                    if (File.Exists(path)) return Failed(valuesPath, metadataPath, Path.GetFileName(path) + " already exists, nothing was replaced");

                var values = snapshot.ConfigurationValues().ToList();
                var valuesBytes = Utf8.GetBytes(ValuesText(values));
                var metadataBytes = Utf8.GetBytes(Serialize(MetadataJson(snapshot, Path.GetFileName(valuesPath), valuesBytes, values.Count, pluginVersion)));

                WriteChecked(valuesPath + ".tmp", valuesBytes, written);
                WriteChecked(metadataPath + ".tmp", metadataBytes, written);
                MoveInPlace(valuesPath + ".tmp", valuesPath, written);
                MoveInPlace(metadataPath + ".tmp", metadataPath, written);

                var back = Load(valuesPath);
                if (!back.Loaded) throw new IOException("the written file cannot be read back: " + back.Error);
                if (back.Metadata != SnapshotMetadataState.Matches) throw new IOException("the written metadata does not match: " + back.MetadataDetail);
                if (!back.Values.SequenceEqual(values)) throw new IOException("the written values differ from the snapshot when read back");

                return new SnapshotExportResult(valuesPath, metadataPath, OdriveParameterCatalog.Sha256Hex(valuesBytes), values.Count, null);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException
                                       || ex is NotSupportedException || ex is System.Security.SecurityException)
            {
                foreach (var path in written)
                {
                    try { File.Delete(path); }
                    catch (Exception) { /* reported below as part of the failure */ }
                }
                var left = written.Where(File.Exists).ToList();
                return Failed(valuesPath, metadataPath, ex.Message + (left.Count > 0 ? ". Could not remove: " + string.Join(", ", left) : ""));
            }
        }

        private static SnapshotExportResult Failed(string valuesPath, string metadataPath, string error)
            => new SnapshotExportResult(valuesPath, metadataPath, null, 0, error);

        private static void WriteChecked(string path, byte[] bytes, List<string> written)
        {
            if (File.Exists(path)) throw new IOException(Path.GetFileName(path) + " is in the way");
            written.Add(path);
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
            if (!File.ReadAllBytes(path).SequenceEqual(bytes)) throw new IOException(Path.GetFileName(path) + " reads back differently");
        }

        private static void MoveInPlace(string from, string to, List<string> written)
        {
            File.Move(from, to);
            written.Remove(from);
            written.Add(to);
        }

        /// <summary>
        /// Reads a values file, or the values file a metadata file names. Every key is data: it is compared and shown,
        /// and never becomes a command. Damage is reported, never repaired.
        /// </summary>
        public static LoadedSnapshotFile Load(string path)
        {
            string valuesPath = path;
            try
            {
                if (string.IsNullOrWhiteSpace(path)) return new LoadedSnapshotFile(path, "no file name");
                path = Path.GetFullPath(path);
                valuesPath = path;

                if (path.EndsWith(MetadataSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    var named = NamedValuesFile(path, out var why);
                    if (named == null) return new LoadedSnapshotFile(path, why);
                    valuesPath = named;
                }

                var bytes = ReadLimited(valuesPath);
                var (values, problems, error) = ParseValues(bytes);
                if (error != null) return new LoadedSnapshotFile(valuesPath, Path.GetFileName(valuesPath) + ": " + error);

                var metadataPath = MetadataPathFor(valuesPath);
                var (state, detail, info) = ReadMetadata(metadataPath, bytes);
                return new LoadedSnapshotFile(valuesPath, values, problems, metadataPath, state, detail, info);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException
                                       || ex is NotSupportedException || ex is System.Security.SecurityException)
            {
                return new LoadedSnapshotFile(valuesPath, ex.Message);
            }
        }

        // A name from the file is data as well: only a plain file name next to the metadata is followed.
        private static string NamedValuesFile(string metadataPath, out string why)
        {
            why = null;
            JObject meta;
            try { meta = ParseObject(ReadLimited(metadataPath)); }
            catch (Exception ex) when (ex is JsonException || ex is DecoderFallbackException || ex is InvalidDataException)
            {
                why = Path.GetFileName(metadataPath) + " is not readable: " + ex.Message;
                return null;
            }
            var name = (meta["values_file"] as JObject)?["name"] as JValue;
            var text = name?.Type == JTokenType.String ? (string)name.Value : null;
            if (string.IsNullOrEmpty(text) || text != Path.GetFileName(text) || text.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || !text.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || text.EndsWith(MetadataSuffix, StringComparison.OrdinalIgnoreCase))
            {
                why = Path.GetFileName(metadataPath) + " names no values file next to it";
                return null;
            }
            return Path.Combine(Path.GetDirectoryName(metadataPath) ?? "", text);
        }

        private static byte[] ReadLimited(string path)
        {
            var info = new FileInfo(path);
            if (!info.Exists) throw new FileNotFoundException(Path.GetFileName(path) + " does not exist");
            if (info.Length > MaxBytes) throw new IOException(Path.GetFileName(path) + " is too large for a snapshot file");
            return File.ReadAllBytes(path);
        }

        // Strings stay strings: no date or number conversion, so a value is the text in the file.
        private static JObject ParseObject(byte[] bytes)
        {
            var text = Utf8.GetString(bytes);
            if (text.Length > 0 && text[0] == '﻿') text = text.Substring(1);
            using (var reader = new JsonTextReader(new StringReader(text)) { DateParseHandling = DateParseHandling.None, FloatParseHandling = FloatParseHandling.Decimal })
            {
                var token = JToken.ReadFrom(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                if (reader.Read()) throw new InvalidDataException("there is more after the JSON object");
                if (!(token is JObject root)) throw new InvalidDataException("the file is no JSON object");
                return root;
            }
        }

        private static (List<KeyValuePair<string, string>> Values, List<string> Problems, string Error) ParseValues(byte[] bytes)
        {
            JObject root;
            try { root = ParseObject(bytes); }
            catch (DecoderFallbackException) { return (null, null, "not valid UTF-8 text"); }
            catch (Exception ex) when (ex is JsonException || ex is InvalidDataException) { return (null, null, "not a valid values file: " + ex.Message); }

            if (root["_type"] != null || root["values_file"] != null)
                return (null, null, "this is no flat values file, but another kind of JSON");

            var values = new List<KeyValuePair<string, string>>();
            var problems = new List<string>();
            foreach (var property in root.Properties())
            {
                if (property.Value.Type == JTokenType.String) values.Add(new KeyValuePair<string, string>(property.Name, (string)property.Value));
                else problems.Add("'" + property.Name + "' is no string (" + property.Value.Type.ToString().ToLowerInvariant() + "), ignored");
            }
            if (values.Count == 0) problems.Add("the file holds no values");
            return (values, problems, null);
        }

        private static (SnapshotMetadataState State, string Detail, SnapshotFileInfo Info) ReadMetadata(string metadataPath, byte[] valuesBytes)
        {
            if (!File.Exists(metadataPath)) return (SnapshotMetadataState.Missing, "no " + Path.GetFileName(metadataPath) + " next to it", null);

            JObject meta;
            try { meta = ParseObject(ReadLimited(metadataPath)); }
            catch (Exception ex) when (ex is JsonException || ex is DecoderFallbackException || ex is InvalidDataException || ex is IOException)
            {
                return (SnapshotMetadataState.Unreadable, "the metadata is not readable: " + ex.Message, null);
            }

            if (String(meta, "_type") != MetadataType) return (SnapshotMetadataState.Unreadable, "the metadata file is of another kind", null);
            var version = meta["_version"];
            if (version == null || version.Type != JTokenType.Integer) return (SnapshotMetadataState.Unreadable, "the metadata has no version", null);
            if ((long)version != MetadataVersion) return (SnapshotMetadataState.UnknownVersion, "metadata version " + version + " is not known to this plugin", null);

            var file = meta["values_file"] as JObject;
            var hash = file != null ? String(file, "sha256") : null;
            if (hash == null) return (SnapshotMetadataState.Unreadable, "the metadata names no hash of its values file", null);
            if (!string.Equals(hash, OdriveParameterCatalog.Sha256Hex(valuesBytes), StringComparison.Ordinal))
                return (SnapshotMetadataState.NotForThisFile, "the metadata belongs to other content: the values file was changed or replaced", null);

            try
            {
                var capabilities = (JObject)meta["capabilities"];
                var capture = (JObject)meta["capture"];
                var info = new SnapshotFileInfo
                {
                    Declaration      = String(capabilities, "declaration"),
                    AsciiFull        = Bool(capabilities, "ascii_full"),
                    Float32RoundTrip = Bool(capabilities, "float32_rt"),
                    Completeness     = Required(meta, "completeness"),
                    AbortReason      = String(meta, "abort_reason"),
                    StartedUtc       = Required(capture, "started_utc"),
                    EndedUtc         = Required(capture, "ended_utc"),
                    Build            = String((JObject)meta["firmware"], "build"),
                    StatusValues     = Strings((JObject)meta["status_values"]),
                    Failed           = FailedPaths((JArray)meta["failed"]),
                };
                var state = Required(capabilities, "state");
                info.CapabilityState = state == "declared" ? FirmwareCapabilityState.Declared : FirmwareCapabilityState.NotDeclared;

                // The flags have to be what the recorded declaration says, or neither counts.
                if (info.CapabilityState == FirmwareCapabilityState.Declared
                    && (!FirmwareCapabilities.TryParse(info.Declaration, 0, out var declared, out _)
                        || declared.AsciiFull != info.AsciiFull || declared.Float32RoundTrip != info.Float32RoundTrip))
                    return (SnapshotMetadataState.Unreadable, "the metadata's flags do not fit its recorded declaration", null);
                if (info.CapabilityState != FirmwareCapabilityState.Declared)
                {
                    info.AsciiFull = false;
                    info.Float32RoundTrip = false;
                }
                return (SnapshotMetadataState.Matches, null, info);
            }
            catch (Exception ex) when (ex is InvalidCastException || ex is InvalidDataException || ex is NullReferenceException || ex is ArgumentException)
            {
                return (SnapshotMetadataState.Unreadable, "the metadata is damaged: " + ex.Message, null);
            }
        }

        private static string String(JObject parent, string key)
        {
            var token = parent?[key];
            if (token == null || token.Type == JTokenType.Null) return null;
            if (token.Type != JTokenType.String) throw new InvalidDataException(key + " is no string");
            return (string)token;
        }

        private static string Required(JObject parent, string key)
            => String(parent, key) ?? throw new InvalidDataException(key + " is missing");

        private static bool Bool(JObject parent, string key)
        {
            var token = parent?[key];
            if (token == null || token.Type != JTokenType.Boolean) throw new InvalidDataException(key + " is no boolean");
            return (bool)token;
        }

        private static IReadOnlyDictionary<string, string> Strings(JObject section)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            if (section == null) throw new InvalidDataException("status_values is missing");
            foreach (var property in section.Properties())
            {
                if (property.Value.Type != JTokenType.String) throw new InvalidDataException("status value " + property.Name + " is no string");
                result[property.Name] = (string)property.Value;
            }
            return new ReadOnlyDictionary<string, string>(result);
        }

        private static IReadOnlyDictionary<string, string> FailedPaths(JArray failed)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            if (failed == null) throw new InvalidDataException("failed is missing");
            foreach (var item in failed)
            {
                var entry = item as JObject ?? throw new InvalidDataException("an entry of failed is no object");
                var path = Required(entry, "path");
                result[path] = Required(entry, "outcome") + (String(entry, "detail") is string detail ? " (" + detail + ")" : "");
            }
            return new ReadOnlyDictionary<string, string>(result);
        }
    }
}

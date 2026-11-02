using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace OdriveWheel.SimHubPlugin
{
    public enum ParameterType
    {
        Float,
        Integer,
        Boolean,
        Text,
        /// <summary>Error bits, 0 means no error.</summary>
        Bitmask,
        /// <summary>ODrive AXIS_STATE number.</summary>
        AxisState,
        /// <summary>A number with names, see ParameterDefinition.EnumValues.</summary>
        Enum,
    }

    public enum ParameterAccess
    {
        Read,
        Write,
    }

    /// <summary>Which table of OdriveErrorDecoder a bitmask belongs to.</summary>
    public enum ParameterErrorKind
    {
        None,
        System,
        Axis,
        Motor,
        Encoder,
        Controller,
    }

    public sealed class ParameterDefinition
    {
        public string          Path        { get; internal set; }
        public FieldProtocol   Protocol    { get; internal set; }
        public ParameterType   Type        { get; internal set; }
        public ParameterAccess Access      { get; internal set; }
        public string          Unit        { get; internal set; }
        public double?         Min         { get; internal set; }
        public double?         Max         { get; internal set; }
        public string          Group       { get; internal set; }
        public string          Tab         { get; internal set; }
        public string          Description { get; internal set; }
        public ParameterErrorKind ErrorKind { get; internal set; }
        /// <summary>Names of an enum parameter, from the firmware interface.</summary>
        public IReadOnlyDictionary<long, string> EnumValues { get; internal set; }
        /// <summary>The status tab reads this once a second, everything else only on request.</summary>
        public bool Polled     { get; internal set; }
        /// <summary>Result of a calibration: the firmware accepts a write, but the web client refuses it.</summary>
        public bool Calibrated { get; internal set; }
    }

    /// <summary>
    /// The parameters the plugin knows about, from the embedded parameter-catalog.json.
    /// The tab names match the SCHEMA ids of the web client. Paths, types, access and enum
    /// names follow odrive-interface.yaml (tests/plugin/CatalogInterfaceTests.cs). Min and
    /// max are curated by hand, the web client has neither.
    /// </summary>
    public sealed class OdriveParameterCatalog
    {
        public const string ResourceName = "OdriveWheel.SimHubPlugin.parameter-catalog.json";

        private OdriveParameterCatalog(IReadOnlyList<ParameterDefinition> parameters)
        {
            Parameters = parameters;
        }

        public IReadOnlyList<ParameterDefinition> Parameters { get; }

        public IEnumerable<ParameterDefinition> InTab(string tab) => Parameters.Where(p => p.Tab == tab);

        public ParameterDefinition Find(string path) => Parameters.FirstOrDefault(p => p.Path == path);

        public static OdriveParameterCatalog LoadEmbedded()
        {
            using (var stream = typeof(OdriveParameterCatalog).Assembly.GetManifestResourceStream(ResourceName))
            {
                if (stream == null) throw new InvalidDataException("missing resource " + ResourceName);
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                    return Parse(reader.ReadToEnd());
            }
        }

        /// <exception cref="InvalidDataException">The catalog is malformed.</exception>
        public static OdriveParameterCatalog Parse(string json)
        {
            JObject root;
            try { root = JObject.Parse(json); }
            catch (Newtonsoft.Json.JsonException ex) { throw new InvalidDataException("catalog is not valid JSON: " + ex.Message, ex); }

            if (!(root["parameters"] is JArray entries))
                throw new InvalidDataException("catalog has no parameters array");

            var enums = ParseEnums(root["enums"] as JObject);
            var parameters = new List<ParameterDefinition>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var token in entries)
            {
                if (!(token is JObject entry)) throw new InvalidDataException("catalog entry is not an object");
                var definition = ParseEntry(entry, enums);
                if (!seen.Add(definition.Path)) throw new InvalidDataException("duplicate path: " + definition.Path);
                parameters.Add(definition);
            }
            return new OdriveParameterCatalog(parameters);
        }

        private static Dictionary<string, IReadOnlyDictionary<long, string>> ParseEnums(JObject section)
        {
            var enums = new Dictionary<string, IReadOnlyDictionary<long, string>>(StringComparer.Ordinal);
            if (section == null) return enums;
            foreach (var definition in section.Properties())
            {
                var values = new Dictionary<long, string>();
                foreach (var value in ((JObject)definition.Value).Properties())
                {
                    if (!long.TryParse(value.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out long number))
                        throw new InvalidDataException("enum " + definition.Name + " has a value that is no number: " + value.Name);
                    values[number] = (string)value.Value;
                }
                enums[definition.Name] = values;
            }
            return enums;
        }

        private static ParameterDefinition ParseEntry(JObject entry, Dictionary<string, IReadOnlyDictionary<long, string>> enums)
        {
            var path = Required(entry, "path", "(no path)");
            if (!OdriveProtocol.IsValidPath(path)) throw new InvalidDataException("invalid path: " + path);

            var definition = new ParameterDefinition
            {
                Path        = path,
                Protocol    = ParseProtocol(Required(entry, "protocol", path), path),
                Type        = ParseType(Required(entry, "type", path), path),
                Access      = ParseAccess(Required(entry, "access", path), path),
                Unit        = entry.Value<string>("unit") ?? "",
                Min         = entry.Value<double?>("min"),
                Max         = entry.Value<double?>("max"),
                Group       = Required(entry, "group", path),
                Tab         = Required(entry, "tab", path),
                Description = Required(entry, "description", path),
                ErrorKind   = ParseErrorKind(entry.Value<string>("flags"), path),
                Polled      = entry.Value<bool?>("poll") ?? false,
                Calibrated  = entry.Value<bool?>("calibrated") ?? false,
            };
            if (definition.Min > definition.Max)
                throw new InvalidDataException("min is above max: " + path);

            var enumName = entry.Value<string>("enum");
            if ((definition.Type == ParameterType.Enum) != (enumName != null))
                throw new InvalidDataException("enum and type do not fit: " + path);
            if (enumName != null)
            {
                if (!enums.TryGetValue(enumName, out var values))
                    throw new InvalidDataException("unknown enum '" + enumName + "': " + path);
                definition.EnumValues = values;
            }
            if (definition.ErrorKind != ParameterErrorKind.None && definition.Type != ParameterType.Bitmask)
                throw new InvalidDataException("flags on a parameter that is no bitmask: " + path);
            return definition;
        }

        private static ParameterErrorKind ParseErrorKind(string text, string path)
        {
            switch (text)
            {
                case null:         return ParameterErrorKind.None;
                case "system":     return ParameterErrorKind.System;
                case "axis":       return ParameterErrorKind.Axis;
                case "motor":      return ParameterErrorKind.Motor;
                case "encoder":    return ParameterErrorKind.Encoder;
                case "controller": return ParameterErrorKind.Controller;
                default: throw new InvalidDataException("unknown flags '" + text + "': " + path);
            }
        }

        private static string Required(JObject entry, string key, string path)
        {
            var value = entry.Value<string>(key);
            if (string.IsNullOrEmpty(value))
                throw new InvalidDataException("missing " + key + ": " + path);
            return value;
        }

        private static FieldProtocol ParseProtocol(string text, string path)
        {
            switch (text)
            {
                case "odrive": return FieldProtocol.Odrive;
                case "offb":   return FieldProtocol.Openffb;
                default: throw new InvalidDataException("unknown protocol '" + text + "': " + path);
            }
        }

        private static ParameterType ParseType(string text, string path)
        {
            switch (text)
            {
                case "float":     return ParameterType.Float;
                case "integer":   return ParameterType.Integer;
                case "boolean":   return ParameterType.Boolean;
                case "text":      return ParameterType.Text;
                case "bitmask":   return ParameterType.Bitmask;
                case "axisState": return ParameterType.AxisState;
                case "enum":      return ParameterType.Enum;
                default: throw new InvalidDataException("unknown type '" + text + "': " + path);
            }
        }

        private static ParameterAccess ParseAccess(string text, string path)
        {
            switch (text)
            {
                case "read":  return ParameterAccess.Read;
                case "write": return ParameterAccess.Write;
                default: throw new InvalidDataException("unknown access '" + text + "': " + path);
            }
        }
    }
}

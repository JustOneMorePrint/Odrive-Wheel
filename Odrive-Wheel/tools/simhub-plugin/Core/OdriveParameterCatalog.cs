using System;
using System.Collections.Generic;
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
    }

    public enum ParameterAccess
    {
        Read,
        Write,
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
    }

    /// <summary>
    /// The parameters the plugin knows about, from the embedded parameter-catalog.json.
    /// The tab names match the SCHEMA ids of the web client. Min and max are curated by
    /// hand, the web client has neither.
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

            var parameters = new List<ParameterDefinition>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var token in entries)
            {
                if (!(token is JObject entry)) throw new InvalidDataException("catalog entry is not an object");
                var definition = ParseEntry(entry);
                if (!seen.Add(definition.Path)) throw new InvalidDataException("duplicate path: " + definition.Path);
                parameters.Add(definition);
            }
            return new OdriveParameterCatalog(parameters);
        }

        private static ParameterDefinition ParseEntry(JObject entry)
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
            };
            if (definition.Min > definition.Max)
                throw new InvalidDataException("min is above max: " + path);
            return definition;
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

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace OdriveWheel.SimHubPlugin.Tests
{
    /// <summary>
    /// Holds the catalog against the firmware itself: odrive-interface.yaml generates the ODrive
    /// properties, cmd_table.cpp lists the OpenFFBoard commands. The web client was only the
    /// source for which fields exist, and it is wrong in places (see the stream protocol test).
    /// </summary>
    public static class CatalogInterfaceTests
    {
        private const string InterfaceFile = "ODrive-fw-v0.5.6/ODrive-fw-v0.5.6/Firmware/odrive-interface.yaml";
        private const string CommandFile = "Odrive-Wheel/src/cmd_table.cpp";

        private static readonly Lazy<YamlNode> Interface
            = new Lazy<YamlNode>(() => YamlSubset.Parse(File.ReadAllText(FromRepoRoot(InterfaceFile))));

        private static OdriveParameterCatalog Catalog => OdriveParameterCatalog.LoadEmbedded();

        internal static string FromRepoRoot(string relativePath)
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, relativePath);
                if (File.Exists(candidate)) return candidate;
            }
            throw new FileNotFoundException(relativePath);
        }

        // ---- Walking the interface -------------------------------------------

        private static YamlNode Interfaces => Interface.Value["interfaces"];
        private static YamlNode ValueTypes => Interface.Value["valuetypes"];

        private static YamlNode FindAttribute(YamlNode owner, string name)
        {
            var attribute = owner["attributes"]?[name];
            if (attribute != null) return attribute;
            var implemented = owner["implements"]?.Text;
            return implemented == null ? null : FindAttribute(Interfaces[implemented], name);
        }

        // Names are relative to the enclosing interface, the enums of the root sit under ODrive.
        private static string ResolveName(YamlNode table, string name, string scope)
        {
            for (var parts = scope.Split('.').ToList(); ; parts.RemoveAt(parts.Count - 1))
            {
                var candidate = parts.Count > 0 ? string.Join(".", parts) + "." + name : name;
                if (table[candidate] != null) return candidate;
                if (parts.Count == 0) return table["ODrive." + name] != null ? "ODrive." + name : null;
            }
        }

        private static string TypeOf(YamlNode attribute) => attribute.Text ?? attribute["type"]?.Text;

        /// <summary>The attribute at the path and the interface scope around it, null if there is none.</summary>
        private static (YamlNode Attribute, string Scope) Walk(string path)
        {
            var rootName = Interfaces.Keys.First(k => Interfaces[k]["attributes"]?["axis0"] != null);
            var current = Interfaces[rootName];
            var scope = "ODrive";

            var parts = path.Split('.');
            for (int i = 0; i < parts.Length; i++)
            {
                var attribute = FindAttribute(current, parts[i]);
                if (attribute == null) return (null, scope);
                if (i == parts.Length - 1) return (attribute, scope);

                var type = TypeOf(attribute);
                if (type == null)
                {
                    current = attribute;    // an interface written in place
                    continue;
                }
                var name = ResolveName(Interfaces, type, scope);
                if (name == null) return (null, scope);
                current = Interfaces[name];
                scope = name;
            }
            return (null, scope);
        }

        private static Dictionary<long, string> EnumValues(string fullName)
        {
            var values = ValueTypes[fullName]["values"];
            var result = new Dictionary<long, string>();
            long next = 0;
            foreach (var name in values.Keys)
            {
                var explicitValue = values[name]["value"]?.Text;
                if (explicitValue != null)
                    next = explicitValue.StartsWith("0x", StringComparison.Ordinal) ? Convert.ToInt64(explicitValue, 16) : long.Parse(explicitValue);
                result[next++] = name;
            }
            return result;
        }

        // ---- Tests -----------------------------------------------------------

        public static void Test_every_odrive_parameter_exists_with_this_type_access_and_unit()
        {
            var odrive = Catalog.Parameters.Where(p => p.Protocol == FieldProtocol.Odrive).ToList();
            Check.True(odrive.Count > 180, "entries: " + odrive.Count);

            foreach (var p in odrive)
            {
                var (attribute, scope) = Walk(p.Path);
                if (attribute == null)
                {
                    Check.Fail(p.Path + " is not in the interface");
                    continue;
                }

                var type = TypeOf(attribute);
                bool readOnly = type != null && type.StartsWith("readonly ", StringComparison.Ordinal);
                if (readOnly) type = type.Substring("readonly ".Length);

                Check.Equal(readOnly ? ParameterAccess.Read : ParameterAccess.Write, p.Access, p.Path + " access");
                CheckType(p, type, attribute, scope);

                var unit = attribute["unit"]?.Text;
                if (unit != null) Check.Equal(unit, p.Unit, p.Path + " unit");
            }
        }

        private static void CheckType(ParameterDefinition p, string type, YamlNode attribute, string scope)
        {
            switch (p.Type)
            {
                case ParameterType.Boolean:
                    Check.Equal("bool", type, p.Path + " type");
                    break;
                case ParameterType.Float:
                    Check.Equal("float32", type, p.Path + " type");
                    break;
                case ParameterType.Integer:
                    Check.True(type != null && Regex.IsMatch(type, @"^u?int\d+$"), p.Path + " is " + type);
                    break;
                case ParameterType.Bitmask:
                    Check.True(attribute["flags"] != null, p.Path + " has flags");
                    break;
                case ParameterType.AxisState:
                    Check.Equal("ODrive.Axis.AxisState", ResolveName(ValueTypes, type ?? "", scope), p.Path + " type");
                    break;
                case ParameterType.Enum:
                    var name = ResolveName(ValueTypes, type ?? "", scope);
                    Check.True(name != null, p.Path + " has the enum " + type);
                    if (name == null) break;
                    var expected = EnumValues(name);
                    Check.Equal(string.Join(",", expected.OrderBy(e => e.Key).Select(e => e.Key + "=" + e.Value)),
                                string.Join(",", p.EnumValues.OrderBy(e => e.Key).Select(e => e.Key + "=" + e.Value)), p.Path + " names");
                    break;
                default:
                    Check.Fail(p.Path + " has the type " + p.Type + ", which the interface does not use");
                    break;
            }
        }

        public static void Test_every_openffboard_parameter_is_in_the_command_table()
        {
            var table = File.ReadAllText(FromRepoRoot(CommandFile));
            foreach (var p in Catalog.Parameters.Where(p => p.Protocol == FieldProtocol.Openffb))
            {
                var parts = p.Path.Split('.');
                Check.True(Regex.IsMatch(table, "\\{\\s*\"" + parts[0] + "\",\\s*\"" + parts[1] + "\""), p.Path + " is in cmd_table.cpp");
            }
        }

        // Reading must not do anything, and a '?' on a command handler can: sys.save? saves.
        // The two handlers here only format a value on a read (h_sys_build, h_sys_vbusdiv).
        public static void Test_openffboard_reads_of_the_window_are_known_to_be_harmless()
        {
            var window = new[] { "status", "odrv", "axis0", "motor", "encoder", "controller" };
            var offb = Catalog.Parameters.Where(p => window.Contains(p.Tab) && p.Protocol == FieldProtocol.Openffb)
                              .Select(p => p.Path).OrderBy(p => p, StringComparer.Ordinal).ToArray();
            Check.Lines(new[] { "sys.build", "sys.vbusdiv" }, offb, "OpenFFBoard reads of the window");
        }

        public static void Test_error_tables_match_the_interface()
        {
            var tables = new (string Path, ParameterErrorKind Kind)[]
            {
                ("error", ParameterErrorKind.System),
                ("axis0.error", ParameterErrorKind.Axis),
                ("axis0.motor.error", ParameterErrorKind.Motor),
                ("axis0.encoder.error", ParameterErrorKind.Encoder),
                ("axis0.controller.error", ParameterErrorKind.Controller),
            };
            foreach (var (path, kind) in tables)
            {
                var flags = Walk(path).Attribute["flags"];
                int bit = 0;
                foreach (var name in flags.Keys)
                {
                    var explicitBit = flags[name]["bit"]?.Text;
                    if (explicitBit != null) bit = int.Parse(explicitBit);
                    // The decoder works on 32 bits. Above that a serial read cuts the number anyway.
                    if (bit < 32) Check.True(OdriveErrorDecoder.Decode(kind, 1u << bit).Contains(name), path + " bit " + bit + " is " + name);
                    bit++;
                }
                Check.Equal(kind, Catalog.Find(path).ErrorKind, path + " uses its table");
            }
        }

        // The web client calls these 1 FIBRE, 2 ASCII, 3 ASCII_AND_STDOUT, 4 STDOUT.
        // interfaces.hpp has no NONE and counts from 0.
        public static void Test_stream_protocol_names_follow_the_firmware_not_the_web_client()
        {
            var p = Catalog.Find("config.usb_cdc_protocol");
            Check.Equal("0 — Fibre", OdriveParameterFormat.Format(p, "0").Text, "0");
            Check.Equal("1 — Ascii", OdriveParameterFormat.Format(p, "1").Text, "1");
            Check.Equal("2 — Stdout", OdriveParameterFormat.Format(p, "2").Text, "2");
            Check.Equal("3 — AsciiAndStdout", OdriveParameterFormat.Format(p, "3").Text, "3");
            Check.Equal("4 — ?", OdriveParameterFormat.Format(p, "4").Text, "the firmware has no 4");
        }

        // Writable in the firmware although the web client treats them as read-only, and
        // read-only in the firmware although the web client offers to write them.
        public static void Test_access_follows_the_firmware_where_the_web_client_differs()
        {
            foreach (var path in new[] { "axis0.motor.config.phase_resistance", "axis0.motor.config.phase_inductance",
                                         "axis0.encoder.config.direction", "axis0.encoder.config.phase_offset", "axis0.encoder.config.phase_offset_float" })
            {
                Check.Equal(ParameterAccess.Write, Catalog.Find(path).Access, path);
                Check.True(Catalog.Find(path).Calibrated, path + " is marked as a calibration result");
            }
            foreach (var path in new[] { "axis0.controller.config.anticogging.cogging_ratio", "axis0.controller.config.anticogging.index" })
                Check.Equal(ParameterAccess.Read, Catalog.Find(path).Access, path);
        }

        public static void Test_no_limits_were_made_up()
        {
            var withLimits = Catalog.Parameters.Where(p => p.Min != null || p.Max != null)
                                    .Select(p => p.Path).OrderBy(p => p, StringComparer.Ordinal).ToArray();
            Check.Lines(new[] { "axis.maxtorque", "axis0.motor.config.current_lim", "fx.master" }, withLimits, "the three entries from 1a");
        }

        public static void Test_the_tabs_hold_the_fields_of_the_web_client()
        {
            var expected = new (string Tab, int Count)[] { ("odrv", 23), ("axis0", 35), ("motor", 36), ("encoder", 21), ("controller", 35) };
            foreach (var (tab, count) in expected)
                Check.Equal(count, Catalog.InTab(tab).Count(), tab);

            Check.Equal("odrv", Catalog.Find("sys.vbusdiv").Tab, "the one OpenFFBoard field among them");
            Check.True(Catalog.Find("config.gpio1_mode") == null, "left out on purpose by the web client, belongs to the inputs");
        }

        public static void Test_only_the_short_status_list_is_polled()
        {
            var polled = Catalog.Parameters.Where(p => p.Polled).Select(p => p.Path).OrderBy(p => p, StringComparer.Ordinal).ToArray();
            Check.Lines(new[] { "axis0.controller.error", "axis0.current_state", "axis0.encoder.error", "axis0.error", "axis0.motor.error",
                                "error", "sys.build", "vbus_voltage" }, polled, "polled");
        }
    }
}

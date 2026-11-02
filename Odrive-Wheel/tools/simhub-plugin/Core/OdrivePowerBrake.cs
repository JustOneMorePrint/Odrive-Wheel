using System;
using System.Collections.Generic;
using System.Linq;

namespace OdriveWheel.SimHubPlugin
{
    public sealed class PowerBrakeViolation
    {
        internal PowerBrakeViolation(string id, string message)
        {
            Id      = id;
            Message = message;
        }

        public string Id      { get; }
        public string Message { get; }
    }

    /// <summary>
    /// How the Power / Brake values relate, from what the firmware does with them (ODrive::do_fast_checks in
    /// main.cpp, update_brake_current in low_level.cpp, the remarks in odrive-interface.yaml). These are no
    /// general limits: a value alone is never too high or too low here, only in relation to the others.
    /// </summary>
    public static class OdrivePowerBrakeRules
    {
        public const string BrakeResistance = "config.brake_resistance";
        public const string EnableBrake     = "config.enable_brake_resistor";
        public const string UnderVoltage    = "config.dc_bus_undervoltage_trip_level";
        public const string OverVoltage     = "config.dc_bus_overvoltage_trip_level";
        public const string EnableRamp      = "config.enable_dc_bus_overvoltage_ramp";
        public const string RampStart       = "config.dc_bus_overvoltage_ramp_start";
        public const string RampEnd         = "config.dc_bus_overvoltage_ramp_end";
        public const string MaxPositive     = "config.dc_max_positive_current";
        public const string MaxNegative     = "config.dc_max_negative_current";
        public const string MaxRegen        = "config.max_regen_current";

        /// <summary>What would be wrong with these values at this bus voltage.</summary>
        public static IReadOnlyList<PowerBrakeViolation> Check(IReadOnlyDictionary<string, double> values, double busVoltage)
        {
            var found = new List<PowerBrakeViolation>();
            void Add(string id, string message) => found.Add(new PowerBrakeViolation(id, message));

            bool brakeOn = values[EnableBrake] != 0.0;
            bool rampOn  = values[EnableRamp] != 0.0;
            double resistance = values[BrakeResistance];

            if (brakeOn && !(resistance > 0.0))
                Add("brake-resistance", "brake_resistance must be above 0 while the brake resistor is enabled, the firmware stops with INVALID_BRAKE_RESISTANCE otherwise.");

            if (!(values[UnderVoltage] < values[OverVoltage]))
                Add("trip-order", "dc_bus_undervoltage_trip_level must be below dc_bus_overvoltage_trip_level, no bus voltage can pass both otherwise.");
            if (busVoltage < values[UnderVoltage])
                Add("bus-under", "The measured bus voltage is below dc_bus_undervoltage_trip_level, the firmware would stop with DC_BUS_UNDER_VOLTAGE at once.");
            if (busVoltage > values[OverVoltage])
                Add("bus-over", "The measured bus voltage is above dc_bus_overvoltage_trip_level, the firmware would stop with DC_BUS_OVER_VOLTAGE at once.");

            if (rampOn)
            {
                if (!brakeOn)
                    Add("ramp-brake", "The overvoltage ramp only works with the brake resistor enabled.");
                if (!(resistance > 0.0))
                    Add("ramp-resistance", "The firmware disables the overvoltage ramp while brake_resistance is not above 0.");
                if (!(values[RampStart] < values[RampEnd]))
                    Add("ramp-order", "dc_bus_overvoltage_ramp_end must be above dc_bus_overvoltage_ramp_start, the firmware disables the ramp otherwise.");
                if (!(values[RampStart] > busVoltage))
                    Add("ramp-bus", "dc_bus_overvoltage_ramp_start must be above the measured bus voltage: the ramp also runs with the motors disarmed and would drive the brake resistor continuously.");
            }

            if (values[MaxPositive] < 0.0)
                Add("positive-current", "dc_max_positive_current must not be negative, the firmware would stop with DC_BUS_OVER_CURRENT at idle.");
            if (values[MaxNegative] > 0.0)
                Add("negative-current", "dc_max_negative_current must not be positive, the firmware would stop with DC_BUS_OVER_REGEN_CURRENT at idle.");

            if (brakeOn)
            {
                if (values[MaxRegen] < 0.0)
                    Add("regen-negative", "max_regen_current must not be negative, the brake resistor would carry current at idle.");
                if (!(-values[MaxNegative] > values[MaxRegen]))
                    Add("regen-magnitude", "dc_max_negative_current must be greater in magnitude than max_regen_current.");
            }
            return found;
        }

        /// <summary>Things to know that do not stand in the way.</summary>
        public static IReadOnlyList<string> Notes(IReadOnlyDictionary<string, double> values, IEnumerable<string> changedPaths)
        {
            var notes = new List<string>();
            var changed = new HashSet<string>(changedPaths, StringComparer.Ordinal);
            if (changed.Contains(EnableBrake))
                notes.Add("enable_brake_resistor takes effect only after a reboot. Enabling it without a connected brake resistor can break the power supply.");
            if (changed.Contains(EnableRamp) || changed.Contains(RampStart) || changed.Contains(RampEnd))
                notes.Add("The firmware interface marks the overvoltage ramp as experimental.");
            if (values[EnableRamp] != 0.0 && values[RampEnd] > values[OverVoltage])
                notes.Add("dc_bus_overvoltage_ramp_end is above dc_bus_overvoltage_trip_level: the firmware trips before the ramp reaches full duty.");
            return notes;
        }

        /// <summary>
        /// The values go out one at a time, so each step is a state of its own: raising both trip levels the
        /// wrong way round would trip in between. Finds an order in which no step adds a problem.
        /// </summary>
        public static bool TryOrder(IReadOnlyDictionary<string, double> current, IReadOnlyList<PowerBrakeChange> changes,
                                    double busVoltage, out IReadOnlyList<PowerBrakeChange> ordered)
        {
            var state = new Dictionary<string, double>(current.ToDictionary(p => p.Key, p => p.Value), StringComparer.Ordinal);
            var remaining = changes.ToList();
            var order = new List<PowerBrakeChange>();

            while (remaining.Count > 0)
            {
                var before = new HashSet<string>(Check(state, busVoltage).Select(v => v.Id), StringComparer.Ordinal);
                PowerBrakeChange next = null;
                foreach (var change in remaining)
                {
                    var trial = new Dictionary<string, double>(state, StringComparer.Ordinal) { [change.Parameter.Path] = change.NewValue };
                    if (Check(trial, busVoltage).All(v => before.Contains(v.Id)))
                    {
                        next = change;
                        break;
                    }
                }
                if (next == null)
                {
                    ordered = null;
                    return false;
                }
                state[next.Parameter.Path] = next.NewValue;
                order.Add(next);
                remaining.Remove(next);
            }
            ordered = order;
            return true;
        }
    }

    public sealed class PowerBrakeChange
    {
        internal PowerBrakeChange(ParameterDefinition parameter, string oldText, string commandValue, double newValue)
        {
            Parameter    = parameter;
            OldText      = oldText;
            CommandValue = commandValue;
            NewValue     = newValue;
        }

        public ParameterDefinition Parameter { get; }
        /// <summary>The value as shown when the change was reviewed.</summary>
        public string OldText      { get; }
        /// <summary>What goes on the line, which is the float32 the firmware will store.</summary>
        public string CommandValue { get; }
        public double NewValue     { get; }
        public string NewText => OdriveParameterFormat.Format(Parameter, CommandValue, whole: true).Text;
    }

    /// <summary>The checked outcome of the selected inputs. Nothing can be applied without one.</summary>
    public sealed class PowerBrakeReview
    {
        internal PowerBrakeReview(IReadOnlyList<string> errors, IReadOnlyList<PowerBrakeChange> changes, IReadOnlyList<string> unchanged,
                                  IReadOnlyList<string> notes, IReadOnlyDictionary<string, string> reviewedReplies,
                                  IReadOnlyList<ParameterDefinition> group, int connectGeneration, StopTicket lastStop,
                                  FirmwareCapabilityStatus capabilityStatus = null)
        {
            Errors = errors;
            Changes = changes;
            Unchanged = unchanged;
            Notes = notes;
            ReviewedReplies = reviewedReplies;
            Group = group;
            ConnectGeneration = connectGeneration;
            LastStop = lastStop;
            CapabilityStatus = capabilityStatus;
        }

        public IReadOnlyList<string> Errors { get; }
        /// <summary>In the order they will be written.</summary>
        public IReadOnlyList<PowerBrakeChange> Changes { get; }
        /// <summary>Selected, but already at that value.</summary>
        public IReadOnlyList<string> Unchanged { get; }
        public IReadOnlyList<string> Notes { get; }
        public bool CanApply => Errors.Count == 0 && Changes.Count > 0;

        internal IReadOnlyDictionary<string, string> ReviewedReplies { get; }
        internal IReadOnlyList<ParameterDefinition> Group { get; }
        internal int ConnectGeneration { get; }
        internal StopTicket LastStop { get; }
        /// <summary>The state of the firmware check this review rests on. If it is replaced, this review counts for
        /// nothing any more, whatever the new state says. Null where no check is in use.</summary>
        internal FirmwareCapabilityStatus CapabilityStatus { get; }
        /// <summary>The declaration in that state, for all that follows from it. Null if there was none.</summary>
        internal FirmwareCapabilities Capabilities => CapabilityStatus?.Capabilities;
    }

    public static class OdrivePowerBrakeEditor
    {
        public const string BusVoltagePath = "vbus_voltage";

        /// <summary>The only parameters this editor can touch.</summary>
        public static IReadOnlyList<ParameterDefinition> EditableParameters(OdriveParameterCatalog catalog)
            => catalog.Parameters.Where(p => p.Editable).ToList();

        /// <summary>
        /// Checks the selected inputs against what was last read. All values of the group must have been read
        /// on this connection and be clear, because the relations between them cannot be judged otherwise.
        /// A declaration of the firmware counts only for the connection it was made on. While the check of this
        /// connection runs there is no review: what it finds decides what counts as exact. Sends nothing.
        /// </summary>
        public static PowerBrakeReview Review(OdriveParameterCatalog catalog, Func<ParameterDefinition, ParameterReading> reading,
                                              SerialStatus status, IReadOnlyDictionary<string, string> selectedInputs,
                                              FirmwareCapabilityStatus capabilityStatus = null)
        {
            bool checkHasEnded = capabilityStatus == null
                || (capabilityStatus.State != FirmwareCapabilityState.Unknown && capabilityStatus.ConnectGeneration == status.ConnectGeneration);
            var capabilities = checkHasEnded && capabilityStatus?.Capabilities?.BelongsTo(status) == true ? capabilityStatus.Capabilities : null;

            var group = EditableParameters(catalog);
            var errors = new List<string>();
            var changes = new List<PowerBrakeChange>();
            var unchanged = new List<string>();
            var replies = new Dictionary<string, string>(StringComparer.Ordinal);
            var current = new Dictionary<string, double>(StringComparer.Ordinal);

            if (!checkHasEnded) errors.Add("The firmware check has not ended on this connection, review again when it has.");
            if (status.StopLatched) errors.Add("The software stop is set, press Clear stop first.");
            if (selectedInputs.Count == 0) errors.Add("Select at least one field.");
            foreach (var path in selectedInputs.Keys)
                if (group.All(p => p.Path != path)) errors.Add(path + " cannot be edited here.");

            foreach (var parameter in group)
            {
                var shown = reading(parameter);
                if (shown.State != ParameterReadState.Current)
                    errors.Add(parameter.Path + ": no current value, press Refresh first.");
                else if (!OdriveConfigValue.TryParseExactReply(parameter, shown.RawValue, capabilities, out double value))
                    errors.Add(parameter.Path + ": the current value is unclear (" + shown.Text + "). The firmware has not declared exact values on this connection, so it may be rounded or cut.");
                else
                {
                    replies[parameter.Path] = shown.RawValue;
                    current[parameter.Path] = value;
                }
            }

            double busVoltage = double.NaN;
            var bus = catalog.Find(BusVoltagePath);
            var busReading = reading(bus);
            if (busReading.State != ParameterReadState.Current
                || !OdriveConfigValue.TryParseBusVoltage(busReading.RawValue, out busVoltage))
            {
                busVoltage = double.NaN;
                errors.Add("The measured bus voltage is not available.");
            }

            var final = new Dictionary<string, double>(current, StringComparer.Ordinal);
            foreach (var parameter in group)
            {
                if (!selectedInputs.TryGetValue(parameter.Path, out var text)) continue;
                if (!OdriveConfigValue.TryParseInput(parameter, text, out var command, out double newValue, out var error))
                {
                    errors.Add(parameter.Path + ": " + error + ".");
                    continue;
                }
                final[parameter.Path] = newValue;
                if (current.TryGetValue(parameter.Path, out double old) && old == newValue)
                    unchanged.Add(parameter.Path);
                else
                    changes.Add(new PowerBrakeChange(parameter, reading(parameter).Text, command, newValue));
            }

            var notes = new List<string>();
            IReadOnlyList<PowerBrakeChange> ordered = changes;
            if (errors.Count == 0)
            {
                foreach (var violation in OdrivePowerBrakeRules.Check(final, busVoltage)) errors.Add(violation.Message);
                if (changes.Count == 0)
                    errors.Add("Every selected field already has this value, there is nothing to apply.");
                else if (errors.Count == 0 && !OdrivePowerBrakeRules.TryOrder(current, changes, busVoltage, out ordered))
                    errors.Add("These values cannot be written one after the other without an invalid state in between.");
                if (errors.Count == 0) notes.AddRange(OdrivePowerBrakeRules.Notes(final, changes.Select(c => c.Parameter.Path)));
            }

            return new PowerBrakeReview(errors, ordered ?? changes, unchanged, notes, replies, group, status.ConnectGeneration, status.LastStop, capabilityStatus);
        }
    }
}

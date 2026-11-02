using System.Globalization;

namespace OdriveWheel.SimHubPlugin
{
    public sealed class FormattedValue
    {
        public FormattedValue(string text, StatusSeverity severity = StatusSeverity.Neutral, bool isValid = true, bool mayBeCutOff = false)
        {
            Text        = text;
            Severity    = severity;
            IsValid     = isValid;
            MayBeCutOff = mayBeCutOff;
        }

        public string         Text     { get; }
        public StatusSeverity Severity { get; }
        /// <summary>False if the reply is not a value of the expected type.</summary>
        public bool           IsValid  { get; }
        /// <summary>The number itself may be wrong, not just rounded.</summary>
        public bool           MayBeCutOff { get; }
    }

    /// <summary>Turns a reply into the text the window shows. Never guesses: a reply that does not
    /// fit the type, or a number the firmware may have cut, says so instead of looking like a value.</summary>
    public static class OdriveParameterFormat
    {
        private const string MayBeCutOff = "may be cut off";

        public static FormattedValue Format(ParameterDefinition definition, string value)
        {
            switch (definition.Type)
            {
                case ParameterType.Bitmask:   return FormatBitmask(definition, value);
                case ParameterType.AxisState: return FormatAxisState(value);
                case ParameterType.Enum:      return FormatEnum(definition, value);
                case ParameterType.Boolean:   return FormatBoolean(value);
                case ParameterType.Integer:   return FormatInteger(definition, value);
                case ParameterType.Float:     return FormatFloat(definition, value);
                default:                      return new FormattedValue(WithUnit(definition, value));
            }
        }

        private static FormattedValue FormatBitmask(ParameterDefinition definition, string value)
        {
            if (value == "0") return new FormattedValue("0 (no error)");
            if (definition.ErrorKind == ParameterErrorKind.None) return new FormattedValue(value);
            if (!ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out ulong mask))
                return Unexpected(value);

            // A cut-off number decodes to bits that are not set.
            if (FillsTheReply(definition, value))
                return new FormattedValue(value + " (" + MayBeCutOff + ", not decoded)", StatusSeverity.Danger, mayBeCutOff: true);
            if (mask > uint.MaxValue)
                return new FormattedValue(value + " (above 32 bits, not decoded)", StatusSeverity.Danger);
            return new FormattedValue(OdriveErrorDecoder.Decode(definition.ErrorKind, (uint)mask), StatusSeverity.Danger);
        }

        private static FormattedValue FormatAxisState(string value)
        {
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int state)
                ? new FormattedValue(OdriveErrorDecoder.DecodeAxisState(state))
                : Unexpected(value);
        }

        private static FormattedValue FormatEnum(ParameterDefinition definition, string value)
        {
            if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long number))
                return Unexpected(value);
            return new FormattedValue(number + " — " + (definition.EnumValues.TryGetValue(number, out var name) ? name : "?"));
        }

        private static FormattedValue FormatBoolean(string value)
        {
            switch (value)
            {
                case "0": return new FormattedValue("0 (false)");
                case "1": return new FormattedValue("1 (true)");
                default:  return Unexpected(value);
            }
        }

        private static FormattedValue FormatInteger(ParameterDefinition definition, string value)
        {
            bool isNumber = long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _)
                         || ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out _);
            if (!isNumber) return Unexpected(value);
            return WithCutOffNote(definition, value, FillsTheReply(definition, value));
        }

        private static FormattedValue FormatFloat(ParameterDefinition definition, string value)
        {
            if (IsInfinityOrNan(value)) return new FormattedValue(WithUnit(definition, value));
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                return Unexpected(value);

            // With a '.' in the reply only decimals are gone, without one the number itself is wrong.
            return WithCutOffNote(definition, value, FillsTheReply(definition, value) && value.IndexOf('.') < 0);
        }

        private static FormattedValue WithCutOffNote(ParameterDefinition definition, string value, bool mayBeCutOff)
        {
            return mayBeCutOff
                ? new FormattedValue(WithUnit(definition, value) + " (" + MayBeCutOff + ")", StatusSeverity.Warning, mayBeCutOff: true)
                : new FormattedValue(WithUnit(definition, value));
        }

        private static bool FillsTheReply(ParameterDefinition definition, string value)
            => definition.Protocol == FieldProtocol.Odrive && value.Length >= OdriveProtocol.OdriveReadLimit;

        // The way printf writes them.
        private static bool IsInfinityOrNan(string value)
            => value == "inf" || value == "-inf" || value == "nan" || value == "-nan";

        private static string WithUnit(ParameterDefinition definition, string value)
            => string.IsNullOrEmpty(definition.Unit) ? value : value + " " + definition.Unit;

        private static FormattedValue Unexpected(string value)
            => new FormattedValue("unexpected reply (" + value + ")", StatusSeverity.Warning, isValid: false);
    }
}

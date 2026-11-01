using System.Globalization;

namespace OdriveWheel.SimHubPlugin
{
    public enum StatusSeverity
    {
        Neutral,
        Ok,
        Warning,
        Danger,
    }

    public sealed class StatusLine
    {
        public StatusLine(string text, StatusSeverity severity)
        {
            Text     = text;
            Severity = severity;
        }

        public string         Text     { get; }
        public StatusSeverity Severity { get; }
    }

    /// <summary>The wording of connection and stop state, shared by panel and window.</summary>
    public static class OdriveStatusText
    {
        public const string StopDisclaimer =
            "Software stop only, not a hardware E-stop: it needs a working USB link, and the motor coasts "
          + "instead of braking. Keep the power cut-off within reach.";

        private const int StopOverdueMs = 500;
        private const int AxisStateIdle = 1;

        public static StatusLine Connection(SerialStatus status)
        {
            var port = string.IsNullOrEmpty(status.PortName) ? "COM port" : status.PortName;
            switch (status.State)
            {
                case SerialConnectionState.Connected:
                    return new StatusLine(port + " connected", StatusSeverity.Ok);
                case SerialConnectionState.Connecting:
                    return new StatusLine("connecting to " + port + "…", StatusSeverity.Neutral);
                case SerialConnectionState.Busy:
                    return new StatusLine(port + " is held by another application", StatusSeverity.Warning);
                case SerialConnectionState.Released:
                    return new StatusLine(port + " released for other applications", StatusSeverity.Warning);
                case SerialConnectionState.Searching:
                    return new StatusLine("searching for the board…", StatusSeverity.Warning);
                default:
                    return new StatusLine("not running", StatusSeverity.Neutral);
            }
        }

        /// <param name="hidAxisState">Current axis state from the HID stream, null without fresh reports.</param>
        public static StatusLine Stop(StopTicket ticket, bool latched, int? hidAxisState)
        {
            if (ticket == null)
                return new StatusLine(latched ? "Commands that can start the motor are blocked." : "", StatusSeverity.Neutral);

            bool hidIdle = hidAxisState == AxisStateIdle;
            switch (ticket.State)
            {
                case StopState.Requested:
                    return ticket.AgeMs > StopOverdueMs
                        ? new StatusLine("STOP NOT SENT YET after " + Ms(ticket.AgeMs) + ". Use the power cut-off.", StatusSeverity.Danger)
                        : new StatusLine("Stop requested…", StatusSeverity.Warning);

                case StopState.Written:
                    return ticket.AgeMs > StopOverdueMs && !hidIdle
                        ? new StatusLine("Stop sent, IDLE NOT CONFIRMED after " + Ms(ticket.AgeMs) + ". Use the power cut-off.", StatusSeverity.Danger)
                        : new StatusLine("Stop sent, checking…", StatusSeverity.Warning);

                case StopState.Confirmed:
                    return new StatusLine("IDLE confirmed: " + ticket.Detail + " (sent after " + Ms(ticket.WrittenAfterMs) + ").", StatusSeverity.Ok);

                case StopState.Unconfirmed:
                    return hidIdle
                        ? new StatusLine("IDLE confirmed: HID reports IDLE, serial read-back failed.", StatusSeverity.Ok)
                        : new StatusLine("STOP SENT BUT IDLE NOT CONFIRMED: " + ticket.Detail + ". Use the power cut-off.", StatusSeverity.Danger);

                default:
                    return hidIdle
                        ? new StatusLine("Stop not delivered (" + ticket.Detail + "), but HID reports IDLE.", StatusSeverity.Warning)
                        : new StatusLine("STOP NOT DELIVERED: " + ticket.Detail + ". Use the power cut-off.", StatusSeverity.Danger);
            }
        }

        /// <summary>A read result for the status list. A failed read never looks like "no error".</summary>
        public static string ReadValue(ParameterDefinition definition, ParameterResult result)
        {
            switch (result.Status)
            {
                case ParameterStatus.Ok:
                    return FormatValue(definition, result.Value);
                case ParameterStatus.Rejected:
                    return "not readable (" + result.Detail + ")";
                case ParameterStatus.Timeout:
                    return "no reply";
                default:
                    return "—";
            }
        }

        private static string FormatValue(ParameterDefinition definition, string value)
        {
            switch (definition.Type)
            {
                case ParameterType.Bitmask:
                    return value == "0" ? "0 (no error)" : value;
                case ParameterType.AxisState:
                    return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int state)
                        ? OdriveErrorDecoder.DecodeAxisState(state)
                        : value;
                default:
                    return string.IsNullOrEmpty(definition.Unit) ? value : value + " " + definition.Unit;
            }
        }

        private static string Ms(long milliseconds) => milliseconds.ToString(CultureInfo.InvariantCulture) + " ms";
    }
}

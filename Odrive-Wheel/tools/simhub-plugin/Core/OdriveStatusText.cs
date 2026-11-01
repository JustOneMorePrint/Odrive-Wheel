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
        private const int AxisStateUndefined = 0;
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

        public static StatusLine Stop(SerialStatus status, int? hidAxisState)
        {
            var ticket = status.LastStop;
            bool sameConnection = ticket == null || ticket.ConnectGeneration == status.ConnectGeneration;
            return Stop(ticket, status.StopLatched, hidAxisState, sameConnection);
        }

        /// <summary>
        /// What a finished stop achieved belongs to its moment, what the axis does now can have
        /// changed since. Green means both: the stop still stands, and the axis is IDLE now.
        /// </summary>
        /// <param name="latched">False once the stop has been cleared: it is history then.</param>
        /// <param name="hidAxisState">Current axis state from the HID stream, null without fresh reports.</param>
        /// <param name="sameConnection">False if the port has been reconnected since the stop.
        /// The board may have rebooted, which can arm the motor again.</param>
        public static StatusLine Stop(StopTicket ticket, bool latched, int? hidAxisState, bool sameConnection = true)
        {
            if (ticket == null)
                return new StatusLine(latched ? "Commands that can start the motor are blocked." : "", StatusSeverity.Neutral);

            // State 0 is a firmware without the field or a board still booting: no information.
            int? axisNow = hidAxisState == AxisStateUndefined ? null : hidAxisState;
            bool idleNow = axisNow == AxisStateIdle;

            switch (ticket.State)
            {
                case StopState.Requested:
                    return ticket.AgeMs > StopOverdueMs
                        ? new StatusLine("STOP NOT SENT YET after " + Ms(ticket.AgeMs) + ". Use the power cut-off.", StatusSeverity.Danger)
                        : new StatusLine("Stop requested…", StatusSeverity.Warning);

                case StopState.Written:
                    return ticket.AgeMs > StopOverdueMs && !idleNow
                        ? new StatusLine("Stop sent, IDLE NOT CONFIRMED after " + Ms(ticket.AgeMs) + ". Use the power cut-off.", StatusSeverity.Danger)
                        : new StatusLine("Stop sent, checking…", StatusSeverity.Warning);
            }

            if (!latched)
                return new StatusLine("Last software stop " + PastOutcome(ticket) + ", cleared since. Says nothing about the axis now.", StatusSeverity.Neutral);

            switch (ticket.State)
            {
                case StopState.Confirmed:
                    if (axisNow.HasValue && !idleNow)
                        return new StatusLine("AXIS IS NOT IDLE NOW: HID reports " + OdriveErrorDecoder.DecodeAxisState(axisNow.Value)
                                            + ". The earlier stop no longer holds. Use the power cut-off if this is not intended.", StatusSeverity.Danger);
                    if (!sameConnection)
                        return new StatusLine("The board reconnected since the stop, it may have rebooted and armed the motor. "
                                            + (idleNow ? "HID reports IDLE at the moment." : "There is no current axis state."), StatusSeverity.Warning);
                    return idleNow
                        ? new StatusLine("IDLE confirmed: " + ticket.Detail + " (sent after " + Ms(ticket.WrittenAfterMs) + ").", StatusSeverity.Ok)
                        : new StatusLine("Stop was confirmed (" + ticket.Detail + "), but there is no current axis state from HID.", StatusSeverity.Warning);

                case StopState.Unconfirmed:
                    if (idleNow && !sameConnection)
                        return new StatusLine("The board reconnected since the stop. HID reports IDLE at the moment.", StatusSeverity.Warning);
                    return idleNow
                        ? new StatusLine("IDLE confirmed: HID reports IDLE, serial read-back failed.", StatusSeverity.Ok)
                        : new StatusLine("STOP SENT BUT IDLE NOT CONFIRMED: " + ticket.Detail + ". Use the power cut-off.", StatusSeverity.Danger);

                default:
                    return idleNow
                        ? new StatusLine("Stop not delivered (" + ticket.Detail + "), but HID reports IDLE.", StatusSeverity.Warning)
                        : new StatusLine("STOP NOT DELIVERED: " + ticket.Detail + ". Use the power cut-off.", StatusSeverity.Danger);
            }
        }

        private static string PastOutcome(StopTicket ticket)
        {
            switch (ticket.State)
            {
                case StopState.Confirmed:   return "was confirmed";
                case StopState.Unconfirmed: return "was sent but not confirmed";
                default:                    return "was not delivered";
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

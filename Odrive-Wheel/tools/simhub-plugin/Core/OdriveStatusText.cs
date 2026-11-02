using System.Globalization;
using System.Linq;
using System.Text;

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

        /// <summary>What the firmware on this connection has declared about its replies.</summary>
        public static StatusLine Capabilities(FirmwareCapabilityStatus status, bool connected)
        {
            const string title = "Firmware replies: ";
            const string careful = "Long numbers may be cut off, and values that may be rounded are refused for editing.";

            switch (status.State)
            {
                case FirmwareCapabilityState.Declared:
                    if (status.Capabilities.FloatsAreExact)
                        return new StatusLine(title + "declared complete and exact on this connection.", StatusSeverity.Ok);
                    return new StatusLine(title + "the firmware declares "
                                        + (status.Capabilities.AsciiFull ? "complete numbers, but no exact floats. " : "no complete numbers. ") + careful, StatusSeverity.Warning);
                case FirmwareCapabilityState.NotDeclared:
                    return new StatusLine(title + "nothing declared (" + status.Detail + "). " + careful, StatusSeverity.Warning);
                default:
                    return new StatusLine(title + (connected ? "checking…" : "no connection."), StatusSeverity.Neutral);
            }
        }

        /// <summary>A read result for the parameter lists. A failed read never looks like "no error".</summary>
        public static string ReadValue(ParameterDefinition definition, ParameterResult result)
        {
            switch (result.Status)
            {
                case ParameterStatus.Ok:
                    return OdriveParameterFormat.Format(definition, result.Value).Text;
                case ParameterStatus.Rejected:
                    return IsUnsupported(result.Detail)
                        ? "not supported by this firmware (" + result.Detail + ")"
                        : "not readable (" + result.Detail + ")";
                case ParameterStatus.Timeout:
                    return "no reply";
                default:
                    return "—";
            }
        }

        private static bool IsUnsupported(string detail) => detail == "invalid property" || detail == "NOT_FOUND";

        /// <summary>The line above a parameter list after a refresh. Whatever was not read is named.</summary>
        public static string Refresh(ParameterRefreshResult result, System.DateTime now)
        {
            int done = result.Succeeded + result.Failed;
            if (result.AbortReason != null)
                return "Aborted after " + done + " of " + result.Requested + ": " + result.AbortReason + ". The rest was not read.";

            var at = "Read at " + now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + ": " + result.Succeeded + " ok";
            return result.Failed == 0 ? at + "." : at + ", " + result.Failed + " failed.";
        }

        /// <summary>The summary to read before applying.</summary>
        public static string RamReview(PowerBrakeReview review)
        {
            var text = new StringBuilder();
            if (review.Errors.Count > 0)
            {
                text.AppendLine("Cannot be applied:");
                foreach (var error in review.Errors) text.AppendLine("  " + error);
                return text.ToString().TrimEnd();
            }

            text.AppendLine("Will be written to RAM, in this order:");
            foreach (var change in review.Changes)
                text.AppendLine("  " + change.Parameter.Path + ": " + change.OldText + " → " + change.NewText);
            foreach (var path in review.Unchanged)
                text.AppendLine("  " + path + ": already has this value, not written");
            foreach (var note in review.Notes)
                text.AppendLine("Note: " + note);
            text.Append("Nothing is saved to the board. Both axes are checked for IDLE right before each write, which does not lock out other command sources.");
            return text.ToString();
        }

        /// <summary>The outcome of one write, in the words the window uses.</summary>
        public static string RamWrite(RamWriteResult write)
        {
            string outcome;
            switch (write.Outcome)
            {
                case RamWriteOutcome.VerifiedByReadback: outcome = "verified by readback"; break;
                case RamWriteOutcome.NotVerified:        outcome = "sent, not verified"; break;
                case RamWriteOutcome.Sent:               outcome = "sent"; break;
                case RamWriteOutcome.ReadbackDiffers:    outcome = "sent, readback differs"; break;
                case RamWriteOutcome.RefusedByFirmware:  outcome = "refused by the firmware"; break;
                default:                                 outcome = "not sent"; break;
            }
            return string.IsNullOrEmpty(write.Detail) ? outcome : outcome + " (" + write.Detail + ")";
        }

        /// <summary>What happened, with everything that already reached the board named.</summary>
        public static string RamApply(RamApplyResult result)
        {
            var text = new StringBuilder();
            var wentOut = result.Writes.Where(w => w.WentOut).ToList();

            if (result.AbortReason != null)
                text.AppendLine((wentOut.Count == 0 ? "Nothing was written: " : "Aborted: ") + result.AbortReason + ". No retry and no writing back.");
            foreach (var write in result.Writes)
                text.AppendLine("  " + write.Change.Parameter.Path + " = " + write.Change.NewText + ": " + RamWrite(write));
            if (result.AbortReason != null && wentOut.Count > 0)
                text.AppendLine("Already transferred, and still in place: " + string.Join(", ", wentOut.Select(w => w.Change.Parameter.Path)) + ".");
            if (wentOut.Count > 0)
                text.Append("In RAM only, nothing was saved to the board: a reboot restores the saved values.");
            return text.ToString().TrimEnd();
        }

        private static string Ms(long milliseconds) => milliseconds.ToString(CultureInfo.InvariantCulture) + " ms";
    }
}

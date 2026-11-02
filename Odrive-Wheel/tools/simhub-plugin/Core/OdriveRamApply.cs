using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OdriveWheel.SimHubPlugin
{
    public enum RamWriteOutcome
    {
        /// <summary>The line never went out.</summary>
        NotSent,
        /// <summary>The firmware answered with an error.</summary>
        RefusedByFirmware,
        /// <summary>The line went out and nothing came back, which is all a write ever says. No readback yet.</summary>
        Sent,
        /// <summary>The line went out, but the value could not be confirmed. The board may or may not hold it.</summary>
        NotVerified,
        VerifiedByReadback,
        /// <summary>The line went out and the board reports another value.</summary>
        ReadbackDiffers,
    }

    public sealed class RamWriteResult
    {
        internal RamWriteResult(PowerBrakeChange change)
        {
            Change  = change;
            Outcome = RamWriteOutcome.NotSent;
        }

        public PowerBrakeChange Change { get; }
        public RamWriteOutcome  Outcome { get; internal set; }
        public string           Detail  { get; internal set; }

        /// <summary>The board may hold the new value now.</summary>
        public bool WentOut => Outcome != RamWriteOutcome.NotSent && Outcome != RamWriteOutcome.RefusedByFirmware;
    }

    public sealed class RamApplyResult
    {
        internal RamApplyResult(IReadOnlyList<RamWriteResult> writes, string abortReason)
        {
            Writes = writes;
            AbortReason = abortReason;
        }

        public IReadOnlyList<RamWriteResult> Writes { get; }
        /// <summary>Why it ended early, null if every change was attempted.</summary>
        public string AbortReason { get; }
        public bool AllVerified => AbortReason == null && Writes.All(w => w.Outcome == RamWriteOutcome.VerifiedByReadback);
    }

    /// <summary>
    /// Writes reviewed Power / Brake changes to the RAM of the board, one at a time. Nothing is saved to the
    /// board: a reboot restores the saved values. It only ever sends "w" for an editable parameter, and reads.
    ///
    /// The state checks run right before each write, in the plugin. They lock nothing: another source can
    /// still change the axis state between the check and the write. They exist to catch the usual case and
    /// are no hardware safeguard. A failure is reported and never repaired: no retry, no writing back.
    ///
    /// Every command is bound to the connection and the stop state of the review, so the serial worker holds
    /// it back if either changed, however late. The checks here say why, and judge what comes back.
    /// </summary>
    public sealed class OdriveRamApplier
    {
        private const int AxisStateUndefined = 0;
        private const int AxisStateIdle = 1;

        private static readonly string[] Axes = { "axis0", "axis1" };

        private readonly IOdriveParameters _parameters;
        private readonly Func<SerialStatus> _getStatus;
        private readonly Func<int?> _hidAxisState;
        private readonly Action<string> _log;

        public OdriveRamApplier(IOdriveParameters parameters, Func<SerialStatus> getStatus, Func<int?> hidAxisState = null, Action<string> log = null)
        {
            _parameters   = parameters ?? throw new ArgumentNullException(nameof(parameters));
            _getStatus    = getStatus ?? throw new ArgumentNullException(nameof(getStatus));
            _hidAxisState = hidAxisState ?? (() => null);
            _log          = log ?? (_ => { });
        }

        public async Task<RamApplyResult> ApplyAsync(PowerBrakeReview review, CancellationToken cancellation)
        {
            var writes = review.Changes.Select(c => new RamWriteResult(c)).ToList();
            if (!review.CanApply)
                return new RamApplyResult(writes, "the review found problems");
            if (review.Changes.Any(c => !c.Parameter.Editable))
                return new RamApplyResult(writes, "the changes contain a parameter that cannot be edited");

            var blocked = WhyBlocked(review, cancellation);
            if (blocked != null) return new RamApplyResult(writes, blocked);

            var context = new SerialOperationContext(review.ConnectGeneration, review.LastStop);
            var (order, refusal) = await CheckFreshValues(review, context, cancellation).ConfigureAwait(false);
            if (refusal != null) return new RamApplyResult(writes, refusal);

            var byChange = writes.ToDictionary(w => w.Change);
            foreach (var change in order)
            {
                var write = byChange[change];

                var why = WhyBlocked(review, cancellation) ?? await WhyNotIdle(context, cancellation).ConfigureAwait(false);
                // The checks take time, so ask once more right before the line goes out.
                why = why ?? WhyBlocked(review, cancellation);
                if (why != null) return new RamApplyResult(writes, why);

                var abort = await WriteOne(write, review, context, cancellation).ConfigureAwait(false);
                _log("apply to RAM " + change.Parameter.Path + " = " + change.CommandValue + ": " + write.Outcome
                     + (write.Detail != null ? " (" + write.Detail + ")" : ""));
                if (abort != null) return new RamApplyResult(writes, abort);
            }
            return new RamApplyResult(writes, null);
        }

        // What stops every further write: a stop, a new connection, a released port, a closing window.
        private string WhyBlocked(PowerBrakeReview review, CancellationToken cancellation)
        {
            if (cancellation.IsCancellationRequested) return "cancelled";
            var status = _getStatus();
            if (status.LastStop != review.LastStop || status.StopLatched)
                return "software stop";
            if (!status.IsConnected) return OdriveStatusText.Connection(status).Text;
            if (status.ConnectGeneration != review.ConnectGeneration) return "the connection changed, the board may have rebooted";
            return null;
        }

        // The review is based on what was read earlier, this is what the board says now.
        private async Task<(IReadOnlyList<PowerBrakeChange> Order, string Refusal)> CheckFreshValues(PowerBrakeReview review, SerialOperationContext context, CancellationToken cancellation)
        {
            var fresh = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var parameter in review.Group)
            {
                var read = await _parameters.ReadAsync(parameter.Path, parameter.Protocol, CommandPriority.Interactive, cancellation, context).ConfigureAwait(false);
                if (!read.IsOk) return (null, parameter.Path + " could not be read again (" + (read.Detail ?? read.Status.ToString()) + ")");
                if (read.Value != review.ReviewedReplies[parameter.Path])
                    return (null, parameter.Path + " changed since the review, it now reads " + read.Value);
                if (!OdriveConfigValue.TryParseExactReply(parameter, read.Value, out double value))
                    return (null, parameter.Path + " is unclear (" + read.Value + ")");
                fresh[parameter.Path] = value;
            }

            var bus = await _parameters.ReadAsync(OdrivePowerBrakeEditor.BusVoltagePath, FieldProtocol.Odrive, CommandPriority.Interactive, cancellation, context).ConfigureAwait(false);
            if (!bus.IsOk || !OdriveConfigValue.TryParseBusVoltage(bus.Value, out double busVoltage))
                return (null, "the measured bus voltage could not be read");

            var final = new Dictionary<string, double>(fresh, StringComparer.Ordinal);
            foreach (var change in review.Changes) final[change.Parameter.Path] = change.NewValue;
            var violation = OdrivePowerBrakeRules.Check(final, busVoltage).FirstOrDefault();
            if (violation != null) return (null, violation.Message);

            if (!OdrivePowerBrakeRules.TryOrder(fresh, review.Changes, busVoltage, out var order))
                return (null, "these values cannot be written one after the other without an invalid state in between");
            return (order, null);
        }

        // Null means both axes are IDLE, not armed and have no request waiting. Everything else, a failed read
        // or a reply of the wrong kind included, is a reason not to write.
        private async Task<string> WhyNotIdle(SerialOperationContext context, CancellationToken cancellation)
        {
            foreach (var axis in Axes)
            {
                var state = await ReadExact(axis + ".current_state", AxisStateIdle.ToString(CultureInfo.InvariantCulture), context, cancellation).ConfigureAwait(false);
                if (state != null) return state;
                var armed = await ReadExact(axis + ".motor.is_armed", "0", context, cancellation).ConfigureAwait(false);
                if (armed != null) return armed;
                var requested = await ReadExact(axis + ".requested_state", AxisStateUndefined.ToString(CultureInfo.InvariantCulture), context, cancellation).ConfigureAwait(false);
                if (requested != null) return requested;
            }

            // State 0 is a firmware without the field or a board still booting: no information.
            var hid = _hidAxisState();
            if (hid.HasValue && hid.Value != AxisStateUndefined && hid.Value != AxisStateIdle)
                return "the serial replies say IDLE, but HID reports " + OdriveErrorDecoder.DecodeAxisState(hid.Value);
            return null;
        }

        private async Task<string> ReadExact(string path, string expected, SerialOperationContext context, CancellationToken cancellation)
        {
            var read = await _parameters.ReadAsync(path, FieldProtocol.Odrive, CommandPriority.Interactive, cancellation, context).ConfigureAwait(false);
            if (!read.IsOk) return path + " could not be read (" + (read.Detail ?? read.Status.ToString()) + ")";
            return read.Value == expected ? null : path + " is " + read.Value + ", it must be " + expected;
        }

        // Returns why the rest must not go out, null to go on. Each next step builds on this value,
        // so anything short of a confirmed one ends the run.
        private async Task<string> WriteOne(RamWriteResult write, PowerBrakeReview review, SerialOperationContext context, CancellationToken cancellation)
        {
            var parameter = write.Change.Parameter;
            var sent = await _parameters.WriteAsync(parameter.Path, write.Change.CommandValue, parameter.Protocol,
                                                    CommandPriority.Interactive, cancellation, context).ConfigureAwait(false);
            switch (sent.Status)
            {
                case ParameterStatus.Ok:
                    write.Outcome = RamWriteOutcome.Sent;
                    break;
                case ParameterStatus.Rejected:
                    write.Outcome = RamWriteOutcome.RefusedByFirmware;
                    write.Detail  = sent.Detail;
                    return parameter.Path + " was refused by the firmware (" + sent.Detail + ")";
                default:
                    write.Outcome = sent.LineWritten ? RamWriteOutcome.NotVerified : RamWriteOutcome.NotSent;
                    write.Detail  = sent.Detail;
                    return WhyBlocked(review, CancellationToken.None) ?? sent.Detail ?? sent.Status.ToString();
            }

            var blocked = WhyBlocked(review, cancellation);
            if (blocked != null)
            {
                write.Outcome = RamWriteOutcome.NotVerified;
                write.Detail  = "no readback: " + blocked;
                return blocked;
            }

            var read = await _parameters.ReadAsync(parameter.Path, parameter.Protocol, CommandPriority.Interactive, cancellation, context).ConfigureAwait(false);
            if (!read.IsOk)
            {
                write.Outcome = RamWriteOutcome.NotVerified;
                write.Detail  = "readback failed: " + (read.Detail ?? read.Status.ToString());
                return WhyBlocked(review, CancellationToken.None) ?? parameter.Path + " could not be read back";
            }

            // The reply only speaks for this write if the operation still stands now that it is here.
            // A closing window does not change what the reply says, so that does not count here.
            blocked = WhyBlocked(review, CancellationToken.None);
            if (blocked != null)
            {
                write.Outcome = RamWriteOutcome.NotVerified;
                write.Detail  = "readback not used: " + blocked;
                return blocked;
            }

            switch (OdriveConfigValue.CompareReadback(parameter, write.Change.CommandValue, read.Value, out var detail))
            {
                case ReadbackVerdict.Verified:
                    write.Outcome = RamWriteOutcome.VerifiedByReadback;
                    return null;
                case ReadbackVerdict.NotVerified:
                    write.Outcome = RamWriteOutcome.NotVerified;
                    write.Detail  = detail;
                    return parameter.Path + " could not be confirmed";
                default:
                    write.Outcome = RamWriteOutcome.ReadbackDiffers;
                    write.Detail  = detail;
                    return parameter.Path + " does not hold the written value";
            }
        }
    }
}

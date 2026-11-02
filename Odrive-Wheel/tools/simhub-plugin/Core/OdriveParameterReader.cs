using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OdriveWheel.SimHubPlugin
{
    public enum ParameterReadState
    {
        NotRead,
        /// <summary>Read on the connection that is up now.</summary>
        Current,
        /// <summary>The last read on this connection failed.</summary>
        Failed,
        /// <summary>Read on a connection that is gone. The board may have rebooted since.</summary>
        Outdated,
    }

    public sealed class ParameterReading
    {
        internal ParameterReading(ParameterReadState state, string text, StatusSeverity severity,
                                  DateTime? readAt, int connectGeneration, string rawValue = null)
        {
            State             = state;
            Text              = text;
            Severity          = severity;
            ReadAt            = readAt;
            ConnectGeneration = connectGeneration;
            RawValue          = rawValue;
        }

        public ParameterReadState State { get; }
        public string         Text     { get; }
        public StatusSeverity Severity { get; }
        /// <summary>Local time of the read, null unless the state is Current.</summary>
        public DateTime?      ReadAt   { get; }
        /// <summary>The reply as it came, null unless the state is Current.</summary>
        public string         RawValue { get; }
        internal int ConnectGeneration { get; }
    }

    public sealed class ParameterRefreshResult
    {
        internal ParameterRefreshResult(int requested, int succeeded, int failed, string abortReason)
        {
            Requested   = requested;
            Succeeded   = succeeded;
            Failed      = failed;
            AbortReason = abortReason;
        }

        public int Requested { get; }
        public int Succeeded { get; }
        public int Failed    { get; }
        /// <summary>Why the rest was not read, null if every parameter was attempted.</summary>
        public string AbortReason { get; }
        public int NotReached => Requested - Succeeded - Failed;
    }

    /// <summary>
    /// Reads catalog parameters on request and remembers what came back. A value is bound to the
    /// connection it was read on, so it can never pass for the state of a board that may have
    /// rebooted. This class can only read: it gets nothing but the read delegate. Safe to use
    /// from any thread.
    ///
    /// The text of a value is worked out when it is asked for, with what the firmware of that
    /// connection has declared by then. So a value read before the check ended needs no second read.
    /// </summary>
    public sealed class OdriveParameterReader
    {
        public delegate Task<ParameterResult> ReadParameter(ParameterDefinition parameter, CommandPriority priority,
                                                            CancellationToken cancellation);

        // Each one costs the read timeout plus the wait for the reply stream, so a silent board
        // must not be asked for a whole tab.
        internal const int MaxTimeoutsInARow = 2;

        private const string StopReason = "software stop";

        private static readonly ParameterReading NeverRead =
            new ParameterReading(ParameterReadState.NotRead, "—", StatusSeverity.Neutral, null, -1);

        private readonly ReadParameter _read;
        private readonly Func<SerialStatus> _getStatus;
        private readonly Func<int, FirmwareCapabilities> _capabilitiesFor;
        private readonly ConcurrentDictionary<string, ParameterReading> _readings
            = new ConcurrentDictionary<string, ParameterReading>(StringComparer.Ordinal);
        private int _version;

        /// <param name="capabilitiesFor">What the firmware declared on a connection. Without it replies
        /// stay in doubt, as they do on firmware that declares nothing.</param>
        public OdriveParameterReader(ReadParameter read, Func<SerialStatus> getStatus, Func<int, FirmwareCapabilities> capabilitiesFor = null)
        {
            _read      = read ?? throw new ArgumentNullException(nameof(read));
            _getStatus = getStatus ?? throw new ArgumentNullException(nameof(getStatus));
            _capabilitiesFor = capabilitiesFor ?? (_ => null);
        }

        /// <summary>Changes with every stored result. The connection and a declaration can change a reading too.</summary>
        public int Version => Volatile.Read(ref _version);

        /// <summary>What to show for this parameter right now.</summary>
        public ParameterReading Get(ParameterDefinition parameter)
        {
            if (!_readings.TryGetValue(parameter.Path, out var reading)) return NeverRead;

            var status = _getStatus();
            if (status.IsConnected && reading.ConnectGeneration == status.ConnectGeneration) return WithDeclaration(parameter, reading);

            // A failure belongs to its connection as well, it says nothing about this one.
            return reading.State == ParameterReadState.Current
                ? new ParameterReading(ParameterReadState.Outdated, "outdated (was " + reading.Text + ")",
                                       StatusSeverity.Warning, null, reading.ConnectGeneration)
                : NeverRead;
        }

        // The same firmware answered, so what it declares goes for an earlier reply of this connection as well.
        private ParameterReading WithDeclaration(ParameterDefinition parameter, ParameterReading reading)
        {
            if (reading.State != ParameterReadState.Current) return reading;

            var formatted = OdriveParameterFormat.Format(parameter, reading.RawValue, _capabilitiesFor(reading.ConnectGeneration));
            return formatted.Text == reading.Text
                ? reading
                : new ParameterReading(reading.State, formatted.Text, formatted.Severity, reading.ReadAt, reading.ConnectGeneration, reading.RawValue);
        }

        /// <summary>Forgets a value that was written or may have been: what was read before says nothing now.</summary>
        public void Invalidate(ParameterDefinition parameter)
        {
            _readings.TryRemove(parameter.Path, out _);
            Interlocked.Increment(ref _version);
        }

        /// <summary>Reads the parameters one after the other. Ends early on a software stop, on
        /// cancellation, on a lost connection and if the board stops answering, the result says why.</summary>
        public async Task<ParameterRefreshResult> RefreshAsync(IReadOnlyList<ParameterDefinition> parameters,
                                                               CommandPriority priority, CancellationToken cancellation)
        {
            int succeeded = 0, failed = 0, timeoutsInARow = 0;
            string abortReason = null;
            var stopAtStart = _getStatus().LastStop;

            foreach (var parameter in parameters)
            {
                if (cancellation.IsCancellationRequested)
                {
                    abortReason = "cancelled";
                    break;
                }

                // Taken before the read: if the connection changes during it, the value counts
                // for the old one and shows as outdated rather than as current.
                var status = _getStatus();
                if (StopInterferes(status, stopAtStart))
                {
                    abortReason = StopReason;
                    break;
                }
                if (!status.IsConnected)
                {
                    abortReason = OdriveStatusText.Connection(status).Text;
                    break;
                }

                var result = await _read(parameter, priority, cancellation).ConfigureAwait(false);

                // Nothing was learned, so the previous reading stays as it is.
                if (result.Status == ParameterStatus.Cancelled)
                {
                    abortReason = StopInterferes(_getStatus(), stopAtStart) ? StopReason : result.Detail ?? "cancelled";
                    break;
                }

                timeoutsInARow = result.Status == ParameterStatus.Timeout ? timeoutsInARow + 1 : 0;
                if (Store(parameter, result, status.ConnectGeneration)) succeeded++;
                else failed++;

                if (result.Status == ParameterStatus.Unavailable)
                {
                    abortReason = result.Detail ?? "no connection";
                    break;
                }
                if (timeoutsInARow >= MaxTimeoutsInARow)
                {
                    abortReason = "the board does not answer";
                    break;
                }
            }
            return new ParameterRefreshResult(parameters.Count, succeeded, failed, abortReason);
        }

        // The ticket counts from the moment of the request. StopToken is no use for this: only the
        // worker cancels it, which can be after the running read has already come back cancelled.
        private static bool StopInterferes(SerialStatus status, StopTicket stopAtStart)
            => status.LastStop != stopAtStart || (stopAtStart != null && !stopAtStart.IsFinished);

        private bool Store(ParameterDefinition parameter, ParameterResult result, int connectGeneration)
        {
            bool ok = false;
            ParameterReading reading;
            if (result.IsOk)
            {
                var formatted = OdriveParameterFormat.Format(parameter, result.Value);
                ok = formatted.IsValid;
                reading = ok
                    ? new ParameterReading(ParameterReadState.Current, formatted.Text, formatted.Severity, DateTime.Now, connectGeneration,
                                           result.Value)
                    : new ParameterReading(ParameterReadState.Failed, formatted.Text, formatted.Severity, null, connectGeneration);
            }
            else
            {
                reading = new ParameterReading(ParameterReadState.Failed, OdriveStatusText.ReadValue(parameter, result),
                                               StatusSeverity.Warning, null, connectGeneration);
            }
            _readings[parameter.Path] = reading;
            Interlocked.Increment(ref _version);
            return ok;
        }
    }
}

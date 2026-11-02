using System;
using System.Threading;
using System.Threading.Tasks;

namespace OdriveWheel.SimHubPlugin
{
    /// <summary>
    /// What the firmware on one connection declared through sys.capabilities?, and the only reason for the
    /// plugin to stop doubting what a reply looks like. It belongs to its connection: another connection may
    /// be another firmware, so nothing here is ever valid beyond that. See docs/packages/PKG1.md.
    /// </summary>
    public sealed class FirmwareCapabilities
    {
        public const string CommandPath = "sys.capabilities";
        private const string SchemaVersion = "1";

        internal FirmwareCapabilities(int connectGeneration, bool asciiFull, bool float32RoundTrip)
        {
            ConnectGeneration = connectGeneration;
            AsciiFull         = asciiFull;
            Float32RoundTrip  = float32RoundTrip;
        }

        public int  ConnectGeneration { get; }
        /// <summary>An ODrive read holds the whole number or an error, it is never cut.</summary>
        public bool AsciiFull         { get; }
        /// <summary>A float in an ODrive read parses back to the same float32.</summary>
        public bool Float32RoundTrip  { get; }
        /// <summary>A float in a reply is the value on the board, not something close to it. That takes both.</summary>
        public bool FloatsAreExact => AsciiFull && Float32RoundTrip;

        public bool BelongsTo(SerialStatus status) => status.IsConnected && status.ConnectGeneration == ConnectGeneration;

        /// <summary>Accepts exactly the three fields of schema version 1, each once, separated by one space.
        /// Anything else is no declaration, and <paramref name="problem"/> says why.</summary>
        public static bool TryParse(string reply, int connectGeneration, out FirmwareCapabilities capabilities, out string problem)
        {
            capabilities = null;
            problem = null;

            string version = null, asciiFull = null, float32RoundTrip = null;
            foreach (var field in (reply ?? "").Split(' '))
            {
                // Each field is judged on its own, so nothing after a bad one can make up for it.
                int separator = field.IndexOf('=');
                if (separator < 0) return Refuse("a field without a value: '" + field + "'", out problem);
                var name  = field.Substring(0, separator);
                var value = field.Substring(separator + 1);
                switch (name)
                {
                    case "v":
                        if (version != null) return Refuse("v appears twice", out problem);
                        if (value != SchemaVersion) return Refuse("unknown schema version: " + value, out problem);
                        version = value;
                        break;
                    case "ascii_full":
                        if (asciiFull != null) return Refuse("ascii_full appears twice", out problem);
                        if (!IsFlag(value)) return Refuse("invalid ascii_full: " + value, out problem);
                        asciiFull = value;
                        break;
                    case "float32_rt":
                        if (float32RoundTrip != null) return Refuse("float32_rt appears twice", out problem);
                        if (!IsFlag(value)) return Refuse("invalid float32_rt: " + value, out problem);
                        float32RoundTrip = value;
                        break;
                    default:
                        return Refuse("unexpected field: " + field, out problem);
                }
            }

            if (version == null) return Refuse("no schema version", out problem);
            if (asciiFull == null) return Refuse("no ascii_full", out problem);
            if (float32RoundTrip == null) return Refuse("no float32_rt", out problem);

            capabilities = new FirmwareCapabilities(connectGeneration, asciiFull == "1", float32RoundTrip == "1");
            return true;
        }

        private static bool IsFlag(string value) => value == "0" || value == "1";

        private static bool Refuse(string why, out string problem)
        {
            problem = why;
            return false;
        }
    }

    public enum FirmwareCapabilityState
    {
        /// <summary>No connection, or the check has not ended yet.</summary>
        Unknown,
        /// <summary>The firmware on this connection gave a valid declaration, its flags say what it covers.</summary>
        Declared,
        /// <summary>No valid declaration on this connection: older firmware, no reply or an unusable one.</summary>
        NotDeclared,
    }

    /// <summary>
    /// One state of the check for one connection, safe to read from any thread. Each new state is a new object,
    /// also if it says the same, and the old one knows it was replaced. Whatever rests on a state can hold on to
    /// it, and ends with it through <see cref="Superseded"/>.
    /// </summary>
    public sealed class FirmwareCapabilityStatus
    {
        private readonly CancellationTokenSource _superseded = new CancellationTokenSource();

        internal FirmwareCapabilityStatus(int connectGeneration, FirmwareCapabilityState state, FirmwareCapabilities capabilities, string detail)
        {
            ConnectGeneration = connectGeneration;
            State             = state;
            Capabilities      = capabilities;
            Detail            = detail;
        }

        public int ConnectGeneration { get; }
        public FirmwareCapabilityState State { get; }
        /// <summary>Null unless the state is Declared.</summary>
        public FirmwareCapabilities Capabilities { get; }
        /// <summary>Why it is not declared, null otherwise.</summary>
        public string Detail { get; }

        /// <summary>Cancelled when another state takes the place of this one.</summary>
        public CancellationToken Superseded => _superseded.Token;
        public bool IsSuperseded => _superseded.IsCancellationRequested;

        internal void Supersede() => _superseded.Cancel();
    }

    /// <summary>
    /// Asks the firmware once per connection, when the connection comes up. It only ever reads, and it does
    /// not poll: another try takes <see cref="CheckAgain"/>, unless a software stop ended the read before it
    /// said anything about the firmware. A failed check leaves the plugin as careful as it was without one.
    /// </summary>
    public sealed class OdriveCapabilityDetector
    {
        // A stop ends every read, so a check that ran into one has learned nothing yet. A stop during the
        // whole time of a connection must not leave it unknown, and repeated stops must not go on for ever.
        internal const int MaxAttempts = 3;
        private const int StopWaitMs = 5000;

        private static readonly FirmwareCapabilityStatus NoConnection =
            new FirmwareCapabilityStatus(-1, FirmwareCapabilityState.Unknown, null, null);

        private readonly IOdriveSerialService _serial;
        private readonly IOdriveParameters _parameters;
        private readonly Action<string> _log;
        private readonly CancellationTokenSource _stopping = new CancellationTokenSource();
        private readonly object _lock = new object();

        private FirmwareCapabilityStatus _status = NoConnection;
        private int _checkingGeneration = -1;
        private int _highestGeneration = -1;
        private int _version;

        public OdriveCapabilityDetector(IOdriveSerialService serial, IOdriveParameters parameters, Action<string> log = null)
        {
            _serial     = serial ?? throw new ArgumentNullException(nameof(serial));
            _parameters = parameters ?? throw new ArgumentNullException(nameof(parameters));
            _log        = log ?? (_ => { });
        }

        /// <summary>Changes with every change of the state. The connection can change it too, see <see cref="Status"/>.</summary>
        public int Version => Volatile.Read(ref _version);

        /// <summary>The state for the connection that is up now.</summary>
        public FirmwareCapabilityStatus Status => StatusFor(_serial.GetStatus());

        /// <summary>The state for the connection of this status, Unknown if the check knows nothing of it yet.</summary>
        public FirmwareCapabilityStatus StatusFor(SerialStatus serial)
        {
            var status = Volatile.Read(ref _status);
            return serial.IsConnected && status.ConnectGeneration == serial.ConnectGeneration ? status : NoConnection;
        }

        /// <summary>What was declared on that connection, null if nothing was. Never the declaration of another one.</summary>
        public FirmwareCapabilities For(int connectGeneration)
        {
            var status = Volatile.Read(ref _status);
            return status.ConnectGeneration == connectGeneration ? status.Capabilities : null;
        }

        /// <summary>The declaration that counts for this status, null without a connection.</summary>
        public FirmwareCapabilities For(SerialStatus status) => status.IsConnected ? For(status.ConnectGeneration) : null;

        public bool CanCheckAgain
        {
            get
            {
                var status = Status;
                return status.State == FirmwareCapabilityState.NotDeclared && Volatile.Read(ref _checkingGeneration) != status.ConnectGeneration;
            }
        }

        public void Start()
        {
            _serial.Connected += OnConnected;
            var status = _serial.GetStatus();
            if (status.IsConnected) OnConnected(status.ConnectGeneration);
        }

        public void Stop()
        {
            _serial.Connected -= OnConnected;
            _stopping.Cancel();
        }

        /// <summary>One more try, on request and only after a check that found nothing. False if there is nothing to do.</summary>
        public bool CheckAgain()
        {
            if (_stopping.IsCancellationRequested) return false;
            var serial = _serial.GetStatus();
            if (!serial.IsConnected) return false;

            lock (_lock)
            {
                if (_status.ConnectGeneration != serial.ConnectGeneration
                    || _status.State != FirmwareCapabilityState.NotDeclared
                    || _checkingGeneration == serial.ConnectGeneration)
                    return false;
                BeginCheck(serial.ConnectGeneration);
            }
            return true;
        }

        // Start and the event can both report one connection.
        private void OnConnected(int generation)
        {
            if (_stopping.IsCancellationRequested) return;
            lock (_lock)
            {
                if (generation <= _highestGeneration) return;
                _highestGeneration = generation;
                BeginCheck(generation);
            }
        }

        private void BeginCheck(int generation)
        {
            _checkingGeneration = generation;
            Publish(new FirmwareCapabilityStatus(generation, FirmwareCapabilityState.Unknown, null, null));
            Task.Run(() => CheckAsync(generation));
        }

        private async Task CheckAsync(int generation)
        {
            FirmwareCapabilityStatus result;
            try
            {
                result = await AskAsync(generation).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log("firmware capabilities: " + ex.Message);
                result = NotDeclared(generation, ex.Message);
            }

            lock (_lock)
            {
                // The connection this was asked on is gone: whatever came back says nothing about the new one.
                if (generation != _highestGeneration) return;
                _checkingGeneration = -1;
                if (result != null) Publish(result);
            }
            if (result != null) _log("firmware capabilities on connection " + generation + ": " + result.State + (result.Detail != null ? " (" + result.Detail + ")" : ""));
        }

        // Null if the connection went away: the next one gets its own check.
        private async Task<FirmwareCapabilityStatus> AskAsync(int generation)
        {
            var context = SerialOperationContext.ForConnection(generation);
            for (int attempt = 0; attempt < MaxAttempts; attempt++)
            {
                var result = await _parameters.ReadAsync(FirmwareCapabilities.CommandPath, FieldProtocol.Openffb, CommandPriority.Batch,
                                                         _stopping.Token, context).ConfigureAwait(false);

                var serial = _serial.GetStatus();
                if (_stopping.IsCancellationRequested || !serial.IsConnected || serial.ConnectGeneration != generation) return null;

                if (result.Status != ParameterStatus.Cancelled) return Judge(result, generation);

                var ticket = serial.LastStop;
                if (ticket != null) await Task.Run(() => ticket.WaitForResult(StopWaitMs)).ConfigureAwait(false);
            }
            return NotDeclared(generation, "software stops interrupted the check");
        }

        private static FirmwareCapabilityStatus Judge(ParameterResult result, int generation)
        {
            switch (result.Status)
            {
                case ParameterStatus.Ok:
                    return FirmwareCapabilities.TryParse(result.Value, generation, out var capabilities, out var problem)
                        ? new FirmwareCapabilityStatus(generation, FirmwareCapabilityState.Declared, capabilities, null)
                        : NotDeclared(generation, "unusable reply, " + problem);
                case ParameterStatus.Rejected:
                    return NotDeclared(generation, "the firmware does not report them: " + result.Detail);
                case ParameterStatus.Timeout:
                    return NotDeclared(generation, "no reply");
                default:
                    return NotDeclared(generation, result.Detail ?? result.Status.ToString());
            }
        }

        private static FirmwareCapabilityStatus NotDeclared(int generation, string detail)
            => new FirmwareCapabilityStatus(generation, FirmwareCapabilityState.NotDeclared, null, detail);

        private void Publish(FirmwareCapabilityStatus status)
        {
            var previous = _status;
            Volatile.Write(ref _status, status);
            Interlocked.Increment(ref _version);
            // Last, so whoever this wakes finds the new state. The placeholder stands for no state at all.
            if (previous != NoConnection) previous.Supersede();
        }
    }
}

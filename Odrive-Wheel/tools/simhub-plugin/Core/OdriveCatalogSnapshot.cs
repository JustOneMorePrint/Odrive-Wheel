using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OdriveWheel.SimHubPlugin
{
    public enum SnapshotSection
    {
        /// <summary>A setting, what a backup is about. Goes into the flat values file.</summary>
        Configuration,
        /// <summary>A state, a measurement, an error, a counter or a firmware id. Changes on its own.</summary>
        Status,
    }

    public enum SnapshotReadOutcome
    {
        /// <summary>The reply is kept exactly as it came.</summary>
        Read,
        /// <summary>The firmware does not know the path (invalid property, NOT_FOUND).</summary>
        NotSupported,
        /// <summary>The firmware answered with another error.</summary>
        Refused,
        NoReply,
        /// <summary>A reply that is no value of the catalog type. It is kept, but never exported as a value.</summary>
        Unexpected,
        /// <summary>The capture ended before this path, or its reply came when the capture no longer held.</summary>
        NotRead,
    }

    /// <summary>How far a reply can be taken at its word.</summary>
    public enum ReplyCertainty
    {
        Exact,
        /// <summary>Close to the value on the board: rounded, or decimals cut.</summary>
        MayBeRounded,
        /// <summary>The number itself may be wrong: older firmware cut replies at 9 characters.</summary>
        MayBeCutOff,
    }

    public enum SnapshotCompleteness
    {
        /// <summary>Ran to the end on one state, every path has a usable reply.</summary>
        Complete,
        /// <summary>Ran to the end on one state, but some paths have no usable reply.</summary>
        WithFailures,
        /// <summary>Ended early, or the state changed before it was finished. Never a full record.</summary>
        Partial,
    }

    public sealed class SnapshotEntry
    {
        internal SnapshotEntry(string path, ParameterDefinition definition, SnapshotSection section, SnapshotReadOutcome outcome,
                               string reply, string detail, DateTime? readAtUtc, ReplyCertainty certainty)
        {
            Path       = path;
            Definition = definition;
            Section    = section;
            Outcome    = outcome;
            Reply      = reply;
            Detail     = detail;
            ReadAtUtc  = readAtUtc;
            Certainty  = certainty;
        }

        public string Path { get; }
        public ParameterDefinition Definition { get; }
        public SnapshotSection Section { get; }
        public SnapshotReadOutcome Outcome { get; }
        /// <summary>The reply as it came, for Read and Unexpected. Never formatted or decoded.</summary>
        public string Reply { get; }
        /// <summary>Why there is no value, null for Read.</summary>
        public string Detail { get; }
        public DateTime? ReadAtUtc { get; }
        public ReplyCertainty Certainty { get; }
        public bool HasValue => Outcome == SnapshotReadOutcome.Read;
    }

    /// <summary>What the firmware check said when a capture started, kept as plain data so it outlives the connection.</summary>
    public sealed class CapabilityEvidence
    {
        internal CapabilityEvidence(FirmwareCapabilityState state, string declaration, bool asciiFull, bool float32RoundTrip, string detail, bool checkedAtAll)
        {
            State            = state;
            Declaration      = declaration;
            AsciiFull        = asciiFull;
            Float32RoundTrip = float32RoundTrip;
            Detail           = detail;
            Checked          = checkedAtAll;
        }

        public FirmwareCapabilityState State { get; }
        /// <summary>The value of the sys.capabilities? reply, null unless declared.</summary>
        public string Declaration { get; }
        public bool AsciiFull { get; }
        public bool Float32RoundTrip { get; }
        public bool FloatsAreExact => AsciiFull && Float32RoundTrip;
        public string Detail { get; }
        /// <summary>False if there was no firmware check at all.</summary>
        public bool Checked { get; }

        internal static CapabilityEvidence From(FirmwareCapabilityStatus status)
        {
            if (status == null) return new CapabilityEvidence(FirmwareCapabilityState.NotDeclared, null, false, false, "no firmware check", false);
            var capabilities = status.Capabilities;
            return capabilities == null
                ? new CapabilityEvidence(status.State, null, false, false, status.Detail, true)
                : new CapabilityEvidence(status.State, capabilities.Declaration, capabilities.AsciiFull, capabilities.Float32RoundTrip, status.Detail, true);
        }
    }

    /// <summary>
    /// One capture of the catalog: every value read once, one after the other, from the RAM of the board. It is a
    /// catalog snapshot and no more: not a complete NVM image, no calibration record and no restore backup. Nothing in
    /// it changes after the capture, and it stays usable after a disconnect, as a record of that time.
    /// </summary>
    public sealed class CatalogSnapshot
    {
        internal CatalogSnapshot(IList<SnapshotEntry> entries, DateTime startedUtc, DateTime endedUtc, string portName, int connectGeneration,
                                 bool stopLatched, StopState? lastStop, CapabilityEvidence capabilities, string abortReason,
                                 int catalogVersion, string catalogSha256)
        {
            Entries           = new ReadOnlyCollection<SnapshotEntry>(entries.ToList());
            StartedUtc        = startedUtc;
            EndedUtc          = endedUtc;
            PortName          = portName;
            ConnectGeneration = connectGeneration;
            StopLatched       = stopLatched;
            LastStop          = lastStop;
            Capabilities      = capabilities;
            AbortReason       = abortReason;
            CatalogVersion    = catalogVersion;
            CatalogSha256     = catalogSha256;
        }

        /// <summary>Every path of the catalog, in catalog order, also those without a value.</summary>
        public IReadOnlyList<SnapshotEntry> Entries { get; }
        public DateTime StartedUtc { get; }
        public DateTime EndedUtc { get; }
        public string PortName { get; }
        public int ConnectGeneration { get; }
        /// <summary>The latch of a finished software stop was set during the whole capture.</summary>
        public bool StopLatched { get; }
        /// <summary>How the last software stop before the capture ended, null if there was none.</summary>
        public StopState? LastStop { get; }
        public CapabilityEvidence Capabilities { get; }
        /// <summary>Why it is partial, null otherwise.</summary>
        public string AbortReason { get; }
        public int CatalogVersion { get; }
        public string CatalogSha256 { get; }

        public SnapshotCompleteness Completeness =>
            AbortReason != null ? SnapshotCompleteness.Partial
            : Entries.All(e => e.HasValue) ? SnapshotCompleteness.Complete
            : SnapshotCompleteness.WithFailures;

        public int Expected => Entries.Count;
        public int ReadCount => Entries.Count(e => e.HasValue);
        public int UncertainCount => Entries.Count(e => e.HasValue && e.Certainty != ReplyCertainty.Exact);

        public SnapshotEntry Find(string path) => Entries.FirstOrDefault(e => e.Path == path);

        /// <summary>The values of the flat file, in catalog order: settings with a usable reply.</summary>
        public IEnumerable<KeyValuePair<string, string>> ConfigurationValues()
            => Entries.Where(e => e.Section == SnapshotSection.Configuration && e.HasValue)
                      .Select(e => new KeyValuePair<string, string>(e.Path, e.Reply));

        public IEnumerable<KeyValuePair<string, string>> StatusValues()
            => Entries.Where(e => e.Section == SnapshotSection.Status && e.HasValue)
                      .Select(e => new KeyValuePair<string, string>(e.Path, e.Reply));
    }

    public sealed class SnapshotProgress
    {
        public SnapshotProgress(int done, int total, string path)
        {
            Done  = done;
            Total = total;
            Path  = path;
        }

        public int Done { get; }
        public int Total { get; }
        public string Path { get; }
    }

    /// <summary>
    /// How certain a reply is, from what the firmware of its connection declared and from the reply itself. Shared by the
    /// capture and the comparison, so a file and the board are judged alike.
    /// </summary>
    public static class OdriveReplyCertainty
    {
        public static ReplyCertainty Of(ParameterDefinition definition, string reply, bool asciiFull, bool float32RoundTrip)
        {
            if (definition == null || reply == null) return ReplyCertainty.MayBeCutOff;
            switch (definition.Type)
            {
                case ParameterType.Text:
                case ParameterType.Boolean:
                    return ReplyCertainty.Exact;
                case ParameterType.Float:
                    if (OdriveFloatText.IsNonFinite(reply)) return ReplyCertainty.Exact;
                    // The declaration covers "r <path>" only. The OpenFFBoard handlers print floats with a fixed number of decimals.
                    if (definition.Protocol == FieldProtocol.Openffb) return ReplyCertainty.MayBeRounded;
                    if (!asciiFull && reply.Length >= OdriveProtocol.OdriveReadLimit) return ReplyCertainty.MayBeCutOff;
                    return asciiFull && float32RoundTrip ? ReplyCertainty.Exact : ReplyCertainty.MayBeRounded;
                default:
                    if (definition.Protocol == FieldProtocol.Openffb) return ReplyCertainty.Exact;
                    return asciiFull || reply.Length < OdriveProtocol.OdriveReadLimit ? ReplyCertainty.Exact : ReplyCertainty.MayBeCutOff;
            }
        }
    }

    /// <summary>
    /// Captures a catalog snapshot on request. It only reads, catalog paths and nothing else, and does not poll.
    ///
    /// The whole capture rests on one connection, one stop state and one state of the firmware check, down to each
    /// request: the serial worker refuses a line if the connection or the stop state changed, and a new state of the
    /// check cancels it through its token. A finished software stop with its latch set is a stop state like any other,
    /// reads are harmless then, and nothing here touches the latch. A reply only counts if the state still holds when it
    /// is here, and the state is looked at once more before the capture is called finished. There is no retry, and
    /// nothing is filled in from earlier reads.
    /// </summary>
    public sealed class OdriveSnapshotCapture
    {
        // Each costs the read timeout plus the wait for the reply stream, as in OdriveParameterReader.
        internal const int MaxTimeoutsInARow = OdriveParameterReader.MaxTimeoutsInARow;

        /// <summary>Paths the web client leaves out of its export (READONLY_EXPORT_PATHS in odrive-wheel.html):
        /// runtime state, not configuration. Here they count as status, so the flat file holds what the web client's would.</summary>
        internal static readonly IReadOnlyCollection<string> WebClientExportSkips = new[]
        {
            "axis0.controller.config.anticogging.index",
            "axis0.controller.config.anticogging.calib_anticogging",
        };

        private const string StatusTab = "status";

        private readonly IOdriveParameters _parameters;
        private readonly Func<SerialStatus> _getStatus;
        private readonly OdriveParameterCatalog _catalog;
        private readonly Func<SerialStatus, FirmwareCapabilityStatus> _capabilityStatusFor;
        private readonly Func<DateTime> _utcNow;

        /// <param name="capabilityStatusFor">The state of the firmware check for a connection. Without one every
        /// reply stays as uncertain as on firmware that declares nothing.</param>
        public OdriveSnapshotCapture(IOdriveParameters parameters, Func<SerialStatus> getStatus, OdriveParameterCatalog catalog,
                                     Func<SerialStatus, FirmwareCapabilityStatus> capabilityStatusFor = null, Func<DateTime> utcNow = null)
        {
            _parameters = parameters ?? throw new ArgumentNullException(nameof(parameters));
            _getStatus  = getStatus ?? throw new ArgumentNullException(nameof(getStatus));
            _catalog    = catalog ?? throw new ArgumentNullException(nameof(catalog));
            _capabilityStatusFor = capabilityStatusFor;
            _utcNow     = utcNow ?? (() => DateTime.UtcNow);
        }

        public static SnapshotSection SectionOf(ParameterDefinition parameter)
            => parameter.Tab == StatusTab || WebClientExportSkips.Contains(parameter.Path) ? SnapshotSection.Status : SnapshotSection.Configuration;

        /// <summary>Why a capture cannot start now, null if it can.</summary>
        public string WhyNotStart()
        {
            var status = _getStatus();
            return WhyNotStart(status, _capabilityStatusFor?.Invoke(status));
        }

        private static string WhyNotStart(SerialStatus status, FirmwareCapabilityStatus check)
        {
            if (!status.IsConnected) return OdriveStatusText.Connection(status).Text;
            if (status.LastStop != null && !status.LastStop.IsFinished) return "a software stop is running";
            if (check != null && check.State == FirmwareCapabilityState.Unknown) return "the firmware check has not ended on this connection yet";
            return null;
        }

        private sealed class Binding
        {
            public int Generation;
            public StopTicket LastStop;
            public bool StopLatched;
            public FirmwareCapabilityStatus Check;
        }

        /// <summary>Reads every catalog path once. Never throws for what the board or the link does: the snapshot says
        /// how far it got and why it ended.</summary>
        /// <exception cref="InvalidOperationException">It cannot start, the message says why. <see cref="WhyNotStart"/>
        /// tells beforehand, unless the state changes in between.</exception>
        public async Task<CatalogSnapshot> CaptureAsync(IProgress<SnapshotProgress> progress, CancellationToken cancellation)
        {
            // One status for the check and the binding, so the capture cannot rest on a stop that began in between.
            var status = _getStatus();
            var check = _capabilityStatusFor?.Invoke(status);
            var refusal = WhyNotStart(status, check);
            if (refusal != null) throw new InvalidOperationException(refusal);

            var binding = new Binding
            {
                Generation  = status.ConnectGeneration,
                LastStop    = status.LastStop,
                StopLatched = status.StopLatched,
                Check       = check,
            };
            var evidence = CapabilityEvidence.From(binding.Check);
            var started = _utcNow();

            var superseded = binding.Check?.Superseded ?? CancellationToken.None;
            using (var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellation, superseded))
            {
                var (entries, abortReason) = await ReadAll(binding, evidence, progress, operation.Token).ConfigureAwait(false);

                // The last reply is no proof that the capture still stood when it ended.
                abortReason = abortReason ?? FinishedOn(WhyEnded(binding, CancellationToken.None));

                return new CatalogSnapshot(entries, started, _utcNow(), status.PortName, binding.Generation, binding.StopLatched,
                                           binding.LastStop?.State, evidence, abortReason, _catalog.Version, _catalog.Sha256);
            }
        }

        private static string FinishedOn(string reason) => reason == null ? null : reason + ", before the capture was finished";

        private async Task<(List<SnapshotEntry> Entries, string AbortReason)> ReadAll(Binding binding, CapabilityEvidence evidence,
                                                                                       IProgress<SnapshotProgress> progress, CancellationToken cancellation)
        {
            var paths = _catalog.Parameters;
            var context = SerialOperationContext.ForReading(binding.Generation, binding.LastStop, binding.StopLatched);
            var entries = new List<SnapshotEntry>(paths.Count);
            string abortReason = null;
            int timeoutsInARow = 0;

            foreach (var parameter in paths)
            {
                abortReason = WhyEnded(binding, cancellation);
                if (abortReason != null) break;

                var result = await _parameters.ReadAsync(parameter.Path, parameter.Protocol, CommandPriority.Batch, cancellation, context)
                                              .ConfigureAwait(false);

                // A reply only speaks for this capture if it still stands now that the reply is here.
                var ended = WhyEnded(binding, CancellationToken.None);
                if (ended != null)
                {
                    entries.Add(NotRead(parameter, result.IsOk ? "reply not used: " + ended : ended));
                    abortReason = ended;
                    break;
                }

                var entry = Judge(parameter, result, evidence);
                entries.Add(entry);
                progress?.Report(new SnapshotProgress(entries.Count, paths.Count, parameter.Path));

                timeoutsInARow = result.Status == ParameterStatus.Timeout ? timeoutsInARow + 1 : 0;
                if (entry.Outcome == SnapshotReadOutcome.NotRead)
                {
                    abortReason = WhyEnded(binding, cancellation) ?? entry.Detail;
                    break;
                }
                if (timeoutsInARow >= MaxTimeoutsInARow)
                {
                    abortReason = "the board does not answer";
                    break;
                }
            }

            foreach (var parameter in paths.Skip(entries.Count))
                entries.Add(NotRead(parameter, "not reached"));
            return (entries, abortReason);
        }

        // In this order, like OdriveRamApplier: the token ends with the last two.
        private string WhyEnded(Binding binding, CancellationToken cancellation)
        {
            var status = _getStatus();
            if (status.LastStop != binding.LastStop) return "software stop";
            if (status.StopLatched != binding.StopLatched) return "the stop latch was cleared";
            if (!status.IsConnected) return OdriveStatusText.Connection(status).Text;
            if (status.ConnectGeneration != binding.Generation) return "the connection changed, the board may have rebooted";
            if (binding.Check != null && binding.Check.IsSuperseded) return "the firmware check changed";
            if (cancellation.IsCancellationRequested) return "cancelled";
            return null;
        }

        private SnapshotEntry Judge(ParameterDefinition parameter, ParameterResult result, CapabilityEvidence evidence)
        {
            var section = SectionOf(parameter);
            switch (result.Status)
            {
                case ParameterStatus.Ok:
                    if (!OdriveParameterFormat.Format(parameter, result.Value, whole: true).IsValid)
                        return new SnapshotEntry(parameter.Path, parameter, section, SnapshotReadOutcome.Unexpected, result.Value,
                                                 "not a value of type " + parameter.Type.ToString().ToLowerInvariant(), _utcNow(), ReplyCertainty.MayBeCutOff);
                    return new SnapshotEntry(parameter.Path, parameter, section, SnapshotReadOutcome.Read, result.Value, null, _utcNow(),
                                             OdriveReplyCertainty.Of(parameter, result.Value, evidence.AsciiFull, evidence.Float32RoundTrip));
                case ParameterStatus.Rejected:
                    bool unsupported = result.Detail == "invalid property" || result.Detail == "NOT_FOUND";
                    return new SnapshotEntry(parameter.Path, parameter, section,
                                             unsupported ? SnapshotReadOutcome.NotSupported : SnapshotReadOutcome.Refused,
                                             null, result.Detail ?? "refused", null, ReplyCertainty.Exact);
                case ParameterStatus.Timeout:
                    return new SnapshotEntry(parameter.Path, parameter, section, SnapshotReadOutcome.NoReply, null, "no reply", null, ReplyCertainty.Exact);
                default:
                    return NotRead(parameter, result.Detail ?? result.Status.ToString());
            }
        }

        private static SnapshotEntry NotRead(ParameterDefinition parameter, string why)
            => new SnapshotEntry(parameter.Path, parameter, SectionOf(parameter), SnapshotReadOutcome.NotRead, null, why, null, ReplyCertainty.Exact);
    }
}

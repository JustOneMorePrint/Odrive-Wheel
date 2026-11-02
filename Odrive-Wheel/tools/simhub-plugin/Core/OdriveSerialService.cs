using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace OdriveWheel.SimHubPlugin
{
    public interface IOdriveSerialService
    {
        /// <summary>Queues one command. Never blocks, and the task always completes.
        /// Without a connection it completes at once, nothing is replayed later.
        /// Cancelling ends a queued command at once. A running one ends with the worker's next
        /// check, and its result says how far the line got.</summary>
        Task<SerialResult> SendAsync(SerialRequest request);

        /// <summary>Requests IDLE ahead of everything else. Sets a flag and returns,
        /// it never blocks, whatever the worker is doing.</summary>
        StopTicket RequestStop();

        void ClearStopLatch();
        void ReleasePort();
        void AcquirePort();
        SerialStatus GetStatus();

        /// <summary>Cancelled by the next software stop. Multi-line jobs pass it on
        /// and end with their next line.</summary>
        CancellationToken StopToken { get; }

        /// <summary>A new connection is up, with its generation. Raised on a thread pool thread,
        /// never on the worker, so a handler can send and wait for the reply.</summary>
        event Action<int> Connected;

        void Start();
        void Shutdown(int timeoutMs);
    }

    /// <summary>
    /// The only owner of the COM port. One worker thread opens, writes, reads and closes
    /// it, and only one command is on the wire at a time. All other threads talk to the
    /// worker through the queue, the status snapshot and a few flags.
    /// </summary>
    public sealed class OdriveSerialService : IOdriveSerialService
    {
        private const string StopLineAxis0 = "w axis0.requested_state 1";
        private const string StopLineAxis1 = "w axis1.requested_state 1";
        private const string StateReadLine = "r axis0.current_state";
        private const int    AxisStateIdle = 1;

        private sealed class PendingCommand
        {
            private const int Queued = 0, Taken = 1, CalledOff = 2;
            private int _phase = Queued;

            public PendingCommand(SerialRequest request)
            {
                Request = request;
                EnqueuedTicks = Stopwatch.GetTimestamp();
            }

            public SerialRequest Request { get; }
            public long EnqueuedTicks { get; }
            // Waiters must not run on the worker thread.
            public TaskCompletionSource<SerialResult> Completion { get; }
                = new TaskCompletionSource<SerialResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            public CancellationTokenRegistration Registration { get; set; }

            // Exactly one of these wins. In the queue nothing has happened yet, so a cancellation
            // can answer at once. Once the worker has the command, only it knows how far the line
            // got, so from then on only the worker answers.
            public bool TryTake() => Interlocked.CompareExchange(ref _phase, Taken, Queued) == Queued;
            public bool TryCallOff() => Interlocked.CompareExchange(ref _phase, CalledOff, Queued) == Queued;
        }

        private readonly ISerialTransport     _transport;
        private readonly IPortLocator         _locator;
        private readonly Func<int?>           _hidAxisState;
        private readonly SerialServiceOptions _options;
        private readonly Action<string>       _log;

        private readonly object _queueLock = new object();
        private readonly Queue<PendingCommand>[] _queues =
        {
            new Queue<PendingCommand>(),
            new Queue<PendingCommand>(),
            new Queue<PendingCommand>(),
        };
        private int  _queuedCount;
        private bool _queueClosed;

        private readonly AutoResetEvent _wake        = new AutoResetEvent(false);
        private readonly AutoResetEvent _watcherWake = new AutoResetEvent(false);

        private Thread _worker;
        private Thread _watcher;

        private volatile bool _started;
        private volatile bool _shutdownRequested;
        private volatile bool _wantReleased;
        private volatile bool _stopLatched;
        private StopTicket _activeStop;
        private volatile StopTicket _lastStop;
        private volatile CancellationTokenSource _stopCts = new CancellationTokenSource();

        private volatile SerialConnectionState _state = SerialConnectionState.NotStarted;
        private volatile string _openPort;
        private volatile string _locatedPort;
        private volatile string _lastError;
        private int _generation;
        private int _portMisses;

        // Worker thread only.
        private bool _transportOpen;
        private bool _odriveOutOfSync;
        private bool _offbOutOfSync;
        private bool _linePartial;
        private uint _nextSyncMarker = 1000000;
        private long _nextConnectTicks;

        public OdriveSerialService(ISerialTransport transport, IPortLocator locator,
                                   Func<int?> hidAxisState = null,
                                   SerialServiceOptions options = null,
                                   Action<string> log = null)
        {
            _transport    = transport ?? throw new ArgumentNullException(nameof(transport));
            _locator      = locator   ?? throw new ArgumentNullException(nameof(locator));
            _hidAxisState = hidAxisState ?? (() => null);
            _options      = options ?? new SerialServiceOptions();
            _log          = log ?? (_ => { });
        }

        // ---- Public API ------------------------------------------------------

        public CancellationToken StopToken => _stopCts.Token;

        public event Action<int> Connected;

        public void Start()
        {
            if (_started) return;
            _started = true;
            _state   = SerialConnectionState.Searching;

            _watcher = new Thread(WatcherLoop) { IsBackground = true, Name = "OdrivePortWatcher" };
            _worker  = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name         = "OdriveSerialWorker",
                Priority     = ThreadPriority.AboveNormal,
            };
            _watcher.Start();
            _worker.Start();
        }

        public Task<SerialResult> SendAsync(SerialRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            if (_shutdownRequested)
                return Finished(SerialOutcome.ShuttingDown, "the plugin is shutting down");
            if (request.Cancellation.IsCancellationRequested)
                return Finished(SerialOutcome.Cancelled, "cancelled, not sent");
            if (_stopLatched && OdriveCommandGuard.IsBlockedWhileStopped(request.Line))
                return Finished(SerialOutcome.BlockedByStop, "blocked by the software stop");

            var state = _state;
            if (state != SerialConnectionState.Connected)
                return Finished(OutcomeFor(state), DetailFor(state));

            var pending = new PendingCommand(request);

            // Before the queue: the worker disposes this when it completes the command.
            if (request.Cancellation.CanBeCanceled)
                pending.Registration = request.Cancellation.Register(() => CallOff(pending));

            lock (_queueLock)
            {
                if (_queueClosed)
                    return Refuse(pending, SerialOutcome.ShuttingDown, "the plugin is shutting down");
                if (_queuedCount >= _options.MaxQueuedCommands)
                    return Refuse(pending, SerialOutcome.QueueFull, "too many commands are waiting");
                _queues[(int)request.Priority].Enqueue(pending);
                _queuedCount++;
            }

            _wake.Set();
            return pending.Completion.Task;
        }

        private static void CallOff(PendingCommand command)
        {
            if (command.TryCallOff())
                command.Completion.TrySetResult(new SerialResult(SerialOutcome.Cancelled, detail: "cancelled, not sent"));
        }

        private static Task<SerialResult> Refuse(PendingCommand command, SerialOutcome outcome, string detail)
        {
            Complete(command, new SerialResult(outcome, detail: detail));
            return command.Completion.Task;
        }

        public StopTicket RequestStop()
        {
            _stopLatched = true;
            if (!_started || _shutdownRequested)
            {
                var dead = new StopTicket();
                dead.Finish(StopState.NotDelivered, "the serial service is not running");
                _lastStop = dead;
                return dead;
            }

            var fresh = new StopTicket();
            while (true)
            {
                var current = Volatile.Read(ref _activeStop);
                if (current != null && !current.IsFinished)
                {
                    _wake.Set();
                    return current;
                }
                if (Interlocked.CompareExchange(ref _activeStop, fresh, current) == current) break;
            }
            _lastStop = fresh;
            _wake.Set();
            return fresh;
        }

        public void ClearStopLatch() => _stopLatched = false;

        public void ReleasePort()
        {
            _wantReleased = true;
            _wake.Set();
        }

        public void AcquirePort()
        {
            _wantReleased = false;
            _wake.Set();
            _watcherWake.Set();
        }

        public SerialStatus GetStatus()
        {
            return new SerialStatus(_state, _openPort ?? _locatedPort, Volatile.Read(ref _generation),
                                    _lastError, _lastStop, _stopLatched);
        }

        public void Shutdown(int timeoutMs)
        {
            _shutdownRequested = true;
            _wake.Set();
            _watcherWake.Set();

            // Also covers a worker stuck in a driver call: nobody keeps waiting on it.
            FailAllQueued(SerialOutcome.ShuttingDown, "the plugin is shutting down", closeQueue: true);

            var worker = _worker;
            if (worker != null && !worker.Join(timeoutMs))
                _log("serial worker did not stop within " + timeoutMs + " ms");

            var watcher = _watcher;
            if (watcher != null) watcher.Join(Math.Min(timeoutMs, 200));

            if (!_started) _state = SerialConnectionState.ShutDown;
        }

        // ---- Worker ----------------------------------------------------------

        private void WorkerLoop()
        {
            while (!_shutdownRequested)
            {
                try
                {
                    WorkerStep();
                }
                catch (Exception ex)
                {
                    // An unhandled exception here would take SimHub down with it.
                    _log("serial worker error: " + ex);
                    LoseConnection("internal error: " + ex.Message);
                    _wake.WaitOne(_options.IdleWaitMs);
                }
            }

            var stop = Interlocked.Exchange(ref _activeStop, null);
            if (stop != null && !stop.IsFinished)
                stop.Finish(StopState.NotDelivered, "the plugin is shutting down");

            FailAllQueued(SerialOutcome.ShuttingDown, "the plugin is shutting down", closeQueue: true);
            CloseTransport();
            try { _transport.Dispose(); } catch (Exception ex) { _log("transport dispose: " + ex.Message); }
            _state = SerialConnectionState.ShutDown;
        }

        private void WorkerStep()
        {
            var stop = Volatile.Read(ref _activeStop);
            if (stop != null && !stop.IsFinished)
            {
                RunStop(stop);
                return;
            }

            if (_wantReleased)
            {
                CloseTransport();
                MarkAllOutOfSync();   // another application may use the port meanwhile
                _state = SerialConnectionState.Released;
                FailAllQueued(SerialOutcome.PortReleased, DetailFor(SerialConnectionState.Released));
                _wake.WaitOne(_options.IdleWaitMs);
                return;
            }

            if (!_transportOpen)
            {
                TryConnect();
                if (!_transportOpen)
                {
                    var state = _state;
                    FailAllQueued(OutcomeFor(state), DetailFor(state));
                    _wake.WaitOne(_options.IdleWaitMs);
                }
                return;
            }

            if (Volatile.Read(ref _portMisses) >= _options.PortMissLimit)
            {
                LoseConnection("the board is no longer present");
                return;
            }

            var command = Dequeue();
            if (command == null)
            {
                _wake.WaitOne(_options.IdleWaitMs);
                return;
            }
            Execute(command);
        }

        private void Execute(PendingCommand command)
        {
            var request = command.Request;
            bool written = false;
            try
            {
                var refusal = RefusalBeforeSending(command) ?? PrepareLine(request);
                if (refusal != null)
                {
                    Complete(command, refusal);
                    return;
                }

                // A late reply or a diagnostic line must not pass as this command's reply.
                _transport.DiscardInput();

                // Preparing takes time, and until now nothing is lost by saying no.
                refusal = RefusalBeforeSending(command);
                if (refusal != null)
                {
                    Complete(command, refusal);
                    return;
                }

                _transport.WriteLine(request.Line, _options.WriteTimeoutMs);
                written = true;
                Complete(command, AwaitReply(request));
            }
            catch (TransportWriteTimeoutException ex)
            {
                _linePartial = true;
                Complete(command, new SerialResult(SerialOutcome.Timeout, detail: ex.Message + ", not sent"));
            }
            catch (TransportException ex)
            {
                Complete(command, new SerialResult(SerialOutcome.TransportError, detail: ex.Message, lineWritten: written));
                LoseConnection(ex.Message);
            }
            catch (Exception ex)
            {
                // Nobody else can end a running command. The worker loop handles the error itself.
                Complete(command, new SerialResult(SerialOutcome.TransportError, detail: ex.Message, lineWritten: written));
                throw;
            }
        }

        // Why a command must not go out any more, null if it may.
        private SerialResult RefusalBeforeSending(PendingCommand command)
        {
            var request = command.Request;
            var abort = AbortReason(request.Cancellation, lineWritten: false);
            if (abort != null) return abort;
            if (ElapsedMs(command.EnqueuedTicks) > _options.MaxQueueWaitMs)
                return new SerialResult(SerialOutcome.Timeout, detail: "expired in the queue");
            if (_stopLatched && OdriveCommandGuard.IsBlockedWhileStopped(request.Line))
                return new SerialResult(SerialOutcome.BlockedByStop, detail: "blocked by the software stop");
            return ContextRefusal(request.Context);
        }

        // Only this thread opens a connection, so the generation cannot change between here and the line.
        // A stop requested in that instant cannot call the line back, its own lines follow right behind it.
        private SerialResult ContextRefusal(SerialOperationContext context)
        {
            if (context == null) return null;
            if (context.BindsStop && (_stopLatched || _lastStop != context.LastStop))
                return new SerialResult(SerialOutcome.BlockedByStop, detail: "software stop, not sent");
            if (Volatile.Read(ref _generation) != context.ConnectGeneration)
                return new SerialResult(SerialOutcome.NotConnected, detail: "the connection changed, not sent");
            return null;
        }

        private SerialResult AbortReason(CancellationToken cancellation, bool lineWritten)
        {
            string sent = lineWritten ? ", the line was already sent" : ", not sent";
            if (_shutdownRequested)
                return new SerialResult(SerialOutcome.ShuttingDown, detail: "the plugin is shutting down" + sent, lineWritten: lineWritten);
            if (Volatile.Read(ref _activeStop) != null)
                return new SerialResult(SerialOutcome.Cancelled, detail: "software stop" + sent, lineWritten: lineWritten);
            if (_wantReleased)
                return new SerialResult(SerialOutcome.PortReleased, detail: DetailFor(SerialConnectionState.Released) + sent, lineWritten: lineWritten);
            if (cancellation.IsCancellationRequested)
                return new SerialResult(SerialOutcome.Cancelled, detail: "cancelled" + sent, lineWritten: lineWritten);
            return null;
        }

        // Returns null once the command may go out, otherwise why it may not.
        //
        // After a write timeout part of a line may sit in the board's buffer. An empty line
        // ends it; the firmware answers that with "unknown command", so the stream needs a
        // resync afterwards.
        //
        // A command only needs its own reply path in sync: a late reply from the other one has
        // another form and can't pass for its reply. So a silent ODrive channel, for example a
        // stuck sink, doesn't take the OpenFFBoard commands with it.
        private SerialResult PrepareLine(SerialRequest request)
        {
            if (_linePartial)
            {
                _transport.WriteLine(string.Empty, _options.WriteTimeoutMs);
                _linePartial = false;
                MarkAllOutOfSync();
            }
            if (!IsOutOfSync(request.Mode)) return null;
            return Resync(request.Mode, _options.ResyncTimeoutMs,
                          () => AbortReason(request.Cancellation, lineWritten: false));
        }

        private bool IsOutOfSync(ReplyMode mode)
            => mode == ReplyMode.OffbEcho ? _offbOutOfSync : _odriveOutOfSync;

        private void SetOutOfSync(ReplyMode mode, bool outOfSync)
        {
            if (mode == ReplyMode.OffbEcho) _offbOutOfSync = outOfSync;
            else _odriveOutOfSync = outOfSync;
        }

        private void MarkAllOutOfSync()
        {
            _odriveOutOfSync = true;
            _offbOutOfSync   = true;
        }

        // After a timeout or an abort a reply can still arrive at any time, and neither an ODrive
        // reply nor an OpenFFBoard echo says which request it belongs to. Waiting proves nothing,
        // so this asks: everything older than the echo of a sync line has passed on that reply
        // path, the firmware answers in order. Lines before it are late replies. The line can
        // get lost, so it is repeated. Echoes of repeats that arrive later are recognisable and
        // never taken for a reply.
        private SerialResult Resync(ReplyMode mode, int timeoutMs, Func<SerialResult> abortReason)
        {
            bool offb = mode == ReplyMode.OffbEcho;
            long start = Stopwatch.GetTimestamp();
            long lastSent = 0;
            uint firstMarker = _nextSyncMarker;

            while (true)
            {
                var abort = abortReason();
                if (abort != null) return abort;

                long remaining = timeoutMs - ElapsedMs(start);
                if (remaining <= 0)
                    return new SerialResult(SerialOutcome.Timeout, detail: "the reply stream is out of sync, not sent");

                if (lastSent == 0 || ElapsedMs(lastSent) >= _options.ResyncRetryMs)
                {
                    _transport.WriteLine(offb ? OdriveProtocol.OffbSyncRequest(_nextSyncMarker)
                                              : OdriveProtocol.SyncRequest(_nextSyncMarker),
                                         _options.WriteTimeoutMs);
                    _nextSyncMarker++;
                    lastSent = Stopwatch.GetTimestamp();
                }

                var line = _transport.ReadLine((int)Math.Min(_options.ReadSliceMs, remaining));
                if (line == null) continue;
                line = line.TrimEnd('\r', '\n');
                if (line.Length == 0) continue;

                uint marker;
                bool isEcho = offb ? OdriveProtocol.TryParseOffbSyncEcho(line, out marker)
                                   : OdriveProtocol.TryParseSyncEcho(line, out marker);
                if (isEcho && marker >= firstMarker && marker < _nextSyncMarker)
                {
                    SetOutOfSync(mode, false);
                    return null;
                }
                if (!OdriveProtocol.IsSyncEcho(line) && !OdriveProtocol.IsOffbSyncEcho(line))
                    _log("discarded late reply: " + line);
            }
        }

        private SerialResult AwaitReply(SerialRequest request)
        {
            long start = Stopwatch.GetTimestamp();
            string partial = null;

            while (true)
            {
                var abort = AbortReason(request.Cancellation, lineWritten: true);
                if (abort != null)
                {
                    SetOutOfSync(request.Mode, true);
                    return abort;
                }

                long remaining = request.TimeoutMs - ElapsedMs(start);
                if (remaining <= 0)
                {
                    // In both cases a reply can still be on its way.
                    SetOutOfSync(request.Mode, true);
                    if (request.Mode == ReplyMode.SilenceIsOk)
                        return new SerialResult(SerialOutcome.Silence, lineWritten: true);
                    return new SerialResult(SerialOutcome.Timeout, detail: "no reply", lineWritten: true);
                }

                var line = _transport.ReadLine((int)Math.Min(_options.ReadSliceMs, remaining));
                if (line == null) continue;
                line = line.TrimEnd('\r', '\n');
                if (line.Length == 0) continue;

                switch (request.Mode)
                {
                    case ReplyMode.OffbEcho:
                        if (OdriveProtocol.IsOffbSyncEcho(line))
                        {
                            _log("discarded stale reply: " + line);
                        }
                        // A reply can span lines, it ends with the closing bracket.
                        else if (partial != null)
                        {
                            partial += "\n" + line;
                            if (partial.EndsWith("]", StringComparison.Ordinal))
                                return new SerialResult(SerialOutcome.Reply, partial, lineWritten: true);
                        }
                        else if (line.StartsWith(request.ExpectedEcho ?? "[", StringComparison.Ordinal))
                        {
                            if (line.EndsWith("]", StringComparison.Ordinal))
                                return new SerialResult(SerialOutcome.Reply, line, lineWritten: true);
                            partial = line;
                        }
                        else
                        {
                            _log("discarded stale reply: " + line);
                        }
                        break;

                    default:
                        if (line.StartsWith("[", StringComparison.Ordinal) || OdriveProtocol.IsSyncEcho(line))
                            _log("discarded stale reply: " + line);
                        else
                            return new SerialResult(SerialOutcome.Reply, line, lineWritten: true);
                        break;
                }
            }
        }

        private void DiscardFor(int durationMs)
        {
            long start = Stopwatch.GetTimestamp();
            while (true)
            {
                long remaining = durationMs - ElapsedMs(start);
                if (remaining <= 0) return;
                var line = _transport.ReadLine((int)Math.Min(_options.ReadSliceMs, remaining));
                if (!string.IsNullOrEmpty(line)) _log("discarded late reply: " + line);
            }
        }

        // ---- Software stop ---------------------------------------------------

        private void RunStop(StopTicket ticket)
        {
            // Everything queued is void now, a running profile ends with its next line.
            var oldCts = _stopCts;
            _stopCts = new CancellationTokenSource();
            oldCts.Cancel();
            FailAllQueued(SerialOutcome.Cancelled, "software stop, not sent");

            bool closeAfterwards = false;
            if (!_transportOpen)
            {
                closeAfterwards = _wantReleased;
                var reason = OpenForStop();
                if (reason != null)
                {
                    FinishStop(ticket, StopState.NotDelivered, reason);
                    return;
                }
            }

            try
            {
                var (state, detail) = DeliverStop(ticket);
                FinishStop(ticket, state, detail);
            }
            finally
            {
                if (closeAfterwards) CloseTransport();
            }
        }

        private (StopState state, string detail) DeliverStop(StopTicket ticket)
        {
            string lastProblem = "nothing reported IDLE";

            for (int attempt = 0; attempt < _options.StopAttempts && !_shutdownRequested; attempt++)
            {
                try
                {
                    // The leading line end finishes whatever an aborted write left behind.
                    // It gets an answer, so the streams need a resync before the next command.
                    MarkAllOutOfSync();
                    _transport.WriteLine("\n" + StopLineAxis0, _options.WriteTimeoutMs);
                    _linePartial = false;
                    ticket.MarkWritten();
                    _transport.WriteLine(StopLineAxis1, _options.WriteTimeoutMs);

                    DiscardFor(_options.StopSettleMs);

                    if (ReportsIdleOverHid()) return (StopState.Confirmed, "HID reports IDLE");
                    if (ReadsIdleOverSerial()) return (StopState.Confirmed, "serial read-back reports IDLE");
                }
                catch (TransportWriteTimeoutException ex)
                {
                    // Board busy, for example with flash: the next attempt may get through.
                    _linePartial = true;
                    lastProblem  = ex.Message;
                }
                catch (TransportException ex)
                {
                    LoseConnection(ex.Message);
                    return (ticket.WrittenAfterMs >= 0 ? StopState.Unconfirmed : StopState.NotDelivered, ex.Message);
                }
            }

            return (ticket.WrittenAfterMs >= 0 ? StopState.Unconfirmed : StopState.NotDelivered, lastProblem);
        }

        private bool ReportsIdleOverHid()
        {
            try { return _hidAxisState() == AxisStateIdle; }
            catch (Exception ex) { _log("HID axis state: " + ex.Message); return false; }
        }

        // A late reply of a command the stop has just aborted must not pass for the state, so
        // this reads only once the stream is in sync. Two matching replies in a row on top: a
        // wrong confirmation is worse than a missing one.
        private bool ReadsIdleOverSerial()
        {
            var unsynced = Resync(ReplyMode.OdriveLine, _options.StopReadbackMs, () =>
                _shutdownRequested ? new SerialResult(SerialOutcome.ShuttingDown) : null);
            if (unsynced != null) return false;

            for (int i = 0; i < 2; i++)
            {
                _transport.DiscardInput();
                _transport.WriteLine(StateReadLine, _options.WriteTimeoutMs);
                var reply = ReadOneLine(_options.StopReadbackMs);
                if (reply == null)
                {
                    _odriveOutOfSync = true;
                    return false;
                }
                if (!int.TryParse(reply.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int state)
                    || state != AxisStateIdle)
                    return false;
            }
            return true;
        }

        private string ReadOneLine(int timeoutMs)
        {
            long start = Stopwatch.GetTimestamp();
            while (true)
            {
                long remaining = timeoutMs - ElapsedMs(start);
                if (remaining <= 0) return null;
                var line = _transport.ReadLine((int)Math.Min(_options.ReadSliceMs, remaining));
                if (string.IsNullOrEmpty(line)) continue;
                if (line.StartsWith("[", StringComparison.Ordinal) || OdriveProtocol.IsSyncEcho(line)) continue;
                return line;
            }
        }

        private void FinishStop(StopTicket ticket, StopState state, string detail)
        {
            ticket.Finish(state, detail, Volatile.Read(ref _generation));
            Interlocked.CompareExchange(ref _activeStop, null, ticket);
            _log("software stop: " + state + " (" + detail + "), written after " + ticket.WrittenAfterMs + " ms");
        }

        // Returns null if the port is open now, otherwise why it is not.
        private string OpenForStop()
        {
            var port = _locatedPort;
            if (string.IsNullOrEmpty(port)) return "the board was not found";
            try
            {
                OpenTransport(port);
                return null;
            }
            catch (PortBusyException)
            {
                if (!_wantReleased) _state = SerialConnectionState.Busy;
                return "another application holds " + port;
            }
            catch (TransportException ex)
            {
                return ex.Message;
            }
        }

        // ---- Connection ------------------------------------------------------

        private void TryConnect()
        {
            var port = _locatedPort;
            if (string.IsNullOrEmpty(port))
            {
                _state = SerialConnectionState.Searching;
                return;
            }
            if (Stopwatch.GetTimestamp() < _nextConnectTicks) return;

            _state = SerialConnectionState.Connecting;
            try
            {
                OpenTransport(port);
            }
            catch (PortBusyException ex)
            {
                _lastError = ex.Message;
                _state = SerialConnectionState.Busy;
                ScheduleReconnect();
            }
            catch (TransportException ex)
            {
                _lastError = ex.Message;
                _state = SerialConnectionState.Searching;
                ScheduleReconnect();
            }
        }

        private void OpenTransport(string port)
        {
            _transport.Open(port);
            try
            {
                if (_options.OpenSettleMs > 0) Thread.Sleep(_options.OpenSettleMs);
                _transport.DiscardInput();
            }
            catch (TransportException)
            {
                _transport.Close();
                throw;
            }

            _transportOpen = true;
            _openPort      = port;
            _lastError     = null;
            Volatile.Write(ref _portMisses, 0);

            // Opened for a stop while released: not a connection for the other callers.
            if (!_wantReleased)
            {
                int generation = Interlocked.Increment(ref _generation);
                _state = SerialConnectionState.Connected;
                _log("connected on " + port);
                RaiseConnected(generation);
            }
        }

        private void RaiseConnected(int generation)
        {
            var handlers = Connected;
            if (handlers == null) return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { handlers(generation); }
                catch (Exception ex) { _log("connected handler: " + ex.Message); }
            });
        }

        private void CloseTransport()
        {
            if (!_transportOpen) return;
            _transportOpen = false;
            _openPort = null;
            _transport.Close();
        }

        private void LoseConnection(string reason)
        {
            bool wasOpen = _transportOpen;
            CloseTransport();
            // The board may have kept running, with a reply or part of a line pending.
            MarkAllOutOfSync();
            _lastError = reason;
            if (wasOpen) _log("connection lost: " + reason);
            if (!_wantReleased) _state = SerialConnectionState.Searching;
            ScheduleReconnect();
            FailAllQueued(SerialOutcome.NotConnected, reason);
            _watcherWake.Set();
        }

        private void ScheduleReconnect()
        {
            _nextConnectTicks = Stopwatch.GetTimestamp()
                              + _options.ReconnectIntervalMs * Stopwatch.Frequency / 1000;
        }

        private void WatcherLoop()
        {
            while (!_shutdownRequested)
            {
                try
                {
                    var found = _locator.FindPort();
                    if (found == null) Interlocked.Increment(ref _portMisses);
                    else Volatile.Write(ref _portMisses, 0);

                    if (!string.Equals(found, _locatedPort, StringComparison.Ordinal))
                    {
                        _locatedPort = found;
                        _wake.Set();
                    }
                }
                catch (Exception ex)
                {
                    // A failed search says nothing about the board, the last result stays.
                    _log("port search failed: " + ex.Message);
                }
                _watcherWake.WaitOne(_options.PortScanIntervalMs);
            }
        }

        // ---- Queue -----------------------------------------------------------

        private PendingCommand Dequeue()
        {
            lock (_queueLock)
            {
                foreach (var queue in _queues)
                {
                    while (queue.Count > 0)
                    {
                        _queuedCount--;
                        var command = queue.Dequeue();
                        if (command.TryTake()) return command;   // otherwise it is already answered
                    }
                }
            }
            return null;
        }

        private void FailAllQueued(SerialOutcome outcome, string detail, bool closeQueue = false)
        {
            var failed = new List<PendingCommand>();
            lock (_queueLock)
            {
                if (closeQueue) _queueClosed = true;
                foreach (var queue in _queues)
                {
                    while (queue.Count > 0) failed.Add(queue.Dequeue());
                }
                _queuedCount = 0;
            }
            foreach (var command in failed)
                Complete(command, new SerialResult(outcome, detail: detail));
        }

        private static void Complete(PendingCommand command, SerialResult result)
        {
            command.Completion.TrySetResult(result);
            command.Registration.Dispose();
        }

        // ---- Helpers ---------------------------------------------------------

        private static Task<SerialResult> Finished(SerialOutcome outcome, string detail)
            => Task.FromResult(new SerialResult(outcome, detail: detail));

        private static SerialOutcome OutcomeFor(SerialConnectionState state)
        {
            switch (state)
            {
                case SerialConnectionState.Released: return SerialOutcome.PortReleased;
                case SerialConnectionState.Busy:     return SerialOutcome.PortBusy;
                case SerialConnectionState.ShutDown: return SerialOutcome.ShuttingDown;
                default:                             return SerialOutcome.NotConnected;
            }
        }

        private static string DetailFor(SerialConnectionState state)
        {
            switch (state)
            {
                case SerialConnectionState.Released: return "the port is released";
                case SerialConnectionState.Busy:     return "another application holds the port";
                case SerialConnectionState.ShutDown: return "the plugin is shutting down";
                default:                             return "the board is not connected";
            }
        }

        private static long ElapsedMs(long startTicks)
            => (Stopwatch.GetTimestamp() - startTicks) * 1000 / Stopwatch.Frequency;
    }
}

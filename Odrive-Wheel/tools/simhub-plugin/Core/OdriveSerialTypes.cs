using System;
using System.Diagnostics;
using System.Threading;

namespace OdriveWheel.SimHubPlugin
{
    public enum CommandPriority
    {
        /// <summary>A person is waiting: panel, window, mapped actions.</summary>
        Interactive = 0,
        /// <summary>Profile apply and bulk read, one line at a time.</summary>
        Batch = 1,
        /// <summary>Status polling.</summary>
        Background = 2,
    }

    public enum ReplyMode
    {
        /// <summary>ODrive read: one plain line. A line starting with '[' is a stale OpenFFBoard reply.</summary>
        OdriveLine,
        /// <summary>OpenFFBoard: "[head|value]", accepted only if it starts with the expected echo.</summary>
        OffbEcho,
        /// <summary>ODrive write: silent on success, any plain line in the window is a rejection.</summary>
        SilenceIsOk,
    }

    public enum SerialOutcome
    {
        Reply,
        Silence,
        Timeout,
        Cancelled,
        NotConnected,
        PortReleased,
        PortBusy,
        TransportError,
        QueueFull,
        BlockedByStop,
        ShuttingDown,
    }

    public enum SerialConnectionState
    {
        NotStarted,
        Searching,
        Connecting,
        Connected,
        Busy,
        Released,
        ShutDown,
    }

    public enum StopState
    {
        /// <summary>The worker has not written the stop line yet.</summary>
        Requested,
        /// <summary>The stop line went out, the worker is checking the result.</summary>
        Written,
        /// <summary>Serial read-back or HID report IDLE.</summary>
        Confirmed,
        /// <summary>The stop line went out, but nothing reported IDLE.</summary>
        Unconfirmed,
        /// <summary>The stop line never reached the port.</summary>
        NotDelivered,
    }

    public sealed class SerialRequest
    {
        public SerialRequest(string line, ReplyMode mode, int timeoutMs,
                             CommandPriority priority = CommandPriority.Interactive,
                             string expectedEcho = null,
                             CancellationToken cancellation = default)
        {
            Line         = line ?? throw new ArgumentNullException(nameof(line));
            Mode         = mode;
            TimeoutMs    = timeoutMs;
            Priority     = priority;
            ExpectedEcho = expectedEcho;
            Cancellation = cancellation;
        }

        public string            Line         { get; }
        public ReplyMode         Mode         { get; }
        public int               TimeoutMs    { get; }
        public CommandPriority   Priority     { get; }
        public string            ExpectedEcho { get; }
        public CancellationToken Cancellation { get; }
    }

    public sealed class SerialResult
    {
        public SerialResult(SerialOutcome outcome, string reply = null, string detail = null, bool lineWritten = false)
        {
            Outcome     = outcome;
            Reply       = reply;
            Detail      = detail;
            LineWritten = lineWritten;
        }

        public SerialOutcome Outcome     { get; }
        public string        Reply       { get; }
        public string        Detail      { get; }
        /// <summary>True once the whole line went out, even if the link failed afterwards.</summary>
        public bool          LineWritten { get; }
    }

    /// <summary>Immutable picture of the connection, safe to read from any thread.</summary>
    public sealed class SerialStatus
    {
        public SerialStatus(SerialConnectionState state, string portName, int connectGeneration,
                            string lastError, StopTicket lastStop, bool stopLatched)
        {
            State             = state;
            PortName          = portName;
            ConnectGeneration = connectGeneration;
            LastError         = lastError;
            LastStop          = lastStop;
            StopLatched       = stopLatched;
        }

        public SerialConnectionState State { get; }
        /// <summary>The open port, otherwise the discovered one, otherwise null.</summary>
        public string     PortName          { get; }
        /// <summary>Counts successful connects; a change means the board may have rebooted.</summary>
        public int        ConnectGeneration { get; }
        public string     LastError         { get; }
        public StopTicket LastStop          { get; }
        public bool       StopLatched       { get; }
        public bool       IsConnected => State == SerialConnectionState.Connected;
    }

    /// <summary>Tracks one software stop. Any thread can read it, only the worker changes it.</summary>
    public sealed class StopTicket
    {
        private readonly long _requestedTicks = Stopwatch.GetTimestamp();
        private readonly ManualResetEventSlim _finished = new ManualResetEventSlim(false);
        private volatile int _state = (int)StopState.Requested;
        private volatile string _detail;
        private long _writtenAfterMs = -1;

        public StopState State  => (StopState)_state;
        public string    Detail => _detail;
        public bool      IsFinished => _finished.IsSet;
        public long      AgeMs => ElapsedMs(_requestedTicks);

        /// <summary>Milliseconds from the request to the written stop line, -1 if not written.</summary>
        public long WrittenAfterMs => Interlocked.Read(ref _writtenAfterMs);

        public bool WaitForResult(int timeoutMs) => _finished.Wait(timeoutMs);

        internal void MarkWritten()
        {
            if (Interlocked.CompareExchange(ref _writtenAfterMs, AgeMs, -1) == -1)
                _state = (int)StopState.Written;
        }

        internal void Finish(StopState state, string detail)
        {
            _detail = detail;
            _state  = (int)state;
            _finished.Set();
        }

        private static long ElapsedMs(long startTicks)
            => (Stopwatch.GetTimestamp() - startTicks) * 1000 / Stopwatch.Frequency;
    }

    /// <summary>Timing of the serial service. Tests shorten the waits.</summary>
    public sealed class SerialServiceOptions
    {
        /// <summary>Longest uninterruptible read, the main part of the stop delay while waiting for a reply.</summary>
        public int ReadSliceMs         { get; set; } = 10;
        /// <summary>Longest uninterruptible write, the upper limit of the stop delay.</summary>
        public int WriteTimeoutMs      { get; set; } = 200;
        public int IdleWaitMs          { get; set; } = 100;
        public int MaxQueueWaitMs      { get; set; } = 2000;
        public int MaxQueuedCommands   { get; set; } = 256;
        /// <summary>Quiet period after a timeout, so a late reply cannot reach the next command.</summary>
        public int DrainMs             { get; set; } = 30;
        /// <summary>Line-settle delay after opening: a CDC that resets RX on DTR would eat the first character.</summary>
        public int OpenSettleMs        { get; set; } = 20;
        public int ReconnectIntervalMs { get; set; } = 1000;
        public int PortScanIntervalMs  { get; set; } = 2000;
        /// <summary>Consecutive scans without the board before an open port counts as gone.</summary>
        public int PortMissLimit       { get; set; } = 2;
        public int StopAttempts        { get; set; } = 3;
        public int StopReadbackMs      { get; set; } = 150;
    }
}

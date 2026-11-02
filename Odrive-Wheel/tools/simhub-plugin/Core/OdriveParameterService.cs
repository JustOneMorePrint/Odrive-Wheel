using System;
using System.Threading;
using System.Threading.Tasks;

namespace OdriveWheel.SimHubPlugin
{
    public enum ParameterStatus
    {
        Ok,
        /// <summary>The board answered and refused: ERR, NOT_FOUND, invalid property, ...</summary>
        Rejected,
        Timeout,
        Cancelled,
        /// <summary>The command never reached the board or the link failed.</summary>
        Unavailable,
        BlockedByStop,
        /// <summary>The path or value contains characters that are not allowed.</summary>
        InvalidInput,
    }

    public sealed class ParameterResult
    {
        public ParameterResult(ParameterStatus status, string value = null, string detail = null, bool lineWritten = false)
        {
            Status = status;
            Value  = value;
            Detail = detail;
            LineWritten = lineWritten;
        }

        public ParameterStatus Status { get; }
        public string Value  { get; }
        public string Detail { get; }
        public bool   IsOk => Status == ParameterStatus.Ok;
        /// <summary>The command went out. If it then failed, the board may still have acted on it.
        /// If the serial worker does not respond, this is not known, the detail says so.</summary>
        public bool   LineWritten { get; }
    }

    /// <summary>
    /// Reads and writes parameters in both dialects. The async methods are for the UI;
    /// the blocking ones are for background threads and must never run on a UI thread.
    /// </summary>
    public interface IOdriveParameters
    {
        Task<ParameterResult> ReadAsync(string path, FieldProtocol protocol,
                                        CommandPriority priority = CommandPriority.Interactive,
                                        CancellationToken cancellation = default,
                                        SerialOperationContext context = null);
        Task<ParameterResult> WriteAsync(string path, string value, FieldProtocol protocol,
                                         CommandPriority priority = CommandPriority.Interactive,
                                         CancellationToken cancellation = default,
                                         SerialOperationContext context = null);
        Task<ParameterResult> ExecOffbAsync(string path, CommandPriority priority = CommandPriority.Interactive,
                                            CancellationToken cancellation = default);
        Task<ParameterResult> SaveOdriveAsync();

        ParameterResult Read(string path, FieldProtocol protocol,
                             CommandPriority priority = CommandPriority.Interactive,
                             CancellationToken cancellation = default);
        ParameterResult Write(string path, string value, FieldProtocol protocol,
                              CommandPriority priority = CommandPriority.Interactive,
                              CancellationToken cancellation = default);
        ParameterResult ExecOffb(string path, CommandPriority priority = CommandPriority.Interactive,
                                 CancellationToken cancellation = default);

        /// <summary>Sends "ss". Ok means it went out: the firmware does not answer, and it
        /// ignores the command while the motor is armed.</summary>
        ParameterResult SaveOdrive();
    }

    public sealed class OdriveParameterService : IOdriveParameters
    {
        public const int ReadTimeoutMs        = 500;
        public const int OffbWriteTimeoutMs   = 500;
        public const int ExecTimeoutMs        = 2000;
        public const int OdriveWriteSilenceMs = 120;
        public const int SaveSilenceMs        = 200;

        // Safety net for the blocking calls: the worker completes every command on its own,
        // this only matters if it is stuck in a driver call. Covers the wait in the queue
        // and for the reply stream to get back in sync.
        private const int BlockingMarginMs = 5000;

        private readonly IOdriveSerialService _serial;
        private readonly Action<string> _log;

        public OdriveParameterService(IOdriveSerialService serial, Action<string> log = null)
        {
            _serial = serial ?? throw new ArgumentNullException(nameof(serial));
            _log    = log ?? (_ => { });
        }

        // ---- Async -----------------------------------------------------------

        public async Task<ParameterResult> ReadAsync(string path, FieldProtocol protocol,
                                                     CommandPriority priority = CommandPriority.Interactive,
                                                     CancellationToken cancellation = default,
                                                     SerialOperationContext context = null)
        {
            if (!OdriveProtocol.IsValidPath(path))
                return new ParameterResult(ParameterStatus.InvalidInput, detail: "invalid path: " + path);

            var request = protocol == FieldProtocol.Openffb
                ? new SerialRequest(OdriveProtocol.OffbRead(path), ReplyMode.OffbEcho, ReadTimeoutMs, priority,
                                    OdriveProtocol.OffbEcho(path, '?'), cancellation, context)
                : new SerialRequest(OdriveProtocol.OdriveRead(path), ReplyMode.OdriveLine, ReadTimeoutMs, priority,
                                    cancellation: cancellation, context: context);

            var result = await _serial.SendAsync(request).ConfigureAwait(false);
            return protocol == FieldProtocol.Openffb ? MapOffb(result) : MapOdriveRead(result);
        }

        public async Task<ParameterResult> WriteAsync(string path, string value, FieldProtocol protocol,
                                                      CommandPriority priority = CommandPriority.Interactive,
                                                      CancellationToken cancellation = default,
                                                      SerialOperationContext context = null)
        {
            if (!OdriveProtocol.IsValidPath(path))
                return new ParameterResult(ParameterStatus.InvalidInput, detail: "invalid path: " + path);
            if (!OdriveProtocol.IsValidValue(value))
                return new ParameterResult(ParameterStatus.InvalidInput, detail: "invalid value: " + value);

            if (protocol == FieldProtocol.Openffb)
            {
                var request = new SerialRequest(OdriveProtocol.OffbWrite(path, value), ReplyMode.OffbEcho,
                                                OffbWriteTimeoutMs, priority, OdriveProtocol.OffbEcho(path, '='),
                                                cancellation, context);
                return MapOffb(await _serial.SendAsync(request).ConfigureAwait(false));
            }
            else
            {
                var request = new SerialRequest(OdriveProtocol.OdriveWrite(path, value), ReplyMode.SilenceIsOk,
                                                OdriveWriteSilenceMs, priority, cancellation: cancellation, context: context);
                var result = await _serial.SendAsync(request).ConfigureAwait(false);
                if (result.Outcome == SerialOutcome.Silence) return new ParameterResult(ParameterStatus.Ok, value, lineWritten: true);
                if (result.Outcome == SerialOutcome.Reply)
                {
                    _log("write " + path + "=" + value + " rejected: " + result.Reply);
                    return new ParameterResult(ParameterStatus.Rejected, detail: result.Reply, lineWritten: true);
                }
                return MapFailure(result);
            }
        }

        public async Task<ParameterResult> ExecOffbAsync(string path, CommandPriority priority = CommandPriority.Interactive,
                                                         CancellationToken cancellation = default)
        {
            if (!OdriveProtocol.IsValidPath(path))
                return new ParameterResult(ParameterStatus.InvalidInput, detail: "invalid path: " + path);

            var request = new SerialRequest(OdriveProtocol.OffbExec(path), ReplyMode.OffbEcho, ExecTimeoutMs, priority,
                                            OdriveProtocol.OffbEcho(path, '!'), cancellation);
            return MapOffb(await _serial.SendAsync(request).ConfigureAwait(false));
        }

        public async Task<ParameterResult> SaveOdriveAsync()
        {
            var request = new SerialRequest("ss", ReplyMode.SilenceIsOk, SaveSilenceMs);
            var result = await _serial.SendAsync(request).ConfigureAwait(false);
            if (result.Outcome == SerialOutcome.BlockedByStop) return MapFailure(result);
            // The board reboots right after saving, so a failing link afterwards is expected.
            return result.LineWritten
                ? new ParameterResult(ParameterStatus.Ok, lineWritten: true)
                : MapFailure(result);
        }

        // ---- Blocking --------------------------------------------------------

        public ParameterResult Read(string path, FieldProtocol protocol,
                                    CommandPriority priority = CommandPriority.Interactive,
                                    CancellationToken cancellation = default)
            => Wait(ReadAsync(path, protocol, priority, cancellation), ReadTimeoutMs);

        public ParameterResult Write(string path, string value, FieldProtocol protocol,
                                     CommandPriority priority = CommandPriority.Interactive,
                                     CancellationToken cancellation = default)
            => Wait(WriteAsync(path, value, protocol, priority, cancellation), OffbWriteTimeoutMs);

        public ParameterResult ExecOffb(string path, CommandPriority priority = CommandPriority.Interactive,
                                        CancellationToken cancellation = default)
            => Wait(ExecOffbAsync(path, priority, cancellation), ExecTimeoutMs);

        public ParameterResult SaveOdrive() => Wait(SaveOdriveAsync(), SaveSilenceMs);

        private static ParameterResult Wait(Task<ParameterResult> task, int timeoutMs)
        {
            return task.Wait(timeoutMs + BlockingMarginMs)
                ? task.Result
                : new ParameterResult(ParameterStatus.Unavailable, detail: "the serial worker does not respond, the command may have gone out");
        }

        // ---- Mapping ---------------------------------------------------------

        private static ParameterResult MapOffb(SerialResult result)
        {
            if (result.Outcome != SerialOutcome.Reply) return MapFailure(result);
            var value = OdriveProtocol.ExtractOffbValue(result.Reply);
            if (value == null) return new ParameterResult(ParameterStatus.Rejected, detail: result.Reply, lineWritten: true);
            return OdriveProtocol.IsOffbFailure(value)
                ? new ParameterResult(ParameterStatus.Rejected, detail: value, lineWritten: true)
                : new ParameterResult(ParameterStatus.Ok, value, lineWritten: true);
        }

        private static ParameterResult MapOdriveRead(SerialResult result)
        {
            if (result.Outcome != SerialOutcome.Reply) return MapFailure(result);
            var reply = result.Reply.Trim();
            return OdriveProtocol.IsOdriveFailure(reply)
                ? new ParameterResult(ParameterStatus.Rejected, detail: reply, lineWritten: true)
                : new ParameterResult(ParameterStatus.Ok, reply, lineWritten: true);
        }

        private static ParameterResult MapFailure(SerialResult result)
        {
            switch (result.Outcome)
            {
                case SerialOutcome.Timeout:
                    return new ParameterResult(ParameterStatus.Timeout, detail: result.Detail ?? "no reply", lineWritten: result.LineWritten);
                case SerialOutcome.Cancelled:
                    return new ParameterResult(ParameterStatus.Cancelled, detail: result.Detail ?? "cancelled", lineWritten: result.LineWritten);
                case SerialOutcome.BlockedByStop:
                    return new ParameterResult(ParameterStatus.BlockedByStop, detail: result.Detail);
                default:
                    return new ParameterResult(ParameterStatus.Unavailable, detail: result.Detail ?? result.Outcome.ToString(), lineWritten: result.LineWritten);
            }
        }
    }
}

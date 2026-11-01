using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace OdriveWheel.SimHubPlugin.Tests
{
    /// <summary>
    /// Stands in for the COM port and the board. Answers like the firmware does
    /// (cmdparser.c, ascii_protocol.cpp), and can stall, stay silent, lose the link
    /// or refuse the port. No real port is involved anywhere in this project.
    /// </summary>
    internal sealed class FakeBoard : ISerialTransport, IPortLocator
    {
        private readonly object _lock = new object();
        private readonly List<string> _lines = new List<string>();
        private readonly List<(long dueMs, string line)> _replies = new List<(long, string)>();
        private readonly Dictionary<string, string> _values = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
        private readonly ManualResetEventSlim _writeGate = new ManualResetEventSlim(true);

        public FakeBoard()
        {
            _values["vbus_voltage"]                   = "48.000";
            _values["axis0.current_state"]            = "8";
            _values["axis1.current_state"]            = "1";
            _values["axis0.requested_state"]          = "0";
            _values["axis1.requested_state"]          = "0";
            _values["axis0.error"]                    = "0";
            _values["axis0.motor.error"]              = "0";
            _values["axis0.encoder.error"]            = "0";
            _values["axis0.controller.error"]         = "0";
            _values["axis0.motor.config.current_lim"] = "25.000";
            _values["axis0.motor.config.current_control_bandwidth"] = "1000.000";
            _values["axis0.encoder.config.bandwidth"] = "1000.000";
            _values["axis0.motor.config.phase_resistance"] = "0.100";
            _values["config.brake_resistance"]        = "2.000";
            foreach (var f in OdriveProfileFields.All)
                if (!_values.ContainsKey(f.Path)) _values[f.Path] = "1";
            _values["sys.swver"] = "1.17.0";
            _values["sys.build"] = "abc branch=test";
        }

        // ---- Test controls ---------------------------------------------------

        public string PortName { get; set; } = "COM99";
        public volatile bool Present = true;
        public volatile bool RefuseOpenAsBusy;
        public volatile bool FailOpen;
        public volatile bool Mute;
        public volatile bool IgnoreStop;
        public volatile int  ReplyDelayMs;
        public volatile bool FailNextRead;
        public volatile bool FailNextWrite;
        public volatile bool LocatorThrows;

        public int OpenCount;
        public int CloseCount;
        public int DiscardCount;
        public volatile bool IsOpen;
        public volatile bool Disposed;

        /// <summary>Set while FindPort should hang, like a stuck WMI query.</summary>
        public ManualResetEventSlim LocatorGate { get; } = new ManualResetEventSlim(true);

        /// <summary>Called for each received line; a non-null result replaces the normal reply.</summary>
        public Func<string, string> ReplyOverride { get; set; }

        /// <summary>Lets the next writes hang until their timeout, like a stalled USB endpoint.</summary>
        public void StallWrites() => _writeGate.Reset();
        public void ReleaseWrites() => _writeGate.Set();

        /// <summary>Lets Close hang until ReleaseClose.</summary>
        public ManualResetEventSlim CloseGate { get; } = new ManualResetEventSlim(true);

        /// <summary>Queues a line the board sends on its own, for example a late reply.</summary>
        public void Inject(string line, int delayMs = 0)
        {
            lock (_lock)
            {
                AddReply(_clock.ElapsedMilliseconds + delayMs, line);
                Monitor.PulseAll(_lock);
            }
        }

        // In order of arrival: a late reply arrives after one that was sent later but is faster.
        private void AddReply(long dueMs, string line)
        {
            int at = _replies.Count;
            while (at > 0 && _replies[at - 1].dueMs > dueMs) at--;
            _replies.Insert(at, (dueMs, line));
        }

        public string[] Lines
        {
            get { lock (_lock) return _lines.ToArray(); }
        }

        public void ClearLines()
        {
            lock (_lock) _lines.Clear();
        }

        public int CountLine(string line) => Lines.Count(l => l == line);

        public string Value(string path)
        {
            lock (_lock) return _values.TryGetValue(path, out var v) ? v : null;
        }

        public void SetValue(string path, string value)
        {
            lock (_lock) _values[path] = value;
        }

        // ---- IPortLocator ----------------------------------------------------

        public string FindPort()
        {
            LocatorGate.Wait();
            if (LocatorThrows) throw new InvalidOperationException("WMI failed");
            return Present ? PortName : null;
        }

        // ---- ISerialTransport ------------------------------------------------

        public void Open(string portName)
        {
            if (RefuseOpenAsBusy) throw new PortBusyException("access denied");
            if (FailOpen || !Present) throw new TransportException("port does not exist");
            Interlocked.Increment(ref OpenCount);
            IsOpen = true;
        }

        public void WriteLine(string line, int timeoutMs)
        {
            if (!_writeGate.Wait(timeoutMs))
                throw new TransportWriteTimeoutException("write timed out");
            if (FailNextWrite)
            {
                FailNextWrite = false;
                throw new TransportException("device removed during write");
            }
            if (!IsOpen) throw new TransportException("port is closed");

            lock (_lock)
            {
                foreach (var part in line.Split('\n'))
                    Process(part);
                Monitor.PulseAll(_lock);
            }
        }

        public string ReadLine(int timeoutMs)
        {
            lock (_lock)
            {
                long deadline = _clock.ElapsedMilliseconds + timeoutMs;
                while (true)
                {
                    if (FailNextRead)
                    {
                        FailNextRead = false;
                        throw new TransportException("device removed during read");
                    }
                    if (!IsOpen) throw new TransportException("port is closed");

                    long now = _clock.ElapsedMilliseconds;
                    if (_replies.Count > 0 && _replies[0].dueMs <= now)
                    {
                        var line = _replies[0].line;
                        _replies.RemoveAt(0);
                        return line;
                    }

                    long wait = deadline - now;
                    if (wait <= 0) return null;
                    if (_replies.Count > 0) wait = Math.Min(wait, Math.Max(1, _replies[0].dueMs - now));
                    Monitor.Wait(_lock, (int)wait);
                }
            }
        }

        public void DiscardInput()
        {
            Interlocked.Increment(ref DiscardCount);
            lock (_lock)
            {
                long now = _clock.ElapsedMilliseconds;
                // Only what has arrived by now is gone, a late reply is still under way.
                _replies.RemoveAll(r => r.dueMs <= now);
            }
        }

        public void Close()
        {
            CloseGate.Wait();
            if (IsOpen) Interlocked.Increment(ref CloseCount);
            IsOpen = false;
            lock (_lock)
            {
                _replies.Clear();
                Monitor.PulseAll(_lock);
            }
        }

        public void Dispose()
        {
            Disposed = true;
            Close();
        }

        // ---- Firmware behaviour ----------------------------------------------

        private void Process(string line)
        {
            if (line.Length == 0)
            {
                Reply("unknown command");   // process_line with an empty buffer
                return;
            }
            _lines.Add(line);

            if (ReplyOverride != null)
            {
                var replaced = ReplyOverride(line);
                if (replaced != null)
                {
                    Reply(replaced);
                    return;
                }
            }

            int op = line.IndexOfAny(new[] { '?', '=', '!' });
            if (op >= 0 && line.IndexOf('.') >= 0)
            {
                ProcessOffb(line, op);
                return;
            }

            var parts = line.Split(' ');
            switch (parts[0])
            {
                case "r":
                    Reply(_values.TryGetValue(parts[1], out var value) ? value : "invalid property");
                    break;
                case "w":
                    ProcessOdriveWrite(parts[1], parts[2]);
                    break;
                case "ss":
                case "sc":
                    break;
                default:
                    Reply("unknown command");
                    break;
            }
        }

        private void ProcessOdriveWrite(string path, string value)
        {
            if (!_values.ContainsKey(path))
            {
                Reply("invalid property");
                return;
            }
            _values[path] = value;

            if (path.EndsWith(".requested_state", StringComparison.Ordinal) && !IgnoreStop)
            {
                var axis = path.Substring(0, path.IndexOf('.'));
                _values[axis + ".current_state"] = value;
            }
        }

        private void ProcessOffb(string line, int op)
        {
            var path = line.Substring(0, op);
            char kind = line[op];
            string head = kind == '=' ? line : path + kind;

            if (!_values.ContainsKey(path))
            {
                Reply("[" + head + "|NOT_FOUND]");
                return;
            }
            switch (kind)
            {
                case '?':
                    Reply("[" + head + "|" + _values[path] + "]");
                    break;
                case '=':
                    var value = line.Substring(op + 1);
                    _values[path] = value;
                    Reply("[" + head + "|" + value + "]");
                    break;
                default:
                    Reply("[" + head + "|OK]");
                    break;
            }
        }

        private void Reply(string text)
        {
            if (Mute) return;
            AddReply(_clock.ElapsedMilliseconds + ReplyDelayMs, text);
        }
    }

    internal static class FakeBoardFormat
    {
        public static string Inv(double value) => value.ToString(CultureInfo.InvariantCulture);
    }
}

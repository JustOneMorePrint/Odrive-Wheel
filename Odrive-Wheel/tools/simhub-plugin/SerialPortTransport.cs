using System;
using System.IO;
using System.IO.Ports;
using System.Text;
using System.Threading.Tasks;

namespace OdriveWheel.SimHubPlugin
{
    /// <summary>
    /// The real COM port. Turns every SerialPort failure into a TransportException,
    /// because an exception escaping a worker thread would take SimHub down.
    /// </summary>
    public sealed class SerialPortTransport : ISerialTransport
    {
        private const int BAUD = 115200;
        private const int CloseWaitMs = 500;

        private readonly Action<string> _log;
        private SerialPort _port;
        private int _readTimeoutMs;
        private int _writeTimeoutMs;

        public SerialPortTransport(Action<string> log)
        {
            _log = log ?? (_ => { });
        }

        public void Open(string portName)
        {
            Close();
            var port = new SerialPort(portName, BAUD, Parity.None, 8, StopBits.One)
            {
                NewLine      = "\n",
                ReadTimeout  = 10,
                WriteTimeout = 200,
                Handshake    = Handshake.None,
                // DTR/RTS MUST be true — matches what WebSerial in
                // Chrome/Edge sets by default. Many USB CDC ACM firmwares
                // (including TinyUSB on the STM32) treat DTR=false as
                // "host disconnected" and silently drop incoming bytes.
                DtrEnable    = true,
                RtsEnable    = true,
                Encoding     = Encoding.ASCII,
            };
            _readTimeoutMs  = port.ReadTimeout;
            _writeTimeoutMs = port.WriteTimeout;

            try
            {
                port.Open();
                // Closing the stream of a removed USB device can throw. In the finalizer
                // that would end the process; Close() below handles it on our terms.
                GC.SuppressFinalize(port.BaseStream);
                _port = port;
            }
            catch (UnauthorizedAccessException ex)
            {
                port.Dispose();
                throw new PortBusyException(portName + " is held by another application", ex);
            }
            catch (Exception ex) when (IsPortFailure(ex))
            {
                port.Dispose();
                throw new TransportException("could not open " + portName + ": " + ex.Message, ex);
            }
        }

        public void WriteLine(string line, int timeoutMs)
        {
            var port = RequirePort();
            try
            {
                if (timeoutMs != _writeTimeoutMs)
                {
                    port.WriteTimeout = timeoutMs;
                    _writeTimeoutMs = timeoutMs;
                }
                port.WriteLine(line);
            }
            catch (TimeoutException ex)
            {
                throw new TransportWriteTimeoutException("write did not finish in " + timeoutMs + " ms", ex);
            }
            catch (Exception ex) when (IsPortFailure(ex))
            {
                throw new TransportException("write failed: " + ex.Message, ex);
            }
        }

        public string ReadLine(int timeoutMs)
        {
            var port = RequirePort();
            try
            {
                if (timeoutMs != _readTimeoutMs)
                {
                    port.ReadTimeout = timeoutMs;
                    _readTimeoutMs = timeoutMs;
                }
                return port.ReadLine();
            }
            catch (TimeoutException)
            {
                return null;   // a partial line stays in SerialPort's buffer
            }
            catch (Exception ex) when (IsPortFailure(ex))
            {
                throw new TransportException("read failed: " + ex.Message, ex);
            }
        }

        public void DiscardInput()
        {
            var port = RequirePort();
            try
            {
                port.DiscardInBuffer();
            }
            catch (Exception ex) when (IsPortFailure(ex))
            {
                throw new TransportException("discard failed: " + ex.Message, ex);
            }
        }

        public void Close()
        {
            var port = _port;
            if (port == null) return;
            _port = null;

            // Close can hang on a removed device. The worker must not hang with it,
            // or the software stop would become unreachable.
            var closing = Task.Run(() =>
            {
                try { port.Close(); } catch (Exception ex) { _log("port close: " + ex.Message); }
                try { port.Dispose(); } catch (Exception ex) { _log("port dispose: " + ex.Message); }
            });
            if (!closing.Wait(CloseWaitMs)) _log("port close did not finish in " + CloseWaitMs + " ms");
        }

        public void Dispose() => Close();

        private SerialPort RequirePort()
        {
            return _port ?? throw new TransportException("the port is not open");
        }

        private static bool IsPortFailure(Exception ex)
        {
            return ex is IOException
                || ex is InvalidOperationException
                || ex is UnauthorizedAccessException
                || ex is ArgumentException
                || ex is ObjectDisposedException;
        }
    }
}

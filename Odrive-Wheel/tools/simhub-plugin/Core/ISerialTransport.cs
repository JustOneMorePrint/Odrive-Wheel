using System;

namespace OdriveWheel.SimHubPlugin
{
    /// <summary>The serial link failed; the port is unusable until it is reopened.</summary>
    public class TransportException : Exception
    {
        public TransportException(string message, Exception inner = null) : base(message, inner) { }
    }

    /// <summary>Another application holds the port.</summary>
    public sealed class PortBusyException : TransportException
    {
        public PortBusyException(string message, Exception inner = null) : base(message, inner) { }
    }

    /// <summary>
    /// A write did not finish in time. The link can still be fine, for example
    /// while the board erases flash, but part of the line may have gone out.
    /// </summary>
    public sealed class TransportWriteTimeoutException : TransportException
    {
        public TransportWriteTimeoutException(string message, Exception inner = null) : base(message, inner) { }
    }

    /// <summary>
    /// One line-based serial connection. Only OdriveSerialService uses it, and only
    /// from its worker thread, so implementations need no locking.
    /// </summary>
    public interface ISerialTransport : IDisposable
    {
        /// <exception cref="PortBusyException">Another application holds the port.</exception>
        /// <exception cref="TransportException">The port could not be opened.</exception>
        void Open(string portName);

        /// <exception cref="TransportWriteTimeoutException">The write did not finish in time.</exception>
        /// <exception cref="TransportException">The link failed.</exception>
        void WriteLine(string line, int timeoutMs);

        /// <summary>Returns one line without its terminator, or null if none arrived in time.
        /// A partial line stays buffered for the next call.</summary>
        /// <exception cref="TransportException">The link failed.</exception>
        string ReadLine(int timeoutMs);

        /// <exception cref="TransportException">The link failed.</exception>
        void DiscardInput();

        /// <summary>Never throws.</summary>
        void Close();
    }

    /// <summary>Finds the board's COM port. May be slow, so it never runs on the worker thread.</summary>
    public interface IPortLocator
    {
        /// <summary>Returns the port name, or null if the board is not present.</summary>
        string FindPort();
    }
}

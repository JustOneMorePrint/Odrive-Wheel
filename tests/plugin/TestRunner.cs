using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace OdriveWheel.SimHubPlugin.Tests
{
    /// <summary>
    /// Runs every public static method named Test_*. Same output style as tests/host: a
    /// failed check prints where it failed, the exit code tells CI. An optional argument
    /// filters by name. A hanging test fails on its time limit, it doesn't hang the run.
    /// </summary>
    internal static class Program
    {
        private const int TestLimitMs = 30000;

        private static int Main(string[] args)
        {
            var filter = args.Length > 0 ? args[0] : null;
            var tests = typeof(Program).Assembly.GetTypes()
                .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
                .Where(m => m.Name.StartsWith("Test_", StringComparison.Ordinal))
                .Where(m => filter == null || m.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(m => m.DeclaringType.Name).ThenBy(m => m.Name)
                .ToList();

            var watch = Stopwatch.StartNew();
            foreach (var test in tests)
                Run(test);

            Console.WriteLine("plugin tests: {0} tests, {1} checks, {2} failed, {3:F1} s",
                              tests.Count, Check.Count, Check.Failures, watch.Elapsed.TotalSeconds);

            // Tests with hanging fakes may leave threads behind on purpose.
            Environment.Exit(Check.Failures == 0 && tests.Count > 0 ? 0 : 1);
            return 0;
        }

        private static void Run(MethodInfo test)
        {
            Check.CurrentTest = test.DeclaringType.Name + "." + test.Name;
            var thread = new Thread(() =>
            {
                try
                {
                    test.Invoke(null, null);
                }
                catch (TargetInvocationException ex)
                {
                    Check.Fail("threw " + ex.InnerException);
                }
            })
            {
                IsBackground = true,
                Name = test.Name,
            };
            thread.Start();
            if (!thread.Join(TestLimitMs))
                Check.Fail("did not finish within " + TestLimitMs + " ms");
        }
    }

    internal static class Check
    {
        private static int _count;
        private static int _failures;

        public static int Count => Volatile.Read(ref _count);
        public static int Failures => Volatile.Read(ref _failures);
        public static string CurrentTest;

        public static void True(bool condition, string what)
        {
            Interlocked.Increment(ref _count);
            if (!condition) Fail(what);
        }

        public static void Equal<T>(T expected, T actual, string what)
        {
            Interlocked.Increment(ref _count);
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                Fail(what + "\n  expected: " + expected + "\n  actual:   " + actual);
        }

        public static void Lines(string[] expected, string[] actual, string what)
        {
            Interlocked.Increment(ref _count);
            if (!expected.SequenceEqual(actual))
                Fail(what + "\n  expected: " + string.Join(" | ", expected) + "\n  actual:   " + string.Join(" | ", actual));
        }

        public static void Fail(string what)
        {
            Interlocked.Increment(ref _failures);
            Console.WriteLine("FAILED: " + CurrentTest + ": " + what);
        }
    }

    internal static class Wait
    {
        public static bool Until(Func<bool> condition, int timeoutMs = 3000)
        {
            var watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < timeoutMs)
            {
                if (condition()) return true;
                Thread.Sleep(2);
            }
            return condition();
        }
    }

    /// <summary>A started service on a fake board, with short waits where timing doesn't matter.</summary>
    internal sealed class Rig : IDisposable
    {
        public FakeBoard Board { get; } = new FakeBoard();
        public OdriveSerialService Service { get; }
        public OdriveParameterService Parameters { get; }
        public List<string> Log { get; } = new List<string>();

        /// <summary>What the HID stream reports, null without fresh reports.</summary>
        public int? HidState;

        public Rig(Action<SerialServiceOptions> tune = null, bool connect = true, Action<FakeBoard> prepare = null)
        {
            var options = new SerialServiceOptions
            {
                PortScanIntervalMs  = 10,
                ReconnectIntervalMs = 20,
                IdleWaitMs          = 20,
                OpenSettleMs        = 1,
            };
            tune?.Invoke(options);
            prepare?.Invoke(Board);

            Service = new OdriveSerialService(Board, Board, () => HidState, options, AddLog);
            Parameters = new OdriveParameterService(Service, AddLog);
            Service.Start();
            if (connect) WaitConnected();
        }

        public void WaitConnected()
        {
            Check.True(Wait.Until(() => Service.GetStatus().IsConnected), "the service should connect");
        }

        public Task<SerialResult> Send(string line, ReplyMode mode = ReplyMode.OdriveLine, int timeoutMs = 500,
                                       CommandPriority priority = CommandPriority.Interactive,
                                       string echo = null, CancellationToken cancellation = default)
            => Service.SendAsync(new SerialRequest(line, mode, timeoutMs, priority, echo, cancellation));

        public void Dispose()
        {
            Board.ReleaseWrites();
            Board.CloseGate.Set();
            Board.LocatorGate.Set();
            Service.Shutdown(2000);
        }

        private void AddLog(string line)
        {
            lock (Log) Log.Add(line);
        }
    }
}

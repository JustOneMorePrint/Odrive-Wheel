using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace OdriveWheel.SimHubPlugin
{
    /// <summary>
    /// The two command dialects of the firmware, which share one serial line:
    ///
    ///   OpenFFBoard (class.property):
    ///     Read:  "axis.range?"     → "[axis.range?|900]"
    ///     Write: "axis.range=900"  → "[axis.range=900|900]"
    ///     Exec:  "sys.save!"       → "[sys.save!|OK]"
    ///     Failure replies carry ERR or NOT_FOUND as value (cmdparser.c).
    ///
    ///   ODrive ASCII (deep paths like axis0.motor.config.*):
    ///     Read:  "r path"          → one plain line
    ///     Write: "w path value"    → silent on success, plain error line otherwise
    ///     "ss" persists the config and reboots, but does nothing while the motor is armed.
    /// </summary>
    public static class OdriveProtocol
    {
        // The firmware splits commands at ';' and line ends, treats '*' as checksum and
        // routes any line with '.' and one of ?=! to the OpenFFBoard parser. Values and
        // paths must not be able to smuggle in a second command.
        private static readonly Regex PathPattern  = new Regex(@"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z0-9_]+)*$", RegexOptions.Compiled);
        private static readonly Regex ValuePattern = new Regex(@"^[A-Za-z0-9+\-._]+$", RegexOptions.Compiled);

        public static bool IsValidPath(string path)   => path != null && PathPattern.IsMatch(path);
        public static bool IsValidValue(string value) => value != null && ValuePattern.IsMatch(value);

        public static string OdriveRead(string path)                => "r " + path;
        public static string OdriveWrite(string path, string value) => "w " + path + " " + value;
        public static string OffbRead(string path)                  => path + "?";
        public static string OffbWrite(string path, string value)   => path + "=" + value;
        public static string OffbExec(string path)                  => path + "!";

        /// <summary>What an OpenFFBoard reply to this line starts with. A write echo is only
        /// compared up to the '=', because the firmware may cut long values.</summary>
        public static string OffbEcho(string path, char kind) => "[" + path + kind;

        /// <summary>Extracts VALUE from "[head|VALUE]". Returns null if the reply has another form.</summary>
        public static string ExtractOffbValue(string reply)
        {
            if (string.IsNullOrEmpty(reply)) return null;
            var match = Regex.Match(reply, @"^\[[^|]+\|([\s\S]*)\]$");
            return match.Success ? match.Groups[1].Value : null;
        }

        // The only way to tell where a reply stream stands, and the firmware has two of them.
        // ODrive replies run through a buffered sink, OpenFFBoard replies go straight to the USB
        // FIFO (process_line, cmdparser_cdc_write). Each is in order, but one can overtake the
        // other, so an echo says something about its own path only.
        //
        // ODrive replies carry no echo, but "f <n>" with n above AXIS_COUNT reads nothing,
        // changes nothing and answers "invalid motor <n>" through the sink (cmd_get_feedback).
        public static string SyncRequest(uint marker) => "f " + marker.ToString(CultureInfo.InvariantCulture);
        public static string SyncEcho(uint marker) => SyncEchoPrefix + marker.ToString(CultureInfo.InvariantCulture);
        public static bool IsSyncEcho(string line) => line != null && line.StartsWith(SyncEchoPrefix, StringComparison.Ordinal);

        public static bool TryParseSyncEcho(string line, out uint marker)
        {
            marker = 0;
            return IsSyncEcho(line)
                && uint.TryParse(line.Substring(SyncEchoPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out marker);
        }

        private const string SyncEchoPrefix = "invalid motor ";

        // An OpenFFBoard echo names the path, not the request: a late reply looks exactly like
        // the current one. A class the firmware doesn't have runs no handler and answers
        // NOT_FOUND with the whole head, so a counter in the name makes that reply unique (parse_one).
        public static string OffbSyncRequest(uint marker) => OffbSyncHead + marker.ToString(CultureInfo.InvariantCulture) + "?";
        public static string OffbSyncEcho(uint marker) => "[" + OffbSyncRequest(marker) + OffbSyncEchoEnd;
        public static bool IsOffbSyncEcho(string line) => line != null && line.StartsWith("[" + OffbSyncHead, StringComparison.Ordinal);

        public static bool TryParseOffbSyncEcho(string line, out uint marker)
        {
            marker = 0;
            if (!IsOffbSyncEcho(line) || !line.EndsWith("?" + OffbSyncEchoEnd, StringComparison.Ordinal)) return false;
            int start = OffbSyncHead.Length + 1;
            int end = line.Length - OffbSyncEchoEnd.Length - 1;
            return uint.TryParse(line.Substring(start, end - start), NumberStyles.None, CultureInfo.InvariantCulture, out marker);
        }

        private const string OffbSyncHead = "odwsync.m";
        private const string OffbSyncEchoEnd = "|NOT_FOUND]";

        public static bool IsOffbFailure(string value) => value == "ERR" || value == "NOT_FOUND";

        /// <summary>True for the error texts of ascii_protocol.cpp. Note that a read can answer
        /// "not implemented" just because the value does not fit its 10-byte buffer.</summary>
        public static bool IsOdriveFailure(string reply)
        {
            if (reply == null) return false;
            return reply.StartsWith("invalid", StringComparison.Ordinal)
                || reply == "not implemented"
                || reply == "unknown command";
        }
    }
}

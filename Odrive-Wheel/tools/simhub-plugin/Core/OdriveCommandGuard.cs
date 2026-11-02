using System;
using System.Collections.Generic;

namespace OdriveWheel.SimHubPlugin
{
    /// <summary>
    /// Decides which lines the plugin refuses while the software stop latch is set:
    /// everything that reboots the board or can set the motor moving. A reboot counts
    /// because startup_closed_loop_control re-arms the motor on boot. Follows the line
    /// routing of the firmware (ascii_protocol.cpp, process_line).
    /// </summary>
    public static class OdriveCommandGuard
    {
        private static readonly char[] OffbOperators = { '?', '=', '!' };

        private static readonly HashSet<string> BlockedOffbCommands = new HashSet<string>(StringComparer.Ordinal)
        {
            "sys.reboot",
            "sys.format",
            "axis.anticogcal",
        };

        public static bool IsBlockedWhileStopped(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return false;
            var text = line.Trim();

            // The firmware treats a line with '.' and an operator as OpenFFBoard command.
            int op = text.IndexOfAny(OffbOperators);
            if (op >= 0 && text.IndexOf('.') >= 0)
                return BlockedOffbCommands.Contains(text.Substring(0, op));

            switch (text[0])
            {
                case 's':   // ss save + reboot, sr reboot, se erase, sd DFU; sc only clears errors
                    return text.Length >= 2 && "sred".IndexOf(text[1]) >= 0;
                case 'w':
                    return IsArmingWrite(text);
                case 'p':   // position, velocity, torque and trajectory commands
                case 'q':
                case 'v':
                case 'c':
                case 't':
                case 'Y':   // anticogging calibration
                    return true;
                default:
                    return false;
            }
        }

        private static bool IsArmingWrite(string text)
        {
            var parts = text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) return false;
            bool isStatePath = parts[1].EndsWith(".requested_state", StringComparison.Ordinal);
            return isStatePath && parts[2] != "1";
        }
    }
}

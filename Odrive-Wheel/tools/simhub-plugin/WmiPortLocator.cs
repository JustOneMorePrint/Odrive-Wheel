using System;
using System.Management;
using System.Text.RegularExpressions;

namespace OdriveWheel.SimHubPlugin
{
    /// <summary>
    /// Enumerates USB devices via WMI, matches by VID/PID, extracts COMn.
    /// The relevant Win32_PnPEntity rows have a Name like
    ///   "USB Serial Device (COM12)"
    /// and a DeviceID starting with "USB\VID_1209&amp;PID_0D40\...".
    /// A query takes about 100 ms and can stall, so it runs on its own thread.
    /// </summary>
    public sealed class WmiPortLocator : IPortLocator
    {
        private const int VID = 0x1209;
        private const int PID = 0x0D40;

        public string FindPort()
        {
            string vidpid = string.Format("VID_{0:X4}&PID_{1:X4}", VID, PID);

            using (var searcher = new ManagementObjectSearcher(
                "SELECT DeviceID, Name FROM Win32_PnPEntity WHERE Name LIKE '%(COM%)'"))
            {
                foreach (ManagementObject obj in searcher.Get())
                {
                    var deviceId = obj["DeviceID"] as string ?? "";
                    var name     = obj["Name"]     as string ?? "";
                    if (deviceId.IndexOf(vidpid, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    var m = Regex.Match(name, @"\((COM\d+)\)");
                    if (m.Success) return m.Groups[1].Value;
                }
            }
            return null;
        }
    }
}

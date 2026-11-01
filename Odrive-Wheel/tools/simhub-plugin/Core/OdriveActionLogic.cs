using System;
using System.Globalization;

namespace OdriveWheel.SimHubPlugin
{
    /// <summary>
    /// What the mappable SimHub actions do. Each inc/dec follows the same recipe:
    ///   1. lock (so back-to-back button presses can't interleave)
    ///   2. read current value from the ProfileService cache (instant —
    ///      the plugin is the sole writer, so the cache is authoritative
    ///      unless the user has been tuning in the HTML tool, in which
    ///      case they hit the manual refresh button first)
    ///   3. parse → clamp(current + delta) → format
    ///   4. serial write
    ///   5. PokeCache with the newly written value — dashboards and the
    ///      settings panel reflect the change immediately, no extra read
    ///
    /// Blocks its caller for the serial round trip, so it runs on SimHub's
    /// action thread and never on a UI thread.
    /// </summary>
    public sealed class OdriveActionLogic
    {
        private readonly IOdriveParameters _parameters;
        private readonly OdriveProfileService _profiles;
        private readonly Action<string> _log;
        private readonly object _incLock = new object();

        public OdriveActionLogic(IOdriveParameters parameters, OdriveProfileService profiles, Action<string> log)
        {
            _parameters = parameters;
            _profiles   = profiles;
            _log        = log ?? (_ => { });
        }

        public void IncrementInt(string path, int delta, int min, int max, string actionName)
        {
            lock (_incLock)
            {
                // Cache-first. Cache empty means initial read hasn't finished
                // (device just plugged in) — fall back to one serial read.
                var raw = _profiles.GetRaw(path) ?? _parameters.Read(path, FieldProtocol.Openffb).Value;
                if (raw == null)
                {
                    _log(actionName + ": no value known (device not connected?)");
                    return;
                }
                if (!int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int cur))
                {
                    // Some ints come back with decimals (e.g. "255.000000").
                    if (!double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double dcur))
                    {
                        _log(actionName + ": parse failed '" + raw + "'");
                        return;
                    }
                    cur = (int)dcur;
                }
                int next = cur + delta;
                if (next < min) next = min;
                if (next > max) next = max;
                if (next == cur)
                {
                    _log(actionName + ": already at limit (" + cur + ")");
                    return;
                }
                var nextStr = next.ToString(CultureInfo.InvariantCulture);
                var result = _parameters.Write(path, nextStr, FieldProtocol.Openffb);
                if (result.IsOk)
                {
                    _profiles.PokeCache(path, nextStr);
                    _log(actionName + ": " + cur + " → " + next);
                }
                else
                {
                    _log(actionName + ": write rejected (" + result.Detail + ")");
                }
            }
        }

        public void IncrementFloat(string path, double delta, double min, double max, string fmt, string actionName)
        {
            lock (_incLock)
            {
                // Cache-first (see IncrementInt for the rationale).
                var raw = _profiles.GetRaw(path) ?? _parameters.Read(path, FieldProtocol.Openffb).Value;
                if (raw == null)
                {
                    _log(actionName + ": no value known (device not connected?)");
                    return;
                }
                if (!double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double cur))
                {
                    _log(actionName + ": parse failed '" + raw + "'");
                    return;
                }
                double next = cur + delta;
                if (next < min) next = min;
                if (next > max) next = max;
                // Guard against float dust — treat sub-quantum change as no-op.
                if (Math.Abs(next - cur) < Math.Abs(delta) * 0.01)
                {
                    _log(actionName + ": already at limit (" + cur.ToString(fmt, CultureInfo.InvariantCulture) + ")");
                    return;
                }
                var nextStr = next.ToString(fmt, CultureInfo.InvariantCulture);
                var result = _parameters.Write(path, nextStr, FieldProtocol.Openffb);
                if (result.IsOk)
                {
                    _profiles.PokeCache(path, nextStr);
                    _log(actionName + ": " + cur.ToString(fmt, CultureInfo.InvariantCulture) + " → " + nextStr);
                }
                else
                {
                    _log(actionName + ": write rejected (" + result.Detail + ")");
                }
            }
        }

        public void ZeroWheelPosition()
        {
            // axis.zeroenc! is a firmware-native exec that snapshots
            // the current wheel position and applies it as the
            // zero offset in one atomic op. No read-modify-write
            // needed from the plugin side.
            var result = _parameters.ExecOffb("axis.zeroenc");
            if (result.IsOk)
                _log("Odrive-Wheel: ZeroWheelPosition → " + result.Value);
            else
                _log("Odrive-Wheel: ZeroWheelPosition failed: " + result.Detail);
        }
    }
}

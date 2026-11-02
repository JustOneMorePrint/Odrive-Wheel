using System;
using SimHub.Plugins;

namespace OdriveWheel.SimHubPlugin
{
    /// <summary>
    /// SimHub mappable actions — increment/decrement of a curated set of
    /// live-tunable properties + one one-shot (Zero Wheel Position) + the
    /// software stop. The logic lives in OdriveActionLogic.
    ///
    /// SimHub renders the RegistrationName (the string passed to
    /// pluginManager.AddAction) under Controls & Events → Additional
    /// plugins → Odrive-Wheel Telemetry. Names use CamelCase and encode
    /// direction + step size in the identifier itself so the mapping UI
    /// is self-explanatory.
    /// </summary>
    public static class OdriveActions
    {
        // Registered from OdriveWheelPlugin.Init.
        public static void Register(PluginManager pm, OdriveWheelPlugin plugin)
        {
            // ---- FX Master (int, 0..255, ±10 fine / ±25 coarse) ----
            RegisterIncInt(pm, plugin, "FxMasterUpFine",     "fx.master",   +10, 0, 255);
            RegisterIncInt(pm, plugin, "FxMasterDownFine",   "fx.master",   -10, 0, 255);
            RegisterIncInt(pm, plugin, "FxMasterUpCoarse",   "fx.master",   +25, 0, 255);
            RegisterIncInt(pm, plugin, "FxMasterDownCoarse", "fx.master",   -25, 0, 255);

            // ---- FX Ratio (double, 0..1, ±0.05 fine / ±0.20 coarse) ----
            RegisterIncFloat(pm, plugin, "FxRatioUpFine",     "axis.fxratio", +0.05, 0.0, 1.0, "F2");
            RegisterIncFloat(pm, plugin, "FxRatioDownFine",   "axis.fxratio", -0.05, 0.0, 1.0, "F2");
            RegisterIncFloat(pm, plugin, "FxRatioUpCoarse",   "axis.fxratio", +0.20, 0.0, 1.0, "F2");
            RegisterIncFloat(pm, plugin, "FxRatioDownCoarse", "axis.fxratio", -0.20, 0.0, 1.0, "F2");

            // ---- EQ Weight (dB, -12..+12, ±0.5 fine / ±2 coarse) ----
            RegisterIncFloat(pm, plugin, "EqWeightUpFine",     "axis.eqweight", +0.5, -12.0, +12.0, "F1");
            RegisterIncFloat(pm, plugin, "EqWeightDownFine",   "axis.eqweight", -0.5, -12.0, +12.0, "F1");
            RegisterIncFloat(pm, plugin, "EqWeightUpCoarse",   "axis.eqweight", +2.0, -12.0, +12.0, "F1");
            RegisterIncFloat(pm, plugin, "EqWeightDownCoarse", "axis.eqweight", -2.0, -12.0, +12.0, "F1");

            // ---- EQ Chassis (dB, -12..+12, ±0.5 fine / ±2 coarse) ----
            RegisterIncFloat(pm, plugin, "EqChassisUpFine",     "axis.eqchassis", +0.5, -12.0, +12.0, "F1");
            RegisterIncFloat(pm, plugin, "EqChassisDownFine",   "axis.eqchassis", -0.5, -12.0, +12.0, "F1");
            RegisterIncFloat(pm, plugin, "EqChassisUpCoarse",   "axis.eqchassis", +2.0, -12.0, +12.0, "F1");
            RegisterIncFloat(pm, plugin, "EqChassisDownCoarse", "axis.eqchassis", -2.0, -12.0, +12.0, "F1");

            // ---- EQ Road (dB, -12..+12, ±0.5 fine / ±2 coarse) ----
            RegisterIncFloat(pm, plugin, "EqRoadUpFine",     "axis.eqroad", +0.5, -12.0, +12.0, "F1");
            RegisterIncFloat(pm, plugin, "EqRoadDownFine",   "axis.eqroad", -0.5, -12.0, +12.0, "F1");
            RegisterIncFloat(pm, plugin, "EqRoadUpCoarse",   "axis.eqroad", +2.0, -12.0, +12.0, "F1");
            RegisterIncFloat(pm, plugin, "EqRoadDownCoarse", "axis.eqroad", -2.0, -12.0, +12.0, "F1");

            // ---- Zero Wheel Position (one-shot) ----
            pm.AddAction<OdriveWheelPlugin>("ZeroWheelPosition", (a, b) =>
            {
                try { plugin.ActionLogic.ZeroWheelPosition(); }
                catch (Exception ex)
                {
                    SimHub.Logging.Current.Info("Odrive-Wheel: ZeroWheelPosition failed: " + ex.Message);
                }
            });

            // [pkg1] Software stop, so it can sit on a wheel button.
            pm.AddAction<OdriveWheelPlugin>("SoftwareStop", (a, b) =>
            {
                try { plugin.SerialService.RequestStop(); }
                catch (Exception ex)
                {
                    SimHub.Logging.Current.Info("Odrive-Wheel: SoftwareStop failed: " + ex.Message);
                }
            });
        }

        private static void RegisterIncInt(PluginManager pm, OdriveWheelPlugin plugin,
                                           string actionName, string path, int delta,
                                           int min, int max)
        {
            pm.AddAction<OdriveWheelPlugin>(actionName, (a, b) =>
            {
                try { plugin.ActionLogic.IncrementInt(path, delta, min, max, actionName); }
                catch (Exception ex) { SimHub.Logging.Current.Info(actionName + ": " + ex.Message); }
            });
        }

        private static void RegisterIncFloat(PluginManager pm, OdriveWheelPlugin plugin,
                                             string actionName, string path, double delta,
                                             double min, double max, string fmt)
        {
            pm.AddAction<OdriveWheelPlugin>(actionName, (a, b) =>
            {
                try { plugin.ActionLogic.IncrementFloat(path, delta, min, max, fmt, actionName); }
                catch (Exception ex) { SimHub.Logging.Current.Info(actionName + ": " + ex.Message); }
            });
        }
    }
}

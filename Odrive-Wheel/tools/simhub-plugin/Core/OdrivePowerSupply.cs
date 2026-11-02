using System;
using System.Collections.Generic;
using System.Globalization;

namespace OdriveWheel.SimHubPlugin
{
    /// <summary>
    /// What the user knows about the power supply. Hardware data, stored in the global plugin settings
    /// and not part of any profile. Nothing here reaches the board: this is DC supply current, not motor
    /// phase current, and it sets no board value. Null means unknown.
    /// </summary>
    public sealed class PowerSupplyData
    {
        public double? DcOutputVoltage        { get; set; }
        public double? ContinuousCurrent      { get; set; }
        public double? CurrentHeadroomPercent { get; set; }
        public bool    ShortTimeOverloadSupported { get; set; }
        public double? ShortTimeCurrent       { get; set; }
        public double? MaximumDurationMs      { get; set; }

        /// <summary>Continuous current × (1 − headroom / 100). Null if either value is unknown or invalid.</summary>
        public double? UsableContinuousCurrent()
        {
            if (!(ContinuousCurrent is double current) || !(CurrentHeadroomPercent is double headroom)) return null;
            if (!PowerSupplyValidation.IsPositive(current) || !PowerSupplyValidation.IsValidHeadroom(headroom)) return null;
            return current * (1.0 - headroom / 100.0);
        }

        /// <summary>The short-time current for calculations: null while the option is off. Not a continuous limit.</summary>
        public double? ShortTimeCurrentInUse() => ShortTimeOverloadSupported ? ShortTimeCurrent : null;

        /// <summary>The maximum duration for calculations: null while the option is off.</summary>
        public double? MaximumDurationInUse() => ShortTimeOverloadSupported ? MaximumDurationMs : null;
    }

    /// <summary>The power supply fields as typed, before they count as numbers.</summary>
    public sealed class PowerSupplyInput
    {
        public string DcOutputVoltage        { get; set; }
        public string ContinuousCurrent      { get; set; }
        public string CurrentHeadroomPercent { get; set; }
        public bool   ShortTimeOverloadSupported { get; set; }
        public string ShortTimeCurrent       { get; set; }
        public string MaximumDurationMs      { get; set; }
    }

    public static class PowerSupplyValidation
    {
        public const string VoltageName   = "DC Output Voltage";
        public const string ContinuousName = "Continuous Output Current";
        public const string HeadroomName  = "Current Headroom";
        public const string ShortTimeName = "Short-Time Current";
        public const string DurationName  = "Maximum Duration";

        internal static bool IsPositive(double value) => IsFinite(value) && value > 0.0;
        internal static bool IsValidHeadroom(double value) => IsFinite(value) && value >= 0.0 && value < 100.0;
        private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

        /// <summary>Turns the typed fields into data. An empty field stays unknown. Always invariant
        /// culture: a comma is not a decimal separator here, it is an error instead of another number.</summary>
        public static PowerSupplyData Parse(PowerSupplyInput input, out IReadOnlyList<string> errors)
        {
            var found = new List<string>();
            var data = new PowerSupplyData
            {
                DcOutputVoltage        = ParseField(input.DcOutputVoltage, VoltageName, found),
                ContinuousCurrent      = ParseField(input.ContinuousCurrent, ContinuousName, found),
                CurrentHeadroomPercent = ParseField(input.CurrentHeadroomPercent, HeadroomName, found),
                ShortTimeOverloadSupported = input.ShortTimeOverloadSupported,
            };

            // While the option is off these fields are not in use, so they cannot hold up the rest.
            var shortTimeErrors = input.ShortTimeOverloadSupported ? found : new List<string>();
            data.ShortTimeCurrent  = ParseField(input.ShortTimeCurrent, ShortTimeName, shortTimeErrors);
            data.MaximumDurationMs = ParseField(input.MaximumDurationMs, DurationName, shortTimeErrors);

            found.AddRange(Validate(data));
            errors = found;
            return data;
        }

        private static double? ParseField(string text, string name, List<string> errors)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && IsFinite(value))
                return value;
            errors.Add(name + " is not a finite number, use a point as decimal separator: " + text.Trim());
            return null;
        }

        public static IReadOnlyList<string> Validate(PowerSupplyData data)
        {
            var errors = new List<string>();
            if (data.DcOutputVoltage is double voltage && !IsPositive(voltage))
                errors.Add(VoltageName + " must be above 0.");
            if (data.ContinuousCurrent is double continuous && !IsPositive(continuous))
                errors.Add(ContinuousName + " must be above 0.");
            if (data.CurrentHeadroomPercent is double headroom && !IsValidHeadroom(headroom))
                errors.Add(HeadroomName + " must be at least 0 and below 100 %.");

            if (data.ShortTimeOverloadSupported)
            {
                if (data.ShortTimeCurrent is double shortTime)
                {
                    if (!IsPositive(shortTime))
                        errors.Add(ShortTimeName + " must be above 0.");
                    else if (data.ContinuousCurrent is double reference && IsPositive(reference) && shortTime < reference)
                        errors.Add(ShortTimeName + " must not be below the " + ContinuousName + ".");
                }
                if (data.MaximumDurationMs is double duration && !IsPositive(duration))
                    errors.Add(DurationName + " must be above 0.");
            }
            return errors;
        }

        /// <summary>The way a stored number goes back into a text field.</summary>
        public static string ToInputText(double? value)
            => value.HasValue ? value.Value.ToString("R", CultureInfo.InvariantCulture) : "";
    }

    public interface IPowerSupplyStore
    {
        PowerSupplyData Load();
        void Save(PowerSupplyData data);
    }

    /// <summary>Keeps the data in the plugin settings, next to everything else stored there.</summary>
    public sealed class PluginSettingsPowerSupplyStore : IPowerSupplyStore
    {
        private readonly Func<OdrivePluginSettings> _getSettings;
        private readonly Action _persist;

        public PluginSettingsPowerSupplyStore(Func<OdrivePluginSettings> getSettings, Action persist)
        {
            _getSettings = getSettings ?? throw new ArgumentNullException(nameof(getSettings));
            _persist     = persist ?? throw new ArgumentNullException(nameof(persist));
        }

        public PowerSupplyData Load() => _getSettings().PowerSupply ?? new PowerSupplyData();

        public void Save(PowerSupplyData data)
        {
            _getSettings().PowerSupply = data ?? throw new ArgumentNullException(nameof(data));
            _persist();
        }
    }

    /// <summary>For a window without plugin settings: lasts as long as the window.</summary>
    internal sealed class InMemoryPowerSupplyStore : IPowerSupplyStore
    {
        private PowerSupplyData _data = new PowerSupplyData();
        public PowerSupplyData Load() => _data;
        public void Save(PowerSupplyData data) => _data = data;
    }
}

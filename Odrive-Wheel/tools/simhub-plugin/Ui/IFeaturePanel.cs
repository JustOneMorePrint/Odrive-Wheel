using System;
using System.Collections.Generic;
using System.Windows;

namespace OdriveWheel.SimHubPlugin
{
    /// <summary>
    /// One package's controls in the "Extensions" tab of the configuration window.
    /// Their switches are global and not part of the profiles. CreateView runs on the
    /// window's own thread, so it must not use objects created on another UI thread.
    /// </summary>
    public interface IFeaturePanel
    {
        string Title { get; }
        FrameworkElement CreateView(IOdriveParameters parameters);
    }

    public static class FeaturePanels
    {
        /// <summary>A package adds its panel with one line here.</summary>
        public static readonly IReadOnlyList<Func<IFeaturePanel>> All = new Func<IFeaturePanel>[]
        {
        };
    }
}

namespace Gleanvolt.Chargers;

/// <summary>The charger's own setting on the shared <see cref="PluginSettings"/>.</summary>
public static class PluginSettingsExtensions
{
    extension(PluginSettings settings)
    {
        /// <summary>The charger's address: <c>&lt;Section&gt;:Host</c>, or what the page saved.</summary>
        public SavedSetting Address => settings.Get("host");
    }
}

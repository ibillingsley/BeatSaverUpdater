using System;
using BeatSaberMarkupLanguage.Attributes;
using BeatSaberMarkupLanguage.Settings;
using Zenject;

namespace BeatSaverUpdater.UI
{
    internal class SettingsViewController : IInitializable, IDisposable
    {
        public void Initialize()
        {
            BSMLSettings.Instance.AddSettingsMenu(nameof(BeatSaverUpdater), "BeatSaverUpdater.UI.SettingsView.bsml", this);
        }

        public void Dispose()
        {
            if (BSMLSettings.Instance != null)
            {
                BSMLSettings.Instance.RemoveSettingsMenu(this);
            }
        }
        
        [UIValue("use-cache")]
        private bool UseCache
        {
            get => PluginConfig.Instance.UseCache;
            set => PluginConfig.Instance.UseCache = value;
        }
    }
}
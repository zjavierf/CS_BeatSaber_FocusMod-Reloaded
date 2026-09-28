using FocusMod.Configuration;
using IPA;
using IPA.Config;
using IPA.Config.Stores;
using SiraUtil.Zenject;
using IPALogger = IPA.Logging.Logger;

namespace FocusMod {

    [Plugin(RuntimeOptions.SingleStartInit)]
    public class Plugin
    {
        internal static Plugin? Instance { get; private set; }
        internal static IPALogger? Log { get; private set; }

        [Init]
        public Plugin(IPALogger logger, Config conf, Zenjector zenjector) {
            Instance = this;
            Log = logger;

            PluginConfig.Instance = conf.Generated<PluginConfig>();
            zenjector.Install<FocusModInstaller>(Location.StandardPlayer);
            zenjector.Install<FocusModMenuInstaller>(Location.Menu);
        }
    }
}

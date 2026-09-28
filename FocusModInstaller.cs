using Zenject;

namespace FocusMod {
    class FocusModInstaller : MonoInstaller {
        public override void InstallBindings() {
            Container.BindInterfacesAndSelfTo<FocusMod>().AsSingle().NonLazy();
            Container.BindExecutionOrder<FocusMod>(999999);
        }
    }
}

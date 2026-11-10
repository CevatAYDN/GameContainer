#if NEXUS_RELEASE_DI_PROBE && !UNITY_EDITOR
using System;
using Nexus.Core;
using Nexus.Core.Services;
using UnityEngine;
using UnityEngine.Scripting;

namespace NexusVerify.Device
{
    // No setter is called here: only the package's preservation may keep reflection injection alive.
    internal static class ReleaseInjectionProbe
    {
        [Preserve]
        private sealed class ProbeMediator : Mediator<object>
        {
            [Preserve] public ProbeMediator() { }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Run()
        {
            try
            {
                if (Debug.isDebugBuild) throw new InvalidOperationException("Expected a non-development player.");
                NexusDI.ClearGeneratedRegistrations();
                NexusDI.ClearCaches();
                using var context = ContextFactory.Create();
                var di = context.Container;
                di.StrictInjection = true;
                di.BindInstance<IPlayerPrefsService>(new UnityPlayerPrefsService());
                di.BindInstance<IAudioRootProvider>(new DefaultAudioRootProvider());
                di.BindInstance<IAdAdapterFactory>(new AdAdapterFactory());
                di.BindInstance<IIapAdapterFactory>(new IapAdapterFactory());
                di.Bind<IAdService, AdService>(Lifetime.Singleton);
                di.Bind<IIapService, IapService>(Lifetime.Singleton);
                di.Bind<IAudioService, AudioService>(Lifetime.Singleton);
                di.Bind<ITickService, TickService>(Lifetime.Singleton);
                di.Bind<ILocalizationService, LocalizationService>(Lifetime.Singleton);
                di.Bind<IEconomyService, EconomyService>(Lifetime.Singleton);
                di.Bind<IInputService, InputService>(Lifetime.Singleton);
                di.Bind<IScreenStack, ScreenStack>(Lifetime.Singleton);
                Check((AdService)di.Resolve<IAdService>(), context);
                Check((IapService)di.Resolve<IIapService>(), context);
                Check((AudioService)di.Resolve<IAudioService>(), context);
                Check((TickService)di.Resolve<ITickService>(), context);
                Check((LocalizationService)di.Resolve<ILocalizationService>(), context);
                Check((EconomyService)di.Resolve<IEconomyService>(), context);
                Check((InputService)di.Resolve<IInputService>(), context);
                Check((ScreenStack)di.Resolve<IScreenStack>(), context);
                var mediator = new ProbeMediator();
                di.Inject(mediator);
                if (!ReferenceEquals(mediator.SignalBus, context.SignalBus))
                    throw new InvalidOperationException("Mediator's inherited private setter was not injected.");
                Debug.Log("[ReleaseInjectionProbe] PASS development=false services=8/8 Context+SignalBus=16/16 mediator=1/1 reflection=true");
            }
            catch (Exception error) { Debug.LogError("[ReleaseInjectionProbe] FAIL " + error); }
        }

        private static void Check<T>(NexusService<T> service, IContext context) where T : class
        {
            if (!ReferenceEquals(service.Context, context) || !ReferenceEquals(service.SignalBus, context.SignalBus))
                throw new InvalidOperationException(service.GetType().Name + " has uninjected inherited members.");
        }
    }
}
#endif

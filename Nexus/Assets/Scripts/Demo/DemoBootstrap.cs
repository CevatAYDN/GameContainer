using System;
using UnityEngine;
using Nexus.Core;
using Nexus.Core.Services;

namespace Nexus.Demo
{
    /// <summary>
    /// Auto-bootstrap script for the demo scene.
    /// Creates a single Global Root context (explicitly wired via SetUp + RegisterLifecycle),
    /// then starts the demo by firing the ShowMainMenu signal on the shared bus.
    /// </summary>
    [DefaultExecutionOrder(-900)]
    public class DemoBootstrap : MonoBehaviour
    {
        [Header("Context Configuration")]
        [SerializeField] private string scopeTag = "Global";

        [Header("Auto Setup")]
        [SerializeField] private bool autoCreateRoot = true;

        private Root _root;

        private async void Start()
        {
            if (!autoCreateRoot) return;

            try
            {
                Debug.Log("[DemoBootstrap] Creating Nexus context...");

                _root = CreateRoot(scopeTag);
                if (_root == null) return;

                // Wait for the root to initialize before starting the demo.
                if (!await WaitForContextInit(_root))
                {
                    // Fail-fast instead of proceeding with an uninitialized context:
                    // firing ShowMainMenu against a half-wired DI container would
                    // surface as confusing null-service crashes downstream.
                    Debug.LogError("[DemoBootstrap] Context initialization timed out; demo startup aborted.");
                    return;
                }

                Debug.Log("[DemoBootstrap] Context initialized. Starting demo...");

                // Register IAP catalog (composition root config, single place).
                var iapService = _root.Context.TryResolve<IapService>();
                if (iapService == null)
                {
                    Debug.LogError("[DemoBootstrap] IapService is not registered; IAP catalog not configured. Purchases will fail.");
                }
                else
                {
                    iapService.RegisterProducts(
                        new ProductDefinition { Id = "remove_ads", Type = ProductType.NonConsumable, PriceString = "$4.99" },
                        new ProductDefinition { Id = "coins_100", Type = ProductType.Consumable, PriceString = "$0.99" },
                        new ProductDefinition { Id = "coins_500", Type = ProductType.Consumable, PriceString = "$3.99" }
                    );
                }

                // Start the demo by firing the ShowMainMenu signal. All state changes flow
                // through the signal/command pipeline (single path), never direct calls.
                _root.Context.SignalBus.Fire(DemoUISignal.ShowMainMenu());
            }
            catch (Exception ex)
            {
                // async void: every exception would otherwise escape to Unity's unhandled
                // handler with no contextual stack. Surface it with the demo's context.
                Debug.LogError($"[DemoBootstrap] Demo startup failed: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private Root CreateRoot(string scopeTag)
        {
            var rootGo = new GameObject($"{scopeTag}Root");
            rootGo.SetActive(false);

            var root = rootGo.AddComponent<Root>();

            var contextData = ScriptableObject.CreateInstance<ContextData>();
            contextData.name = $"{scopeTag}ContextData";
            contextData.ScopeTag = scopeTag;
            contextData.EnableAutoDiscovery = false;
            contextData.EnableStrictInjection = true;
            contextData.FailOnValidationErrors = true;
            contextData.CommandPoolInitialSize = 8;
            contextData.CommandPoolMaxSize = 128;

            // Configure BEFORE activation so Awake sees a valid ContextData (no reflection).
            root.SetUp(contextData);
            root.RegisterLifecycle(new DemoGlobalLifecycle());

            rootGo.SetActive(true);
            return root;
        }

        /// <summary>Waits for the root context to initialize; returns false on timeout.</summary>
        private async System.Threading.Tasks.Task<bool> WaitForContextInit(Root root, int timeoutFrames = 300)
        {
            int frames = 0;
            while (!root.IsInitialized && frames < timeoutFrames)
            {
                await System.Threading.Tasks.Task.Yield();
                frames++;
            }
            return root.IsInitialized;
        }

        private void OnDestroy()
        {
            // Cleanup handled by Root.OnDestroy
        }
    }
}

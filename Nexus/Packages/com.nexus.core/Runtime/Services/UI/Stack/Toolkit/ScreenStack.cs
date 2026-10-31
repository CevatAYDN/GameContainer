using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Nexus.Core.Services
{
    /// <summary>
    /// A context's screen stack and its back router in one service, so a context never has one without the other
    /// (ADR-0003). Bind it with <c>builder.BindServiceInterfacesAndSelfTo&lt;ScreenStack&gt;()</c>.
    /// <para>
    /// One press has one handler across contexts: the router of a context whose parent context resolves an
    /// <see cref="IBackRouter"/> joins the parent's chain as a <see cref="BackOrder.BeforeScreens"/> step (its steps and
    /// screens, without its <see cref="AtRoot"/> behaviour), so a child context's screens hear back before the
    /// parent's. Only a router without one above it reads the back key, once per frame through
    /// <see cref="IInputService"/> "Cancel", from its own driver: <see cref="ITickService"/> stops ticking while paused, and
    /// back must still close what a paused game shows. A second such router in an unrelated context logs an error and
    /// does not read the key; bind the stack in a common parent context instead.
    /// </para>
    /// </summary>
    public sealed class ScreenStack : NexusService<IScreenStack>, IScreenStack, IBackRouter
    {
        private sealed class BackKeyDriver : MonoBehaviour
        {
            internal ScreenStack Owner;

            private void Update() => Owner?.Poll();
        }

        private readonly ScreenHistory _history = new ScreenHistory();
        private readonly BackChain _chain;
        private IDisposable _parentStep;
        private GameObject _driver;

        /// <summary>Where the back key is read; without it only <see cref="Press"/> runs the chain.</summary>
        [OptionalInject] public IInputService Input { get; set; }

        public ScreenStack()
        {
            _chain = new BackChain(_history.Back, () => Time.unscaledTimeAsDouble, MoveTaskToBack, Application.Quit);
        }

        /// <summary>True while this router reads the back key (the topmost router of its contexts).</summary>
        internal bool ReadsBackKey => _driver != null;

        public IReadOnlyObservableProperty<ScreenRoute> Current => _history.Current;

        public int Depth => _history.Depth;

        public bool ReducedMotion { get; set; }

        public RootBack AtRoot
        {
            get => _chain.AtRoot;
            set => _chain.AtRoot = value;
        }

        public float PressAgainSeconds
        {
            get => _chain.PressAgainSeconds;
            set => _chain.PressAgainSeconds = value;
        }

        public event Action PressAgainHintRequested
        {
            add => _chain.PressAgainHintRequested += value;
            remove => _chain.PressAgainHintRequested -= value;
        }

        public override ValueTask InitializeAsync(CancellationToken ct)
        {
            if (_driver != null || _parentStep != null) return default;
            var parent = Context?.Parent?.TryResolve<IBackRouter>();
            if (parent != null)
            {
                _parentStep = parent.AddStep(_chain.PressBelowRoot, BackOrder.BeforeScreens);
                return default;
            }
            var other = OtherRouterReadingTheKey();
            if (other != null)
            {
                NexusRuntime.Logger?.LogError($"[Nexus] ScreenStack of context '{Context?.ScopeTag}' does not read the back key: the stack of context '{other.Context?.ScopeTag}' already does, so one press would be handled twice. Bind ScreenStack in a common parent context.");
                return default;
            }
            if (Input == null)
                NexusRuntime.Logger?.LogWarning("[Nexus] ScreenStack: no IInputService is bound, so the back key is not read; IBackRouter.Press() still runs the chain.");
            _driver = new GameObject("[Nexus_BackKey]");
            if (Application.isPlaying) UnityEngine.Object.DontDestroyOnLoad(_driver);
            _driver.AddComponent<BackKeyDriver>().Owner = this;
            return default;
        }

        public override void Dispose()
        {
            _parentStep?.Dispose();
            _parentStep = null;
            if (_driver != null) SafeDestroyUtility.SafeDestroy(_driver);
            _driver = null;
        }

        public void Push<TScreen>(object args = null) where TScreen : ToolkitScreen
        {
            _history.Push(typeof(TScreen), args);
            _history.DropTransitions();
        }

        public void ReplaceRoot<TScreen>(object args = null) where TScreen : ToolkitScreen
        {
            _history.ReplaceRoot(typeof(TScreen), args);
            _history.DropTransitions();
        }

        public void Close<TScreen>() where TScreen : ToolkitScreen
        {
            _history.Close(typeof(TScreen));
            _history.DropTransitions();
        }

        public BackResult Back()
        {
            var result = _history.Back();
            _history.DropTransitions();
            return result;
        }

        public ValueTask PushAsync<TScreen>(object args = null) where TScreen : ToolkitScreen
        {
            _history.DropTransitions();
            _history.Push(typeof(TScreen), args);
            return _history.TakeTransitions();
        }

        public ValueTask ReplaceRootAsync<TScreen>(object args = null) where TScreen : ToolkitScreen
        {
            _history.DropTransitions();
            _history.ReplaceRoot(typeof(TScreen), args);
            return _history.TakeTransitions();
        }

        public ValueTask CloseAsync<TScreen>() where TScreen : ToolkitScreen
        {
            _history.DropTransitions();
            _history.Close(typeof(TScreen));
            return _history.TakeTransitions();
        }

        public ValueTask<BackResult> BackAsync()
        {
            _history.DropTransitions();
            var result = _history.Back();
            var transitions = _history.TakeTransitions();
            return transitions.IsCompletedSuccessfully ? new ValueTask<BackResult>(result) : AfterTransitions(transitions, result);
        }

        public bool IsShown<TScreen>() where TScreen : ToolkitScreen => _history.IsShown(typeof(TScreen));

        public IDisposable AddStep(Func<bool> step, BackOrder order) => _chain.AddStep(step, order);

        public bool Press() => _chain.Press();

        internal void Register(Type type, IStackScreen screen) => _history.Register(type, screen);

        internal void Unregister(Type type)
        {
            _history.Unregister(type);
            _history.DropTransitions();
        }

        private void Poll()
        {
            var input = Input;
            if (input != null) _chain.Poll(input);
        }

        private ScreenStack OtherRouterReadingTheKey()
        {
            foreach (var context in NexusRuntime.ActiveContexts)
            {
                if (context == Context || context.LifetimeToken.IsCancellationRequested) continue;
                if (context.TryResolve<IBackRouter>() is ScreenStack stack && stack != this && stack.ReadsBackKey) return stack;
            }
            return null;
        }

        private static async ValueTask<BackResult> AfterTransitions(ValueTask transitions, BackResult result)
        {
            await transitions;
            return result;
        }

        private static void MoveTaskToBack()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
                    activity.Call<bool>("moveTaskToBack", true);
            }
            catch (Exception ex)
            {
                NexusRuntime.Logger?.LogException(ex);
            }
#endif
        }
    }
}

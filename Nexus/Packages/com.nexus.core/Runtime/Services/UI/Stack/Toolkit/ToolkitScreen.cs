using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;

namespace Nexus.Core.Services
{
    /// <summary>
    /// A screen of the UI Toolkit screen stack (ADR-0003): a Nexus <see cref="View"/> whose mediator binds as usual. When
    /// it binds, it registers with the <see cref="IScreenStack"/> of its context under its own type, so no game keeps a
    /// screen table; an override of <see cref="OnBind"/> calls <c>base.OnBind</c>. The stack shows and hides its
    /// <see cref="Root"/> with <c>display</c> and the class <see cref="ShownClass"/>, never by deactivating the
    /// GameObject: a deactivated screen unbinds, as every view does, and leaves its context's history.
    /// <para>
    /// A disabled <see cref="UIDocument"/> drops its tree, and enabled again it builds a new root. The screen follows
    /// it: a transition on the dropped root completes its task at once, and the new root gets the screen's state on
    /// the first frame it is there.
    /// </para>
    /// </summary>
    public abstract class ToolkitScreen : View, IStackScreen
    {
        /// <summary>
        /// Added on the panel update after a show and removed at a hide, so a USS transition on the root animates both.
        /// The stack waits for the root's longest transition (duration plus delay) before a hide sets <c>display: none</c>
        /// and before a task completes; a root without a transition shows and hides at once.
        /// </summary>
        public const string ShownClass = "nexus-screen--shown";

        private static readonly List<TimeValue> NoTransition = new List<TimeValue> { new TimeValue(0) };

        private ScreenStack _stack;
        private VisualElement _root;
        private TaskCompletionSource<bool> _transition;
        private int _generation;
        private bool _motionOverridden;
        private Coroutine _waitingForRoot;

        /// <summary>The stack layer; inside one layer the screen opened last is on top.</summary>
        public virtual UILayer Layer => UILayer.Screen;

        /// <summary>Back never closes it (a damaged-save recovery, a run's end).</summary>
        public virtual bool BlocksBack => false;

        /// <summary>The element the stack shows and hides; null until <see cref="ResolveRoot"/> finds one.</summary>
        public VisualElement Root => _root ?? TrackRoot();

        /// <summary>The arguments of the route that showed it last.</summary>
        public object Args { get; private set; }

        public bool IsShown { get; private set; }

        /// <summary>The element to show and hide. Default: the root of the <see cref="UIDocument"/> on this GameObject.</summary>
        protected virtual VisualElement ResolveRoot() => TryGetComponent(out UIDocument document) ? document.rootVisualElement : null;

        /// <summary>First call of a back press while this screen is on top; true = handled here, nothing is popped.</summary>
        protected virtual bool OnBack() => false;

        protected virtual void OnShown(object args) { }

        protected virtual void OnHidden() { }

        protected override void OnBind(IContext context)
        {
            base.OnBind(context);
            // A root in a document that enables after this screen binds (on another GameObject) is not there yet;
            // its state is applied on the first frame it is.
            if (Root != null) ApplyState(_root);
            else WaitForRoot();
            _stack = context.TryResolve<IScreenStack>() as ScreenStack;
            if (_stack == null)
            {
                NexusRuntime.Logger?.LogError($"[Nexus] Screen '{GetType().Name}' found no ScreenStack in its context; bind one with builder.BindServiceInterfacesAndSelfTo<ScreenStack>().");
                return;
            }
            _stack.Register(GetType(), this);
        }

        protected override void OnUnbind()
        {
            var stack = _stack;
            _stack = null;
            stack?.Unregister(GetType());
            IsShown = false;
            _generation++;
            Finish();
            // Out of the history means hidden, also when only this component was disabled and its document still shows.
            if (_root != null) ApplyState(_root);
            ForgetRoot();
            // A disabled component keeps its coroutines running; only a deactivated GameObject stops them.
            if (_waitingForRoot != null) StopCoroutine(_waitingForRoot);
            _waitingForRoot = null;
            base.OnUnbind();
        }

        bool IStackScreen.OnBack() => OnBack();

        ValueTask IStackScreen.Show(object args)
        {
            bool shownAlready = IsShown; // shown again with other arguments: nothing animates
            Args = args;
            IsShown = true;
            var transition = Root == null || shownAlready ? default : Begin(Root, true);
            OnShown(args);
            return transition;
        }

        ValueTask IStackScreen.Hide()
        {
            IsShown = false;
            var transition = Root == null ? default : Begin(Root, false);
            OnHidden();
            return transition;
        }

        private ValueTask Begin(VisualElement root, bool show)
        {
            int generation = ++_generation;
            Finish(); // the superseded transition's task completes now
            bool reduced = _stack != null && _stack.ReducedMotion;
            SetReducedMotion(root, reduced);
            long ms = reduced ? 0 : LongestTransitionMs(root);
            if (ms <= 0)
            {
                ApplyState(root);
                return default;
            }
            _transition = new TaskCompletionSource<bool>();
            if (show)
            {
                root.style.display = DisplayStyle.Flex;
                // The class goes on with the next panel update, so the transition starts from the hidden style.
                root.schedule.Execute(() =>
                {
                    if (generation != _generation) return;
                    root.AddToClassList(ShownClass);
                    root.schedule.Execute(() => { if (generation == _generation) Finish(); }).StartingIn(ms);
                });
            }
            else
            {
                root.RemoveFromClassList(ShownClass);
                root.schedule.Execute(() =>
                {
                    if (generation != _generation) return;
                    root.style.display = DisplayStyle.None;
                    Finish();
                }).StartingIn(ms);
            }
            return new ValueTask(_transition.Task);
        }

        private void ApplyState(VisualElement root)
        {
            root.style.display = IsShown ? DisplayStyle.Flex : DisplayStyle.None;
            if (IsShown) root.AddToClassList(ShownClass);
            else root.RemoveFromClassList(ShownClass);
        }

        private VisualElement TrackRoot()
        {
            var root = ResolveRoot();
            if (root == null) return null;
            _root = root;
            _motionOverridden = false;
            root.RegisterCallback<DetachFromPanelEvent>(OnRootDetached);
            return root;
        }

        private void ForgetRoot()
        {
            _root?.UnregisterCallback<DetachFromPanelEvent>(OnRootDetached);
            _root = null;
        }

        // The root left its panel: its document was disabled or the element removed. Its scheduled items never run
        // again, so the transition ends now, and the state goes to the root that takes its place.
        private void OnRootDetached(DetachFromPanelEvent evt)
        {
            if (evt.target != _root) return;
            ForgetRoot();
            _generation++;
            Finish();
            WaitForRoot();
        }

        private void WaitForRoot()
        {
            // isActiveAndEnabled stays true until this component's own OnDisable, also while its GameObject is being
            // deactivated or destroyed, where a coroutine cannot start.
            if (_waitingForRoot == null && enabled && gameObject.activeInHierarchy) _waitingForRoot = StartCoroutine(ApplyStateWhenReady());
        }

        private IEnumerator ApplyStateWhenReady()
        {
            do yield return null;
            while (Root == null);
            _waitingForRoot = null;
            ApplyState(_root);
        }

        private void SetReducedMotion(VisualElement root, bool reduced)
        {
            if (reduced == _motionOverridden) return;
            _motionOverridden = reduced;
            root.style.transitionDuration = reduced ? new StyleList<TimeValue>(NoTransition) : new StyleList<TimeValue>(StyleKeyword.Null);
        }

        private void Finish()
        {
            var transition = _transition;
            _transition = null;
            transition?.TrySetResult(true);
        }

        private static long LongestTransitionMs(VisualElement root)
        {
            float duration = 0, delay = 0;
            foreach (var value in root.resolvedStyle.transitionDuration) duration = Math.Max(duration, Seconds(value));
            if (duration <= 0) return 0;
            foreach (var value in root.resolvedStyle.transitionDelay) delay = Math.Max(delay, Seconds(value));
            return (long)Math.Ceiling((duration + delay) * 1000f);
        }

        private static float Seconds(TimeValue value) => value.unit == TimeUnit.Millisecond ? value.value / 1000f : value.value;
    }
}

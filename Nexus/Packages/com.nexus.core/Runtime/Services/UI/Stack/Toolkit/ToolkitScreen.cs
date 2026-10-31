using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine.UIElements;

namespace Nexus.Core.Services
{
    /// <summary>
    /// A screen of the UI Toolkit screen stack (ADR-0003): a Nexus <see cref="View"/> whose mediator binds as usual. When
    /// it binds, it registers with the <see cref="IScreenStack"/> of its context under its own type, so no game keeps a
    /// screen table; an override of <see cref="OnBind"/> calls <c>base.OnBind</c>. The stack shows and hides its
    /// <see cref="Root"/> with <c>display</c> and the class <see cref="ShownClass"/>, never by deactivating the
    /// GameObject: a deactivated screen unbinds, as every view does, and leaves its context's history.
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

        /// <summary>The stack layer; inside one layer the screen opened last is on top.</summary>
        public virtual UILayer Layer => UILayer.Screen;

        /// <summary>Back never closes it (a damaged-save recovery, a run's end).</summary>
        public virtual bool BlocksBack => false;

        /// <summary>The element the stack shows and hides; null until <see cref="ResolveRoot"/> finds one.</summary>
        public VisualElement Root => _root ??= ResolveRoot();

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
            _root = null;
            // A root in a document that enables after this screen binds (on another GameObject) is not there yet;
            // its state is applied on the next frame.
            if (Root != null) ApplyState(Root);
            else if (isActiveAndEnabled) StartCoroutine(ApplyStateWhenReady());
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

        private IEnumerator ApplyStateWhenReady()
        {
            yield return null;
            _root = null;
            if (Root != null) ApplyState(Root);
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

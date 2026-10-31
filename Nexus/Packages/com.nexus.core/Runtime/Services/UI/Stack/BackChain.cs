using System;
using System.Collections.Generic;

namespace Nexus.Core.Services
{
    /// <summary>
    /// The back chain of <see cref="IBackRouter"/>, without the engine (ADR-0003): the steps, the screen stack's back
    /// and the root behaviour, in that order, and the poll that reads one press per frame. The router passes the
    /// stack's back, an unscaled clock and the two root actions; the poll is driven by the router, which keeps it
    /// running while the game is paused. No static state: each context has its own chain.
    /// </summary>
    internal sealed class BackChain
    {
        private sealed class Step : IDisposable
        {
            internal Func<bool> Handler;
            internal List<Step> Owner;

            public void Dispose()
            {
                Owner?.Remove(this);
                Owner = null;
            }
        }

        private readonly List<Step> _before = new List<Step>();
        private readonly List<Step> _after = new List<Step>();
        private readonly Func<BackResult> _screens;
        private readonly Func<double> _clock;
        private readonly Action _moveTaskToBack;
        private readonly Action _quit;
        private double _quitUntil = double.NegativeInfinity;

        public BackChain(Func<BackResult> screens, Func<double> clock, Action moveTaskToBack, Action quit)
        {
            _screens = screens;
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _moveTaskToBack = moveTaskToBack;
            _quit = quit;
        }

        public RootBack AtRoot { get; set; } = RootBack.Nothing;

        public float PressAgainSeconds { get; set; } = 2f;

        public event Action PressAgainHintRequested;

        public IDisposable AddStep(Func<bool> step, BackOrder order)
        {
            if (step == null) throw new ArgumentNullException(nameof(step));
            var list = order == BackOrder.BeforeScreens ? _before : _after;
            var added = new Step { Handler = step, Owner = list };
            list.Add(added);
            return added;
        }

        /// <summary>One frame: a press reaches the chain only when "Cancel" went down this frame. Allocates nothing.</summary>
        public bool Poll(IInputService input) => input != null && input.GetButtonDown("Cancel") && Press();

        public bool Press()
        {
            if (RunSteps(_before)) return Handled();
            if (_screens != null && _screens() != BackResult.AtRoot) return Handled();
            if (RunSteps(_after)) return Handled();
            switch (AtRoot)
            {
                case RootBack.MoveTaskToBack:
                    _moveTaskToBack?.Invoke();
                    return true;
                case RootBack.PressAgainToQuit:
                    double now = _clock();
                    if (now <= _quitUntil)
                    {
                        _quitUntil = double.NegativeInfinity;
                        _quit?.Invoke();
                        return true;
                    }
                    _quitUntil = now + PressAgainSeconds;
                    PressAgainHintRequested?.Invoke();
                    return true;
                default:
                    return false;
            }
        }

        // A press handled before the root ends a pending "press again".
        private bool Handled()
        {
            _quitUntil = double.NegativeInfinity;
            return true;
        }

        // Runs a copy, most recently added first, so a step that removes itself or another one skips nothing and
        // repeats nothing; a step removed during the press is not run. The copy is made only on a press.
        private static bool RunSteps(List<Step> steps)
        {
            if (steps.Count == 0) return false;
            var pressing = steps.ToArray();
            for (int i = pressing.Length - 1; i >= 0; i--)
                if (pressing[i].Owner != null && pressing[i].Handler()) return true;
            return false;
        }
    }
}

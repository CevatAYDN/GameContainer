using System;
using System.Collections.Generic;
using Nexus.Core.Services;

namespace Nexus.Firebase.Analytics
{
    /// <summary>
    /// Sends, holds or drops analytics events as the consent answer and the player's choice allow. Events logged before the answer is known
    /// wait here, in order, up to <see cref="MaxHeld"/>, and are sent in that order once it is granted: the first events of a first session
    /// are the ones this keeps.
    /// </summary>
    public sealed class AnalyticsGate
    {
        public const int MaxHeld = 256;

        private readonly List<AnalyticsEvent> _held = new();
        private bool _overflowReported;
        private bool _closed;

        public int HeldCount => _held.Count;

        /// <summary>Events refused because the wait was full. They were never sent and are not kept.</summary>
        public int Overflowed { get; private set; }

        public void Log(ConsentState state, AnalyticsChoice choice, string name, Dictionary<string, object> parameters, Action<AnalyticsEvent> send)
        {
            if (_closed) return;
            var analyticsEvent = new AnalyticsEvent(name, parameters);
            switch (AnalyticsCollectionPolicy.Decide(state, choice))
            {
                case AnalyticsDecision.Send:
                    Flush(send);
                    send(analyticsEvent);
                    break;
                case AnalyticsDecision.Hold:
                    Hold(analyticsEvent);
                    break;
            }
        }

        /// <summary>Called when the answer or the choice changes: sends the held events on Send, drops them on Discard, keeps them on Hold.</summary>
        public void Resolve(ConsentState state, AnalyticsChoice choice, Action<AnalyticsEvent> send)
        {
            switch (AnalyticsCollectionPolicy.Decide(state, choice))
            {
                case AnalyticsDecision.Send:
                    Flush(send);
                    break;
                case AnalyticsDecision.Discard:
                    _held.Clear();
                    _overflowReported = false;
                    break;
            }
        }

        /// <summary>Analytics will never send (the SDK did not start): drops what is held and holds nothing more (issue #171).</summary>
        public void Close()
        {
            _held.Clear();
            _closed = true;
        }

        private void Hold(AnalyticsEvent analyticsEvent)
        {
            if (_held.Count >= MaxHeld)
            {
                Overflowed++;
                // once per filled wait, in every build: these events are lost (issue #171)
                if (!_overflowReported)
                {
                    _overflowReported = true;
                    NexusLog.WarnAlways("[Firebase] Analytics holds " + MaxHeld + " events until it may send them (the consent answer, or Firebase starting); later events are dropped until then.");
                }
                return;
            }

            _held.Add(analyticsEvent);
        }

        private void Flush(Action<AnalyticsEvent> send)
        {
            foreach (AnalyticsEvent held in _held)
            {
                send(held);
            }

            _held.Clear();
            _overflowReported = false;
        }
    }
}
using System.Collections.Generic;
using NUnit.Framework;
using Nexus.Firebase.Analytics;

namespace Nexus.Firebase.Tests
{
    /// <summary>
    /// The held-event gate: events logged before consent is known wait in order, are sent once analytics are granted, and are dropped when
    /// they are refused. The wait is bounded so a player who never answers cannot grow it without limit.
    /// </summary>
    public sealed class AnalyticsGateTests
    {
        [Test]
        public void Gate_HoldsEventsUntilConsentIsGrantedThenSendsThemInOrder()
        {
            var gate = new AnalyticsGate();
            var sent = new List<string>();

            gate.Log(ConsentState.Unknown, AnalyticsChoice.Unknown, "ftue_step", null, e => sent.Add(e.Name));
            gate.Log(ConsentState.Required, AnalyticsChoice.Unknown, "run_start", null, e => sent.Add(e.Name));
            Assert.AreEqual(0, sent.Count, "nothing reaches the SDK before the answer");

            gate.Resolve(ConsentState.Obtained, AnalyticsChoice.Granted, e => sent.Add(e.Name));

            CollectionAssert.AreEqual(new[] { "ftue_step", "run_start" }, sent);
            Assert.AreEqual(0, gate.HeldCount);
        }

        [Test]
        public void Gate_DiscardsHeldEventsWhenTheChoiceIsRefused()
        {
            var gate = new AnalyticsGate();
            var sent = new List<string>();

            gate.Log(ConsentState.Required, AnalyticsChoice.Unknown, "ftue_step", null, e => sent.Add(e.Name));
            gate.Resolve(ConsentState.Obtained, AnalyticsChoice.Denied, e => sent.Add(e.Name));

            Assert.AreEqual(0, sent.Count, "a refused player's events are never sent");
            Assert.AreEqual(0, gate.HeldCount);
        }

        [Test]
        public void Gate_CountsEventsBeyondTheWaitInsteadOfKeepingThem()
        {
            var gate = new AnalyticsGate();

            for (int i = 0; i < AnalyticsGate.MaxHeld + 5; i++)
            {
                gate.Log(ConsentState.Unknown, AnalyticsChoice.Unknown, "ftue_step", null, _ => { });
            }

            Assert.AreEqual(AnalyticsGate.MaxHeld, gate.HeldCount);
            Assert.AreEqual(5, gate.Overflowed);
        }

        [Test]
        public void Gate_SendsStraightAwayWhenConsentIsNotRequired()
        {
            var gate = new AnalyticsGate();
            var sent = new List<string>();

            gate.Log(ConsentState.NotRequired, AnalyticsChoice.Unknown, "run_start", null, e => sent.Add(e.Name));

            CollectionAssert.AreEqual(new[] { "run_start" }, sent);
            Assert.AreEqual(0, gate.HeldCount);
        }
    }
}
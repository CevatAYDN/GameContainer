using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Nexus.Core;
using Nexus.Core.Services;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Nexus.Editor.Tests
{
    /// <summary>
    /// The model and service defects the audit #206 found: a throwing secure observer stopped the others and left Earn,
    /// Spend and SetLevel unsaved (#225), and IsRTL was false for a regional code such as ar-EG (#228).
    /// </summary>
    public sealed class ModelServiceAuditDefectTests
    {
        private static Regex Isolated(string source, string message) =>
            new Regex(@"^\[Nexus\] " + Regex.Escape(source) + @" threw: System\.InvalidOperationException: " + message);

        [Test]
        public void AThrowingSecureObserver_LeavesTheOthersAndAQueuedChangeDelivered()
        {
            var property = new SecureObservableLong(0);
            var seen = new List<string>();
            bool thrown = false;
            property.OnChanged((old, now) => { if (now == 1) property.Value = 5; });
            property.OnChanged((old, now) => { if (!thrown) { thrown = true; throw new InvalidOperationException("observer bug"); } });
            property.OnChanged((old, now) => seen.Add(old + "->" + now));
            LogAssert.Expect(LogType.Error, Isolated("An observer of SecureObservable<Int64>", "observer bug"));

            Assert.Throws<InvalidOperationException>(() => property.Value = 1);

            Assert.AreEqual(5, property.Value);
            CollectionAssert.AreEqual(new[] { "0->1", "1->5" }, seen);
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public async Task AThrowingBalanceObserver_LeavesEarnAndSpendSavedWithTheirResults()
        {
            var prefs = new InMemoryPlayerPrefsService();
            var economy = new EconomyService { PlayerPrefsService = prefs };
            await economy.InitializeAsync(default);
            try
            {
                var balance = economy.GetObservableBalance("coins");
                int later = 0;
                balance.OnChanged((old, now) => throw new InvalidOperationException("label destroyed"));
                balance.OnChanged((old, now) => later++);

                LogAssert.Expect(LogType.Error, Isolated("An observer of SecureObservable<Int64>", "label destroyed"));
                economy.Earn("coins", 10);
                LogAssert.Expect(LogType.Error, Isolated("An observer of SecureObservable<Int64>", "label destroyed"));
                bool spent = economy.Spend("coins", 4);

                Assert.IsTrue(spent);
                Assert.AreEqual(6L, economy.GetBalance("coins"));
                Assert.AreEqual(2, later);
                Assert.AreEqual(6L, prefs.GetLong("NT_Eco_coins"));
                LogAssert.NoUnexpectedReceived();
            }
            finally { economy.Dispose(); }
        }

        [Test]
        public async Task AThrowingLevelObserver_LeavesBothLevelsSetAndSaved()
        {
            var prefs = new InMemoryPlayerPrefsService();
            var progression = new ProgressionService { PlayerPrefsService = prefs };
            await progression.InitializeAsync(default);
            try
            {
                progression.MaxUnlockedLevel.OnChanged((old, now) => throw new InvalidOperationException("map destroyed"));
                LogAssert.Expect(LogType.Error, Isolated("An observer of SecureObservable<Int32>", "map destroyed"));

                progression.SetLevel(3);

                Assert.AreEqual(3, progression.CurrentLevel.Value);
                Assert.AreEqual(3, prefs.GetInt("NT_Prog_CurrentLevel"));
                Assert.AreEqual(3, prefs.GetInt("NT_Prog_MaxLevel"));
                LogAssert.NoUnexpectedReceived();
            }
            finally { progression.Dispose(); }
        }

        [Test]
        public void IsRTL_IsTrueForARegionalCodeOfARightToLeftLanguage()
        {
            var localization = new LocalizationService(new InMemoryPlayerPrefsService());

            localization.SetLanguage("ar-EG");
            Assert.IsTrue(localization.IsRTL);
            localization.SetLanguage("he_IL");
            Assert.IsTrue(localization.IsRTL);
            localization.SetLanguage("en-US");
            Assert.IsFalse(localization.IsRTL);
        }
    }
}

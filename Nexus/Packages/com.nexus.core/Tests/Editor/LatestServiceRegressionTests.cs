using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Nexus.Core;
using Nexus.Core.Services;
using NUnit.Framework;
using UnityEngine;

namespace Nexus.Editor.Tests
{
    public sealed class LatestServiceRegressionTests
    {
        // Deliberately implements only the old adapter contract: BigDouble must use the
        // interface's string-backed defaults, rather than becoming a silent no-op.
        private sealed class Prefs : IPlayerPrefsService
        {
            private readonly Dictionary<string, object> _values = new();
            public int Saves;
            private T Read<T>(string key, T fallback) => _values.TryGetValue(key, out var value) && value is T typed ? typed : fallback;
            public int GetInt(string key, int defaultValue = 0) => Read(key, defaultValue);
            public void SetInt(string key, int value) => _values[key] = value;
            public bool GetBool(string key, bool defaultValue = false) => Read(key, defaultValue);
            public void SetBool(string key, bool value) => _values[key] = value;
            public string GetString(string key, string defaultValue = "") => Read(key, defaultValue);
            public void SetString(string key, string value) => _values[key] = value;
            public float GetFloat(string key, float defaultValue = 0) => Read(key, defaultValue);
            public void SetFloat(string key, float value) => _values[key] = value;
            public long GetLong(string key, long defaultValue = 0) => Read(key, defaultValue);
            public void SetLong(string key, long value) => _values[key] = value;
            public bool HasKey(string key) => _values.ContainsKey(key);
            public void DeleteKey(string key) => _values.Remove(key);
            public void Save() => Saves++;
        }

        private sealed class DeferredSave : ISaveThrottler
        {
            private Action _pending;
            public float SecondsSinceLastSave => 0;
            public float GetSecondsSinceLastSave(string owner) => 0;
            public void TryRequestSave(Action action) => _pending = action;
            public void TryRequestSave(string owner, Action action) => _pending = action;
            public void ForceSave(Action action) { _pending = action; Flush(); }
            public void ForceSave(string owner, Action action) => ForceSave(action);
            public void Flush(string owner) => Flush();
            public void Flush() { var action = _pending; _pending = null; action?.Invoke(); }
        }

        private class LongValidator : INetworkEconomyValidator
        {
            public readonly TaskCompletionSource<bool> Response = new();
            public Task<bool> ValidateSpendAsync(string currencyId, long amount, string reason) => Response.Task;
            public Task ValidateEarnAsync(string currencyId, long amount, string reason) => Task.CompletedTask;
        }

        private sealed class BigValidator : LongValidator, INetworkBigEconomyValidator
        {
            public readonly TaskCompletionSource<bool> BigResponse = new();
            public Task<bool> ValidateSpendBigAsync(string currencyId, BigDouble amount, string reason) => BigResponse.Task;
            public Task ValidateEarnBigAsync(string currencyId, BigDouble amount, string reason) => Task.CompletedTask;
        }

        private sealed class Provider : IInputProvider
        {
            public Vector2 Move;
            public Vector2 GetMoveInput() => Move;
            public bool GetButton(string actionName) => false;
            public bool GetButtonDown(string actionName) => false;
            public bool GetButtonUp(string actionName) => false;
        }

        [Test]
        public void NullCurrencyReads_ReturnZeroWithoutCreatingBalances()
        {
            using var economy = new EconomyService();
            Assert.AreEqual(0, economy.GetBalance(null));
            Assert.AreEqual(BigDouble.Zero, economy.GetBigBalance(null));
            Assert.IsNull(economy.GetObservableBalance(null));
            Assert.IsNull(economy.GetObservableBigBalance(null));
        }

        [Test]
        public void Promotion_UsesLiveThrottledBalanceAndRetainsLegacyObservable()
        {
            var prefs = new Prefs(); prefs.SetLong("NT_Eco_coin", 100);
            var throttler = new DeferredSave();
            using var economy = new EconomyService { PlayerPrefsService = prefs, SaveThrottler = throttler };
            var legacy = economy.GetObservableBalance("coin");
            Assert.IsTrue(economy.Spend("coin", 40));
            Assert.AreEqual(100, prefs.GetLong("NT_Eco_coin"), "The spend is still pending on disk.");
            Assert.AreEqual(new BigDouble(60), economy.GetBigBalance("coin"));
            economy.Earn("coin", 10);
            Assert.IsTrue(economy.SpendBig("coin", new BigDouble(20)));
            Assert.AreSame(legacy, economy.GetObservableBalance("coin"));
            Assert.AreEqual(50, legacy.Value);
            Assert.AreEqual(new BigDouble(50), economy.GetBigBalance("coin"));
            throttler.Flush();
            Assert.IsFalse(prefs.HasKey("NT_Eco_coin"));
            Assert.AreEqual(new BigDouble(50), ((IPlayerPrefsService)prefs).GetBigDouble("NT_EcoBig_coin"));
            using var restarted = new EconomyService { PlayerPrefsService = prefs };
            Assert.AreEqual(50, restarted.GetBalance("coin"));
            Assert.AreEqual(new BigDouble(50), restarted.GetBigBalance("coin"));
        }

        [Test]
        public void LongOperations_InBigModePreserveFractionAndSaturateProjection()
        {
            using var economy = new EconomyService();
            economy.SetBigBalance("coin", new BigDouble(100.5));
            Assert.AreEqual(100, economy.GetBalance("coin"));
            Assert.IsTrue(economy.Spend("coin", 20)); economy.Earn("coin", 5);
            Assert.AreEqual(85.5, (double)economy.GetBigBalance("coin"), 0.00001);
            economy.SetBigBalance("coin", new BigDouble(1, 30));
            Assert.AreEqual(long.MaxValue, economy.GetBalance("coin"));
            economy.SetBalance("coin", 7);
            Assert.AreEqual(new BigDouble(7), economy.GetBigBalance("coin"));
        }

        [Test]
        public void LegacyObservable_RemainsConnectedAfterPromotionIncludingReentrantWrite()
        {
            using var economy = new EconomyService();
            var legacy = economy.GetObservableBalance("coin");
            economy.GetBigBalance("coin");
            legacy.Value = 7; Assert.AreEqual(new BigDouble(7), economy.GetBigBalance("coin"));
            legacy.OnChanged((_, value) => { if (value == 10) legacy.Value = 20; });
            economy.SetBigBalance("coin", new BigDouble(10));
            Assert.AreEqual(20, legacy.Value);
            Assert.AreEqual(new BigDouble(20), economy.GetBigBalance("coin"));
        }

        [Test]
        public void LongRefund_AfterPromotionUpdatesCanonicalBalance()
        {
            // the warnings this test causes, in the order it causes them (#198)
            UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Warning, new System.Text.RegularExpressions.Regex(@"^\[Economy\]\ Server\ rejected\ spend\ of\ \d+\ 'coin'\ —\ balance\ restored\."));
            var validator = new LongValidator();
            using var economy = new EconomyService { NetworkValidator = validator };
            economy.SetBalance("coin", 100); Assert.IsTrue(economy.Spend("coin", 40));
            Assert.AreEqual(new BigDouble(60), economy.GetBigBalance("coin"));
            validator.Response.SetResult(false);
            Assert.AreEqual(new BigDouble(100), economy.GetBigBalance("coin"));
            Assert.AreEqual(100, economy.GetBalance("coin"));
        }

        [Test]
        public void LongOnlyBackend_BigTransactionsThrowNotSupportedWithoutChangingBalance()
        {
            // SpendBig returned false here (#196), which a caller could not tell from an insufficient balance.
            using var economy = new EconomyService { NetworkValidator = new LongValidator() };
            economy.SetBalance("coin", 10);
            Assert.Throws<NotSupportedException>(() => economy.SpendBig("coin", new BigDouble(2)));
            Assert.Throws<NotSupportedException>(() => economy.EarnBig("coin", BigDouble.One));
            Assert.Throws<NotSupportedException>(() => economy.SetBigBalance("coin", BigDouble.One));
            Assert.AreEqual(10, economy.GetBalance("coin"));
        }

        [Test]
        public void BigBackend_RefundsRejectedSpendAndIgnoresDisposedResponse()
        {
            // the warnings this test causes, in the order it causes them (#198); the disposed service's refusal causes none
            UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Warning, new System.Text.RegularExpressions.Regex(@"^\[Economy\]\ Server\ rejected\ spend\ of\ .+\ 'coin'\ —\ balance\ restored\."));
            var validator = new BigValidator();
            using var economy = new EconomyService { NetworkValidator = validator };
            economy.SetBigBalance("coin", new BigDouble(100));
            Assert.IsTrue(economy.SpendBig("coin", new BigDouble(40)));
            validator.BigResponse.SetResult(false);
            Assert.AreEqual(new BigDouble(100), economy.GetBigBalance("coin"));

            var delayed = new BigValidator();
            var disposed = new EconomyService { NetworkValidator = delayed };
            disposed.SetBigBalance("coin", new BigDouble(100)); disposed.SpendBig("coin", new BigDouble(40));
            disposed.Dispose(); delayed.BigResponse.SetResult(false);
            Assert.AreEqual(BigDouble.Zero, disposed.GetBigBalance("coin"));
            Assert.IsNull(disposed.GetObservableBigBalance("coin"));
        }

        [Test]
        public void BigAmounts_NonfiniteNegativeOrZeroWhereAChangeIsRequired_Throw()
        {
            // CanAffordBig and SpendBig returned false for these (#196) while EarnBig threw.
            using var economy = new EconomyService(); economy.SetBigBalance("coin", new BigDouble(10));
            foreach (double mantissa in new[] { double.NaN, double.PositiveInfinity, -1d })
            {
                var malformed = new BigDouble { Mantissa = mantissa, Exponent = 0 };
                Assert.Throws<ArgumentOutOfRangeException>(() => economy.CanAffordBig("coin", malformed));
                Assert.Throws<ArgumentOutOfRangeException>(() => economy.SpendBig("coin", malformed));
                Assert.Throws<ArgumentOutOfRangeException>(() => economy.EarnBig("coin", malformed));
                Assert.Throws<ArgumentOutOfRangeException>(() => economy.SetBigBalance("coin", malformed));
            }
            Assert.Throws<ArgumentOutOfRangeException>(() => economy.SpendBig("coin", BigDouble.Zero));
            Assert.Throws<ArgumentOutOfRangeException>(() => economy.EarnBig("coin", BigDouble.Zero));
            Assert.IsTrue(economy.CanAffordBig("coin", BigDouble.Zero));
            Assert.AreEqual(new BigDouble(10), economy.GetBigBalance("coin"));
        }

        [Test]
        public void ARefusedSpend_SaysWhy_OnBothApis()
        {
            // Spend returned a bare false for an insufficient balance, an empty id and a disposed service alike (#196).
            using var economy = new EconomyService();
            economy.SetBalance("coin", 10);
            Assert.IsFalse(economy.Spend("coin", 11, out var refusal));
            Assert.AreEqual(EconomyRefusal.InsufficientBalance, refusal);
            Assert.IsFalse(economy.Spend("", 1, out refusal));
            Assert.AreEqual(EconomyRefusal.InvalidCurrencyId, refusal);
            Assert.IsTrue(economy.Spend("coin", 4, out refusal));
            Assert.AreEqual(EconomyRefusal.None, refusal);

            economy.SetBigBalance("gem", new BigDouble(1, 30));
            Assert.IsFalse(economy.SpendBig("gem", new BigDouble(2, 30), out refusal));
            Assert.AreEqual(EconomyRefusal.InsufficientBalance, refusal);
            Assert.IsTrue(economy.SpendBig("gem", new BigDouble(5, 29), out refusal));
            Assert.AreEqual(EconomyRefusal.None, refusal);

            economy.Dispose();
            Assert.IsFalse(economy.Spend("coin", 1, out refusal));
            Assert.AreEqual(EconomyRefusal.Disposed, refusal);
            Assert.IsFalse(economy.SpendBig("gem", BigDouble.One, out refusal));
            Assert.AreEqual(EconomyRefusal.Disposed, refusal);
        }

        private sealed class SignalledEconomy : EconomyService
        {
            public SignalledEconomy(ISignalBus bus) => SignalBus = bus;
        }

        [Test]
        public void AServerRefusedSpend_FiresSpendRefundedSignal_WithTheAmountAndReason()
        {
            // A refund changed the balance with no event, so a game could not take back what it granted (#196).
            UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Warning, new System.Text.RegularExpressions.Regex(@"^\[Economy\]\ Server\ rejected\ spend\ of\ 40\ 'coin'"));
            UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Warning, new System.Text.RegularExpressions.Regex(@"^\[Economy\]\ Server\ rejected\ spend\ of\ .+\ 'gem'"));
            var bus = new RecordingSignalBus();
            var validator = new BigValidator();
            using var economy = new SignalledEconomy(bus) { NetworkValidator = validator };
            economy.SetBalance("coin", 100);
            Assert.IsTrue(economy.Spend("coin", 40, "shop"));
            Assert.AreEqual(0, bus.Count<SpendRefundedSignal>(), "Nothing is refunded before the server answers.");
            validator.Response.SetResult(false);
            var refund = bus.Last<SpendRefundedSignal>();
            Assert.AreEqual("coin", refund.CurrencyId);
            Assert.AreEqual(40, refund.Amount);
            Assert.AreEqual(new BigDouble(40), refund.BigAmount);
            Assert.AreEqual("shop", refund.Reason);

            economy.SetBigBalance("gem", new BigDouble(5, 25));
            Assert.IsTrue(economy.SpendBig("gem", new BigDouble(2, 25), "upgrade"));
            validator.BigResponse.SetResult(false);
            refund = bus.Last<SpendRefundedSignal>();
            Assert.AreEqual("gem", refund.CurrencyId);
            Assert.AreEqual(long.MaxValue, refund.Amount, "A refund above long.MaxValue saturates in the long field.");
            Assert.AreEqual(new BigDouble(2, 25), refund.BigAmount);
            Assert.AreEqual("upgrade", refund.Reason);
            Assert.AreEqual(2, bus.Count<SpendRefundedSignal>());
            Assert.AreEqual(new BigDouble(5, 25), economy.GetBigBalance("gem"));
        }

        [Test]
        public void LegacyPrefsAdapter_DefaultBigMethodsRoundTripInvariantly()
        {
            IPlayerPrefsService prefs = new Prefs();
            var originalCulture = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
                var value = new BigDouble(1.2345678901234567, 123);
                prefs.SetBigDouble("value", value);
                Assert.AreEqual(value, prefs.GetBigDouble("value"));
                prefs.SetString("broken", "NaN;4");
                Assert.AreEqual(BigDouble.One, prefs.GetBigDouble("broken", BigDouble.One));
            }
            finally { CultureInfo.CurrentCulture = originalCulture; }
        }

        [Test]
        public void UnityPrefs_BigDoublePreservesMantissaBitsAcrossCultures()
        {
            var prefs = new UnityPlayerPrefsService();
            string key = "nexus-latest-big-" + Guid.NewGuid().ToString("N");
            var originalCulture = CultureInfo.CurrentCulture;
            try
            {
                var culture = (CultureInfo)CultureInfo.GetCultureInfo("tr-TR").Clone();
                culture.NumberFormat.NegativeSign = "~";
                CultureInfo.CurrentCulture = culture;
                var value = new BigDouble(1.2345678901234567, -321);
                prefs.SetBigDouble(key, value);
                Assert.AreEqual(value.Mantissa.ToString("R", CultureInfo.InvariantCulture) + ";-321", prefs.GetString(key));
                Assert.AreEqual(value, prefs.GetBigDouble(key));
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
                Assert.AreEqual(value, prefs.GetBigDouble(key));
            }
            finally
            {
                prefs.DeleteKey(key);
                CultureInfo.CurrentCulture = originalCulture;
            }
        }

        [Test]
        public void UnityPrefs_NonfiniteBigDoubleReturnsDefaultAndRejectsWrites()
        {
            var prefs = new UnityPlayerPrefsService();
            string key = "nexus-latest-big-" + Guid.NewGuid().ToString("N");
            try
            {
                foreach (string stored in new[] { "NaN;4", "Infinity;4", "-Infinity;4", "1e9999;4", "1;not-an-exponent" })
                {
                    prefs.SetString(key, stored);
                    Assert.AreEqual(BigDouble.One, prefs.GetBigDouble(key, BigDouble.One));
                }
                var valid = new BigDouble(1.2345678901234567, 123);
                prefs.SetBigDouble(key, valid);
                foreach (double mantissa in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
                {
                    var invalid = new BigDouble { Mantissa = mantissa, Exponent = 0 };
                    Assert.Throws<ArgumentOutOfRangeException>(() => prefs.SetBigDouble(key, invalid));
                    Assert.AreEqual(valid, prefs.GetBigDouble(key), "Rejected writes must preserve the previous value.");
                }
            }
            finally { prefs.DeleteKey(key); }
        }

        [Test]
        public void NeutralProvider_OverridesStaleVirtualJoystick()
        {
            var input = new InputService(); input.SetVirtualJoystickInput(Vector2.right);
            input.SetInputProvider(new Provider { Move = Vector2.zero }); input.UpdateInput(0);
            Assert.AreEqual(Vector2.zero, input.MoveInput);
            Assert.IsFalse(input.IsInputActive);
        }

        [Test]
        public void ProviderMovement_IsClampedAndOwnsButtonQueries()
        {
            var input = new InputService(); input.SetInputProvider(new Provider { Move = new Vector2(3, 4) });
            input.UpdateInput(0);
            Assert.AreEqual(1, input.MoveInput.sqrMagnitude, 0.00001);
            Assert.IsFalse(input.GetButton("Jump"));
        }
    }
}

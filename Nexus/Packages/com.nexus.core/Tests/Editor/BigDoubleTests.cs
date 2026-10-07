using NUnit.Framework;
using Nexus.Core;
using Nexus.Core.Services;

namespace Nexus.Editor.Tests
{
    [TestFixture]
    public class BigDoubleTests
    {
        [Test]
        public void BigDouble_ArithmeticOperations_WorkCorrectly()
        {
            BigDouble a = new BigDouble(1.5, 6); // 1.5M
            BigDouble b = new BigDouble(5.0, 5); // 500K

            BigDouble sum = a + b; // 2.0M
            Assert.AreEqual(2.0, sum.Mantissa, 0.001);
            Assert.AreEqual(6, sum.Exponent);

            BigDouble diff = a - b; // 1.0M
            Assert.AreEqual(1.0, diff.Mantissa, 0.001);
            Assert.AreEqual(6, diff.Exponent);

            BigDouble product = a * b; // 7.5e11
            Assert.AreEqual(7.5, product.Mantissa, 0.001);
            Assert.AreEqual(11, product.Exponent);

            BigDouble quotient = a / b; // 3.0
            Assert.AreEqual(3.0, quotient.Mantissa, 0.001);
            Assert.AreEqual(0, quotient.Exponent);
        }

        [Test]
        public void BigDouble_IdleSuffixFormatting_FormatsCorrectly()
        {
            BigDouble val1 = new BigDouble(1250); // 1.25K
            Assert.AreEqual("1.25K", val1.ToString());

            BigDouble val2 = new BigDouble(4.56, 6); // 4.56M
            Assert.AreEqual("4.56M", val2.ToString());

            BigDouble val3 = new BigDouble(7.89, 9); // 7.89B
            Assert.AreEqual("7.89B", val3.ToString());

            BigDouble val4 = new BigDouble(2.34, 15); // 2.34aa
            Assert.AreEqual("2.34aa", val4.ToString());
        }

        [Test]
        public void SecureObservableBigDouble_RAMObfuscation_And_OnChanged_Fires()
        {
            BigDouble init = new BigDouble(1.0, 6);
            var secureProp = new SecureObservableBigDouble(init);

            Assert.AreEqual(init, secureProp.Value);

            bool fired = false;
            BigDouble oldVal = BigDouble.Zero;
            BigDouble newVal = BigDouble.Zero;

            secureProp.OnChanged((o, n) =>
            {
                fired = true;
                oldVal = o;
                newVal = n;
            });

            BigDouble nextVal = new BigDouble(5.0, 6);
            secureProp.Value = nextVal;

            Assert.IsTrue(fired);
            Assert.AreEqual(init, oldVal);
            Assert.AreEqual(nextVal, newVal);
            Assert.AreEqual(nextVal, secureProp.Value);
        }

        [Test]
        public void EncryptedStorageService_BigDouble_SaveLoad_RoundTrips()
        {
            using var storage = new EncryptedStorageService("Test_Salt_BigDouble");
            BigDouble original = new BigDouble(9.87, 45);

            storage.SetBigDouble("User_Idle_Coins", original);
            storage.Save();

            BigDouble loaded = storage.GetBigDouble("User_Idle_Coins", BigDouble.Zero);
            Assert.AreEqual(original.Mantissa, loaded.Mantissa, 0.0001);
            Assert.AreEqual(original.Exponent, loaded.Exponent);

            storage.DeleteKey("User_Idle_Coins");
        }

        [Test]
        public void EconomyService_BigDouble_EarnSpendCanAfford_SupportsNumbersBeyondLongMax()
        {
            using var eco = new EconomyService();
            string currency = "IdleCookies";

            // 1e30 is vastly larger than long.MaxValue (~9.22e18)
            BigDouble astronomicalAmount = new BigDouble(5.0, 30);
            eco.EarnBig(currency, astronomicalAmount);

            Assert.IsTrue(eco.CanAffordBig(currency, astronomicalAmount));
            Assert.IsTrue(eco.CanAffordBig(currency, new BigDouble(2.0, 30)));
            Assert.IsFalse(eco.CanAffordBig(currency, new BigDouble(6.0, 30)));

            BigDouble spendAmount = new BigDouble(2.0, 30);
            bool spendSuccess = eco.SpendBig(currency, spendAmount);
            Assert.IsTrue(spendSuccess);

            BigDouble balance = eco.GetBigBalance(currency);
            Assert.AreEqual(3.0, balance.Mantissa, 0.001);
            Assert.AreEqual(30, balance.Exponent);
        }

        [Test]
        public void EconomyService_GetObservableBigBalance_ReturnsSecureObservableBigDouble_AndFiresOnChanged()
        {
            using var eco = new EconomyService();
            string currency = "CosmicStardust";

            var observable = eco.GetObservableBigBalance(currency);
            Assert.IsNotNull(observable);

            BigDouble receivedOld = BigDouble.Zero;
            BigDouble receivedNew = BigDouble.Zero;
            bool fired = false;

            observable.OnChanged((oldVal, newVal) =>
            {
                receivedOld = oldVal;
                receivedNew = newVal;
                fired = true;
            });

            BigDouble earnVal = new BigDouble(7.5, 20);
            eco.EarnBig(currency, earnVal);

            Assert.IsTrue(fired);
            Assert.AreEqual(BigDouble.Zero, receivedOld);
            Assert.AreEqual(earnVal, receivedNew);
            Assert.AreEqual(earnVal, observable.Value);
        }
    }
}

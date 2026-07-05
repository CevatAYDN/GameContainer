using NUnit.Framework;
using System.Threading.Tasks;
using Nexus.Core.Services;

namespace Nexus.Editor.Tests
{
    [TestFixture]
    public class MonetizationServiceTests
    {
        [Test]
        public async Task AdService_CooldownAndAvailabilityLogic()
        {
            var adService = new AdService();
            await adService.InitializeAsync(default);

            adService.SetInterstitialCooldown(0.1f);
            Assert.IsTrue(adService.IsInterstitialAvailable("default"));

            bool adClosed = false;
            adService.ShowInterstitial("default", () => adClosed = true);
            Assert.IsTrue(adClosed);

            bool rewardedCompleted = false;
            adService.ShowRewarded("default", (success) => rewardedCompleted = success);
            Assert.IsTrue(rewardedCompleted);
        }

        [Test]
        public async Task IapService_ProductRegistrationAndPurchaseMock()
        {
            var iapService = new IapService();
            await iapService.InitializeAsync(default);

            iapService.RegisterProducts(
                new ProductDefinition { Id = "no_ads", Type = ProductType.NonConsumable, PriceString = "$1.99" },
                new ProductDefinition { Id = "coins_100", Type = ProductType.Consumable, PriceString = "$0.99" }
            );

            var noAdsProd = iapService.GetProduct("no_ads");
            Assert.IsNotNull(noAdsProd);
            Assert.AreEqual("$1.99", noAdsProd.PriceString);

            Assert.IsFalse(iapService.IsProductOwned("no_ads"));

            bool purchased = false;
            iapService.PurchaseProduct("no_ads", (success, id) => purchased = success);
            Assert.IsTrue(purchased);
            Assert.IsTrue(iapService.IsProductOwned("no_ads"));
        }
    }
}

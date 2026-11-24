using System;
using System.Collections.Generic;
using System.Text;
using Nexus.Ads;
#if UNITY_EDITOR
using NUnit.Framework;
#endif

namespace Nexus.Editor.Tests
{
    /// <summary>
    /// Issue #444: Google's UMP US-states answer (the IAB GPP string it stores) becomes LevelPlay's do_not_sell. Engine-free: the host
    /// harness runs these (the pre-commit hook) and the editor runs them as NUnit. Section strings are built here bit by bit from the
    /// IAB field tables (Global-Privacy-Platform repository), independently of the decoder's code.
    /// </summary>
    public sealed class FullReviewAdsUsPrivacyTests
    {
        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        // Int(n) fields as bits, then base64url without padding: one GPP section's core segment.
        private static string Segment(params (int value, int bits)[] fields)
        {
            var bits = new StringBuilder();
            foreach (var (value, width) in fields)
                for (int i = width - 1; i >= 0; i--) bits.Append(((value >> i) & 1) == 1 ? '1' : '0');
            while (bits.Length % 6 != 0) bits.Append('0');
            const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
            var text = new StringBuilder();
            for (int i = 0; i < bits.Length; i += 6) text.Append(alphabet[Convert.ToInt32(bits.ToString(i, 6), 2)]);
            return text.ToString();
        }

        // usnat v1/v2 core: Version(6), SharingNotice, SaleOptOutNotice, SharingOptOutNotice, TargetedAdvertisingOptOutNotice,
        // SensitiveDataProcessingOptOutNotice, SensitiveDataLimitUseNotice (6 x Int(2)), SaleOptOut, SharingOptOut, TargetedAdvertisingOptOut, then more bits.
        private static string UsNat(int sale, int sharing, int version = 2) =>
            Segment((version, 6), (1, 2), (1, 2), (1, 2), (1, 2), (0, 2), (0, 2), (sale, 2), (sharing, 2), (2, 2), (0, 38)) + ".YA";

        // usca core: Version(6), SaleOptOutNotice, SharingOptOutNotice, SensitiveDataLimitUseNotice (3 x Int(2)), SaleOptOut, SharingOptOut, then more bits.
        private static string UsCa(int sale, int sharing) =>
            Segment((1, 6), (1, 2), (1, 2), (0, 2), (sale, 2), (sharing, 2), (0, 22)) + ".YA";

        private static Func<string, string> Store(string sids, params (int sid, string text)[] sections)
        {
            var map = new Dictionary<string, string> { ["IABGPP_GppSID"] = sids };
            foreach (var (sid, text) in sections) map["IABGPP_" + sid + "_String"] = text;
            return key => map.TryGetValue(key, out var v) ? v : null;
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void UsNational_SaleOptOut_IsDoNotSell()
            => Require(UsPrivacySignal.DoNotSell(Store("7", (7, UsNat(sale: 1, sharing: 2)))) == true, "SaleOptOut=1 opts out");

#if UNITY_EDITOR
        [Test]
#endif
        public void UsNational_SharingOptOut_IsDoNotSell()
            => Require(UsPrivacySignal.DoNotSell(Store("7", (7, UsNat(sale: 2, sharing: 1)))) == true, "SharingOptOut=1 opts out");

#if UNITY_EDITOR
        [Test]
#endif
        public void UsNational_NoOptOutOrNotApplicable_IsNotDoNotSell_ButTheSectionExists()
        {
            Require(UsPrivacySignal.DoNotSell(Store("7", (7, UsNat(2, 2)))) == false, "did not opt out");
            Require(UsPrivacySignal.DoNotSell(Store("7", (7, UsNat(0, 0)))) == false, "not applicable is no opt-out");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void California_UsesItsOwnOffsets()
        {
            Require(UsPrivacySignal.DoNotSell(Store("8", (8, UsCa(sale: 1, sharing: 2)))) == true, "usca SaleOptOut");
            Require(UsPrivacySignal.DoNotSell(Store("8", (8, UsCa(sale: 2, sharing: 1)))) == true, "usca SharingOptOut");
            Require(UsPrivacySignal.DoNotSell(Store("8", (8, UsCa(2, 2)))) == false, "usca no opt-out");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void NoUsSection_GivesNoDecision()
        {
            Require(UsPrivacySignal.DoNotSell(Store("2", (2, "CQ..."))) == null, "an EU TCF section is not a US signal");
            Require(UsPrivacySignal.DoNotSell(Store("")) == null, "no section ids");
            Require(UsPrivacySignal.DoNotSell(key => null) == null, "nothing stored");
            Require(UsPrivacySignal.DoNotSell(Store("7")) == null, "an id without its string");
            Require(UsPrivacySignal.DoNotSell(Store("7", (7, "!!"))) == null, "a malformed string");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void AnUnsupportedSectionVersionOrState_GivesNoDecision_NeverAGuess()
        {
            Require(UsPrivacySignal.DoNotSell(Store("7", (7, UsNat(1, 1, version: 3)))) == null, "usnat v3 is not decoded");
            Require(UsPrivacySignal.DoNotSell(Store("9", (9, UsCa(1, 1)))) == null, "usva is not decoded");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void SeveralUsSections_AnyOptOutWins()
        {
            var store = Store("8_7", (8, UsCa(2, 2)), (7, UsNat(1, 2)));
            Require(UsPrivacySignal.DoNotSell(store) == true, "the national section opted out");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void TheUsPrivacyString_IsReadOnlyWhenNoGppUsSectionExists()
        {
            Require(UsPrivacySignal.DoNotSell(key => key == "IABUSPrivacy_String" ? "1YYN" : null) == true, "opt-out Y");
            Require(UsPrivacySignal.DoNotSell(key => key == "IABUSPrivacy_String" ? "1YNN" : null) == false, "opt-out N");
            Require(UsPrivacySignal.DoNotSell(key => key == "IABUSPrivacy_String" ? "1---" : null) == null, "not applicable");
            Require(UsPrivacySignal.DoNotSell(key => key == "IABUSPrivacy_String" ? "junk" : null) == null, "malformed");
            var both = Store("7", (7, UsNat(2, 2)));
            Require(UsPrivacySignal.DoNotSell(key => key == "IABUSPrivacy_String" ? "1YYN" : both(key)) == false, "GPP wins over the older string");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void AnUnsupportedUsnatV3_FallsBackToTheUsPrivacyString()
        {
            var gpp = Store("7", (7, UsNat(2, 2, version: 3)));
            Require(UsPrivacySignal.DoNotSell(key => key == "IABUSPrivacy_String" ? "1YYN" : gpp(key)) == true, "v3 undecoded, the US string opts out");
        }

        private sealed class FakeFlow : IConsentFlow
        {
            public bool Completed => true;
            public bool CanRequestAds => true;
            public bool PrivacyOptionsRequired => false;
            public event Action Changed;
            public Action Pending;
            public void Request(Action onCompleted) => Pending = onCompleted;
            public void ShowPrivacyOptions(Action onClosed) { }
            public void Raise() => Changed?.Invoke();
            public bool HasSubscriber => Changed != null;
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void TheSync_AppliesAtTheAnswer_AndAgainOnlyWhenUmpsAnswerChanges()
        {
            var flow = new FakeFlow(); var log = new List<bool>();
            var current = Store("7", (7, UsNat(2, 2)));
            using (new UsPrivacySync(flow, key => current(key), log.Add))
            {
                Require(log.Count == 0, "nothing before UMP answers");
                flow.Pending();
                Require(log.Count == 1 && !log[0], "the stored answer is applied when the flow answers");
                flow.Raise();
                Require(log.Count == 1, "an unchanged answer is not sent again");
                current = Store("7", (7, UsNat(1, 2)));
                flow.Raise();
                Require(log.Count == 2 && log[1], "a changed answer (privacy options) is sent");
            }
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void TheSync_SendsNothingWithoutAUsSection_AndStopsOnDispose()
        {
            var flow = new FakeFlow(); var log = new List<bool>();
            var sync = new UsPrivacySync(flow, key => null, log.Add);
            flow.Pending();
            flow.Raise();
            Require(log.Count == 0, "no US section, no do_not_sell");
            sync.Dispose();
            Require(!flow.HasSubscriber, "disposed: unsubscribed");
        }
    }
}

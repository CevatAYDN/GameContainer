using System;

namespace Nexus.Ads
{
    /// <summary>
    /// The US-state sale/sharing opt-out Google's UMP writes (issue #444), as a LevelPlay <c>do_not_sell</c> decision. UMP stores the IAB
    /// Global Privacy Platform (GPP) answer in SharedPreferences (Android) / NSUserDefaults (iOS) under the keys of the GPP CMP API spec
    /// (Core/CMP API Specification.md, "Storage"): <c>IABGPP_GppSID</c> lists the section ids joined by "_", and <c>IABGPP_[SectionID]_String</c>
    /// holds each section. The older <c>IABUSPrivacy_String</c> ("1YNN") is read only when no GPP US section decides.
    /// </summary>
    public static class UsPrivacySignal
    {
        public const string SectionIdsKey = "IABGPP_GppSID";
        public const string UsPrivacyKey = "IABUSPrivacy_String";
        public static string SectionKey(int sectionId) => "IABGPP_" + sectionId + "_String";

        /// <summary>
        /// True: the player opted out of sale or sharing (send do_not_sell). False: a US section exists and says no opt-out. Null: no US signal
        /// (nothing stored, EU only, an unsupported section or version): the caller sends nothing.
        /// </summary>
        public static bool? DoNotSell(Func<string, string> store)
        {
            if (store == null) throw new ArgumentNullException(nameof(store));
            return FromGpp(store(SectionIdsKey), id => store(SectionKey(id))) ?? FromUsPrivacyString(store(UsPrivacyKey));
        }

        // shortcut: only usnat (7, version 1 and 2) and usca (8) are decoded; the other state sections (9 usva ... 23) have their own
        // offsets and usnat version 3 (MSPA 5th amendment, 2026-11) replaces SaleOptOut/SharingOptOut with MspaOptOut, which no CMP writes
        // yet. Those give no decision (null), never a guess. Add a section when a game's UMP message writes it.
        internal static bool? FromGpp(string sectionIds, Func<int, string> section)
        {
            if (string.IsNullOrEmpty(sectionIds)) return null;
            bool? result = null;
            foreach (var part in sectionIds.Split('_', ','))
            {
                if (!int.TryParse(part.Trim(), out var id) || (id != 7 && id != 8)) continue;
                var core = Core(section(id));
                if (core == null) continue;
                // IAB GPP section field tables, Int(n) fields in order, offsets in bits from the start of the core segment:
                //  usnat v1/v2 (Sections/US-National): Version 0-5, SharingNotice 6-7, SaleOptOutNotice 8-9, SharingOptOutNotice 10-11,
                //    TargetedAdvertisingOptOutNotice 12-13, SensitiveDataProcessingOptOutNotice 14-15, SensitiveDataLimitUseNotice 16-17,
                //    SaleOptOut 18-19, SharingOptOut 20-21.
                //  usca v1 (Sections/US-States/CA): Version 0-5, SaleOptOutNotice 6-7, SharingOptOutNotice 8-9, SensitiveDataLimitUseNotice 10-11,
                //    SaleOptOut 12-13, SharingOptOut 14-15.
                // Values: 0 not applicable, 1 opted out, 2 did not opt out.
                int version = Bits(core, 0, 6);
                if (version < 1 || (id == 7 && version > 2) || (id == 8 && version != 1)) continue;
                int saleAt = id == 7 ? 18 : 12;
                int sale = Bits(core, saleAt, 2), sharing = Bits(core, saleAt + 2, 2);
                if (sale < 0 || sharing < 0) continue;
                result = (result ?? false) || sale == 1 || sharing == 1;
            }
            return result;
        }

        // US Privacy String v1: version digit, notice, opt-out sale, LSPA: "1YNN"; opt-out sale is the third character.
        internal static bool? FromUsPrivacyString(string value)
        {
            if (value == null || value.Length != 4 || value[0] != '1') return null;
            switch (char.ToUpperInvariant(value[2]))
            {
                case 'Y': return true;
                case 'N': return false;
                default: return null;
            }
        }

        // the core segment is everything before the first '.', base64url without padding
        private static string Core(string sectionString)
        {
            if (string.IsNullOrEmpty(sectionString)) return null;
            int dot = sectionString.IndexOf('.');
            return dot < 0 ? sectionString : sectionString.Substring(0, dot);
        }

        // n bits starting at bit offset, from base64url characters of 6 bits each; -1 when the string is shorter or has a bad character
        private static int Bits(string core, int offset, int count)
        {
            int value = 0;
            for (int bit = offset; bit < offset + count; bit++)
            {
                int index = bit / 6;
                if (index >= core.Length) return -1;
                int sextet = Sextet(core[index]);
                if (sextet < 0) return -1;
                value = (value << 1) | ((sextet >> (5 - bit % 6)) & 1);
            }
            return value;
        }

        private static int Sextet(char c)
        {
            if (c >= 'A' && c <= 'Z') return c - 'A';
            if (c >= 'a' && c <= 'z') return c - 'a' + 26;
            if (c >= '0' && c <= '9') return c - '0' + 52;
            return c == '-' ? 62 : c == '_' ? 63 : -1;
        }
    }

    /// <summary>
    /// Forwards <see cref="UsPrivacySignal"/> to <paramref name="sink"/> (LevelPlay's do_not_sell) when the consent flow answers, and again
    /// whenever the flow's <see cref="IConsentFlow.Changed"/> shows UMP's answer is different (status update, form, privacy options).
    /// A value that did not change and a missing US signal send nothing.
    /// </summary>
    public sealed class UsPrivacySync : IDisposable
    {
        private readonly IConsentFlow _flow;
        private readonly Func<string, string> _store;
        private readonly Action<bool> _sink;
        private bool? _sent;
        private bool _disposed;

        public UsPrivacySync(IConsentFlow flow, Func<string, string> store, Action<bool> sink)
        {
            _flow = flow ?? throw new ArgumentNullException(nameof(flow));
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _sink = sink ?? throw new ArgumentNullException(nameof(sink));
            _flow.Changed += Apply;
            _flow.Request(Apply); // first in the flow's callback list: the answer is in LevelPlay before the controller initializes it
        }

        private void Apply()
        {
            if (_disposed) return;
            var decision = UsPrivacySignal.DoNotSell(_store);
            if (decision == null || decision == _sent) return;
            _sent = decision;
            _sink(decision.Value);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _flow.Changed -= Apply;
        }
    }
}

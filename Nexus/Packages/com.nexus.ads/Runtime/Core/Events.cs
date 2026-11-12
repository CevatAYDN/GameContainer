using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Nexus.Ads
{
    /// <summary>An impression's revenue as the mediation reports it, forwarded to analytics; a provider that leaves <see cref="Placement"/> empty gets the placement the controller last showed for that format.</summary>
    public readonly struct AdRevenue
    {
        public readonly string Network;
        public readonly string Placement;
        public readonly AdFormat Format;
        public readonly double Value;
        public readonly string Currency;
        /// <summary>The mediation's precision word ("exact", "estimated", "publisher_defined", "simulated", ...).</summary>
        public readonly string Precision;

        public AdRevenue(string network, string placement, AdFormat format, double value, string currency, string precision)
        {
            Network = network;
            Placement = placement;
            Format = format;
            Value = value;
            Currency = currency;
            Precision = precision;
        }
    }

    /// <summary>One paid reward the game has not acknowledged; see <see cref="AdController.PendingReward"/>.</summary>
    public readonly struct RewardReceipt
    {
        public readonly string Id;
        public readonly string Placement;
        /// <summary>Opaque caller offer token captured before Show. Null for legacy/uncontextualized calls.</summary>
        public readonly string Context;

        public RewardReceipt(string id, string placement) : this(id, placement, null) { }

        public RewardReceipt(string id, string placement, string context)
        {
            Id = id;
            Placement = placement;
            Context = context;
        }
    }

    /// <summary>
    /// Unacknowledged rewards in one versioned <see cref="IPacingStore"/> row, readable before a controller exists.
    /// Appends preserve older receipts (#413); acknowledgements remove only the named id, including out of order.
    /// The legacy id|placement row is read without rewriting it until a successful mutation.
    /// An unreadable row is never reset or silently overwritten: <see cref="Pending"/> and <see cref="Acknowledge"/> throw and
    /// keep it, while a new earned reward moves it to a quarantine key and goes on with an empty queue (#415), so one corrupt
    /// row cannot stop every later reward. <see cref="QuarantineUnreadable"/> does the same move on demand.
    /// All calls use the store's owning/main thread, like the controller. No in-memory queue is cached.
    /// </summary>
    public static class RewardReceipts
    {
        internal const string Key = "nexus.ads.reward.pending";
        // an unreadable row's raw value is kept under Key + ".corrupt." + the first free number (0, 1, 2, ...), never overwritten
        internal const string QuarantinePrefix = Key + ".corrupt.";
        private const string LegacyQueueHeader = "NXRQ2\n";
        private const string Header = "NXRQ3\n";
        private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

        /// <summary>The oldest unacknowledged reward, or null. An unreadable row throws <see cref="InvalidDataException"/> and is
        /// preserved, never reset and never written by this call; recover with <see cref="QuarantineUnreadable"/>.</summary>
        public static RewardReceipt? Pending(IPacingStore store)
        {
            var receipts = Read(store);
            return receipts.Count == 0 ? (RewardReceipt?)null : receipts[0];
        }

        /// <summary>Removes only this id; unknown or repeated acknowledgements do not write the store. An unreadable row throws
        /// <see cref="InvalidDataException"/> and is preserved; recover with <see cref="QuarantineUnreadable"/>.</summary>
        public static void Acknowledge(IPacingStore store, string receiptId)
        {
            var receipts = Read(store);
            int index = receipts.FindIndex(receipt => receipt.Id == receiptId);
            if (index < 0) return;
            receipts.RemoveAt(index);
            store.Set(Key, Encode(receipts));
        }

        /// <summary>
        /// Moves an unreadable reward row out of the way so earned rewards can be written again (#415). Returns the quarantine key
        /// that now holds the row's exact raw value (the main row is then emptied), or null when the row is empty or readable;
        /// nothing is written in that case. The raw value is copied first and the main row cleared second, so a failing store
        /// leaves the unreadable row where it was (the exception propagates). A quarantined row is never overwritten: the key is
        /// <c>nexus.ads.reward.pending.corrupt.</c> plus the first free number. Rewards inside the row are not recovered
        /// automatically; the game can read the key to decide.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="store"/> is null.</exception>
        public static string QuarantineUnreadable(IPacingStore store)
        {
            if (store == null) throw new ArgumentNullException(nameof(store));
            string raw = store.Get(Key);
            Decode(raw, out var failure);
            if (failure == null) return null;
            string quarantineKey = Quarantine(store, raw);
            store.Set(Key, Encode(new List<RewardReceipt>()));
            return quarantineKey;
        }

        /// <summary>Appends the receipt. Returns the quarantine key when the row was unreadable and was moved there first (#415),
        /// else null. Only a row that does not decode quarantines: a conflicting id, an invalid receipt or a failing store throw
        /// with the row untouched.</summary>
        internal static string Write(IPacingStore store, RewardReceipt receipt)
        {
            if (store == null) throw new ArgumentNullException(nameof(store));
            if (string.IsNullOrEmpty(receipt.Id) || receipt.Placement == null)
                throw new ArgumentException("A reward requires an id and a placement.", nameof(receipt));
            string raw = store.Get(Key);
            var receipts = Decode(raw, out var failure);
            string quarantineKey = null;
            if (failure != null)
            {
                // the unreadable value is kept first: if this throws, the main row is untouched and nothing was lost
                quarantineKey = Quarantine(store, raw);
                receipts = new List<RewardReceipt>();
            }
            foreach (var existing in receipts)
                if (existing.Id == receipt.Id)
                {
                    if (existing.Placement != receipt.Placement || existing.Context != receipt.Context)
                        throw new InvalidDataException("Reward id has conflicting placement/context.");
                    return quarantineKey;
                }
            receipts.Add(receipt);
            // One store write replaces the complete row. Never split head/tail across independent keys.
            store.Set(Key, Encode(receipts));
            return quarantineKey;
        }

        /// <summary>The telemetry row for a quarantine, worded once for the controller and the static facade.</summary>
        internal static AdEvent QuarantineEvent(AdFormat format, string placement, string quarantineKey)
            => new AdEvent(AdEventKind.RewardQuarantined, format, placement ?? "",
                "the unreadable reward row was kept unchanged under '" + quarantineKey + "' and the pending rewards restarted empty");

        private static string Quarantine(IPacingStore store, string raw)
        {
            for (int n = 0; n >= 0; n++)
            {
                string key = QuarantinePrefix + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (!string.IsNullOrEmpty(store.Get(key))) continue;
                store.Set(key, raw);
                return key;
            }
            throw new IOException("No free quarantine key for the unreadable reward row.");
        }

        private static List<RewardReceipt> Read(IPacingStore store)
        {
            var receipts = Decode(store?.Get(Key), out var failure);
            if (failure != null) throw failure;
            return receipts;
        }

        // The one place that tells an unreadable row (failure set, receipts empty) from a readable or empty one, so that a caller
        // quarantines on exactly that and never on a store failure or a conflicting id.
        private static List<RewardReceipt> Decode(string raw, out InvalidDataException failure)
        {
            failure = null;
            var receipts = new List<RewardReceipt>();
            if (string.IsNullOrEmpty(raw)) return receipts;
            bool hasContext = raw.StartsWith(Header, StringComparison.Ordinal);
            if (!hasContext && !raw.StartsWith(LegacyQueueHeader, StringComparison.Ordinal))
            {
                int bar = raw.IndexOf('|');
                if (bar <= 0 || raw.IndexOf('\n', 0, bar) >= 0)
                {
                    failure = new InvalidDataException("Unreadable reward receipt; the stored row was preserved.");
                    return receipts;
                }
                receipts.Add(new RewardReceipt(raw.Substring(0, bar), raw.Substring(bar + 1)));
                return receipts;
            }
            var ids = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                foreach (string row in raw.Substring(Header.Length).Split('\n'))
                {
                    string[] parts = row.Split('|');
                    if (parts.Length != (hasContext ? 3 : 2)) throw new FormatException("Invalid reward queue row.");
                    string id = Utf8.GetString(Convert.FromBase64String(parts[0]));
                    string placement = Utf8.GetString(Convert.FromBase64String(parts[1]));
                    string context = !hasContext || parts[2] == "-" ? null : Utf8.GetString(Convert.FromBase64String(parts[2]));
                    if (id.Length == 0 || !ids.Add(id)) throw new FormatException("Empty or duplicate reward id.");
                    receipts.Add(new RewardReceipt(id, placement, context));
                }
            }
            catch (Exception error) when (error is FormatException || error is DecoderFallbackException)
            {
                failure = new InvalidDataException("Unreadable reward queue; the stored row was preserved.", error);
                return new List<RewardReceipt>();
            }
            return receipts;
        }

        private static string Encode(List<RewardReceipt> receipts)
        {
            if (receipts.Count == 0) return "";
            var buffer = new StringBuilder(Header);
            for (int i = 0; i < receipts.Count; i++)
            {
                if (i != 0) buffer.Append('\n');
                buffer.Append(Convert.ToBase64String(Utf8.GetBytes(receipts[i].Id))).Append('|')
                    .Append(Convert.ToBase64String(Utf8.GetBytes(receipts[i].Placement))).Append('|')
                    .Append(receipts[i].Context == null ? "-" : Convert.ToBase64String(Utf8.GetBytes(receipts[i].Context)));
            }
            return buffer.ToString();
        }
    }

    public enum AdEventKind
    {
        Loaded,
        LoadFailed,
        NoFill,
        Shown,
        ShowFailed,
        Rewarded,
        Closed,
        Revenue,
        Violation,
        NotReady,
        KillSwitch,
        /// <summary>The SDK did not start; the detail names the attempt and the retry's delay.</summary>
        InitFailed,
        /// <summary>The consent flow allows no ad request; nothing starts until the player changes it in the privacy options.</summary>
        ConsentRefused,
        /// <summary>The SDK started; after failed starts the detail counts them.</summary>
        Initialized,
        /// <summary>The consent flow or the SDK start has not answered after <see cref="AdController.AnswerWaitSeconds"/>; the
        /// detail names which. The controller keeps waiting (a consent form may still be open, an SDK may wait for a network).</summary>
        Unanswered,
        /// <summary>A loaded ad the provider no longer has (it expired, or the SDK dropped it when consent changed); the format
        /// reloads (issue #175).</summary>
        Expired,
        /// <summary>A subscriber or a telemetry sink threw while the controller answered a show or raised ReadyChanged; the detail
        /// names the step and the exception. The rest of the answer, the game's result included, still ran (issue #176).</summary>
        HandlerFailed,
        /// <summary>Legacy replacement event (#178), retained for enum compatibility. Since #413 the built-in controller
        /// appends receipts and does not emit this event.</summary>
        RewardReplaced,
        /// <summary>An unreadable reward row was moved to a quarantine key so earned rewards can be written again (#415); the
        /// detail names the key, which holds the row's exact raw value. The queue went on empty: rewards that row held are not
        /// pending any more, so the game decides from the quarantined value whether any needs its own recovery.</summary>
        RewardQuarantined,
        /// <summary>The ad request or preload was refused because ads are suppressed, or suppression was toggled (issue #428).</summary>
        Suppressed,
    }

    /// <summary>One telemetry row. The Unity layer forwards it to the game's analytics; every row carries the placement name.</summary>
    public readonly struct AdEvent
    {
        public readonly AdEventKind Kind;
        public readonly AdFormat Format;
        public readonly string Placement;
        public readonly string Detail;

        public AdEvent(AdEventKind kind, AdFormat format, string placement, string detail = null)
        {
            Kind = kind;
            Format = format;
            Placement = placement;
            Detail = detail;
        }

        public override string ToString() => Kind + " " + Format + " '" + Placement + "'" + (Detail == null ? "" : ": " + Detail);
    }
}

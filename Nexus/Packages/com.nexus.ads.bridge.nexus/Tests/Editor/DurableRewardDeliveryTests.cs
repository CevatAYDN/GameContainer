using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Nexus.Core.Services;
#if UNITY_EDITOR
using NUnit.Framework;
#endif

namespace Nexus.Ads.Bridge.Tests
{
    // Also compiled by the host. Only path discovery uses reflection, strictly for filesystem fault injection.
    // All delivery and reopen assertions use the public consumer API.
    public sealed class DurableRewardDeliveryTests
    {
#if UNITY_EDITOR
        [Test]
#endif
        public void BoolCallback_FailedSave_ReopensWithPendingReward() => FailedSave(false);

#if UNITY_EDITOR
        [Test]
#endif
        public void OutcomeCallback_FailedSave_ReopensWithPendingReward() => FailedSave(true);

        private static void FailedSave(bool outcome)
        {
            using var files = new Files();
            var game = files.OpenGame();
            var receipts = files.OpenReceipts();
            using var rig = new Rig(receipts);
            files.Block(game, "wallet");
            files.ExpectFailedDispose(game);
            ExpectIOException(() => rig.Deliver(outcome, () => Grant(game, rig.Controller.DeliveringReward.Value)));
            // Abrupt reopen BEFORE disposal/retry: the failed instance must not repair the save.
            var reopenedGame = files.OpenGame();
            var reopenedReceipts = files.OpenReceipts();
            using var restarted = new Rig(reopenedReceipts);
            Require(reopenedGame.GetString("wallet", null) == null, "blocked balance must not reach disk");
            Require(restarted.Controller.PendingReward.HasValue,
                "a failed game save must retain the independently durable receipt");
            string id = restarted.Controller.PendingReward.Value.Id;
            files.Close(game); // failed store is disposed while still blocked, never repaired by cleanup
            files.Unblock(reopenedGame, "wallet");
            Grant(reopenedGame, restarted.Controller.PendingReward.Value);
            restarted.Controller.AcknowledgeReward(id);
            restarted.Controller.AcknowledgeReward(id);
            var finalGame = files.OpenGame();
            using var final = new Rig(files.OpenReceipts());
            Require(ReadWallet(finalGame).Balance == 10 && ReadWallet(finalGame).LastReceiptId == id,
                "successful startup replay must persist one grant and its receipt together");
            Require(!final.Controller.PendingReward.HasValue && restarted.Receipts.Acknowledgements == 1,
                "successful replay must clear the durable receipt once");
        }

        [Serializable]
        public sealed class Wallet
        {
            public int Balance;
            public string LastReceiptId;
        }

        private static Wallet ReadWallet(IPlayerPrefsService game)
        {
            var status = game.TryGetString("wallet", out string blob);
            if (status == StorageReadStatus.Unreadable) throw new IOException("wallet is unreadable; do not reset it");
            return status == StorageReadStatus.Absent ? new Wallet() : UnityEngine.JsonUtility.FromJson<Wallet>(blob);
        }

        private static void Grant(IPlayerPrefsService game, RewardReceipt receipt)
        {
            // One atomic JSON blob holds BOTH balance and id; no multiple-key transaction is implied.
            var wallet = ReadWallet(game);
            if (wallet.LastReceiptId != receipt.Id)
            {
                wallet.Balance += 10;
                wallet.LastReceiptId = receipt.Id;
                game.SetString("wallet", UnityEngine.JsonUtility.ToJson(wallet));
            }
            game.SaveOrThrow();
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void StartupReplay_FailedSave_RetainsReceiptAcrossAnotherReopen()
        {
            using var files = new Files();
            using (var interrupted = new Rig(files.OpenReceipts()))
                ExpectIOException(() => interrupted.Deliver(true, () => throw new IOException("killed before grant")));
            var game = files.OpenGame();
            files.Block(game, "wallet");
            files.ExpectFailedDispose(game);
            using var restarted = new Rig(files.OpenReceipts());
            string id = restarted.Controller.PendingReward.Value.Id;
            ExpectIOException(() =>
            {
                Grant(game, restarted.Controller.PendingReward.Value);
                restarted.Controller.AcknowledgeReward(id);
            });
            var reopenedGame = files.OpenGame();
            using var reopened = new Rig(files.OpenReceipts());
            Require(ReadWallet(reopenedGame).Balance == 0 && reopened.Controller.PendingReward.Value.Id == id,
                "failed startup replay must leave both durable balance and pending receipt unchanged");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void CommitBeforeAckFailure_ReplaysWithoutDuplicate() => CommitBeforeAckFailure(true);

#if UNITY_EDITOR
        [Test]
#endif
        public void BoolCommitBeforeAckFailure_ReplaysWithoutDuplicate() => CommitBeforeAckFailure(false);

#if UNITY_EDITOR
        [Test]
#endif
        public void SuccessfulCallbacks_CommitDistinctRewards_AndClearEachReceiptOnce()
        {
            foreach (bool outcome in new[] { false, true })
            {
                using var files = new Files();
                var game = files.OpenGame();
                using var rig = new Rig(files.OpenReceipts());
                string firstId = null, lastId = null;
                rig.Deliver(outcome, () =>
                {
                    firstId = rig.Controller.DeliveringReward.Value.Id;
                    Grant(game, rig.Controller.DeliveringReward.Value);
                });
                rig.Deliver(outcome, () =>
                {
                    lastId = rig.Controller.DeliveringReward.Value.Id;
                    Grant(game, rig.Controller.DeliveringReward.Value);
                });
                var reopenedGame = files.OpenGame();
                using var reopened = new Rig(files.OpenReceipts());
                var wallet = ReadWallet(reopenedGame);
                Require(firstId != lastId && wallet.Balance == 20 && wallet.LastReceiptId == lastId,
                    "distinct successful receipts must add their grants to the same durable wallet");
                Require(!reopened.Controller.PendingReward.HasValue && rig.Receipts.Acknowledgements == 2,
                    "each successful callback must clear its receipt once");
            }
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void NexusEconomyReceipt_CallbackAndStartupFailures_NeverAcknowledgeAnUncommittedGrant()
        {
            foreach (bool outcome in new[] { false, true })
                foreach (bool walletFault in new[] { false, true })
                {
                    using var files = new Files();
                    var game = files.OpenGame();
                    using var economy = new EconomyService { PlayerPrefsService = game };
                    economy.SetBalance("coin", 100);
                    using var rig = new Rig(files.OpenReceipts());
                    if (walletFault)
                    {
                        files.Block(game, "NT_EcoWallet_coin");
                        files.ExpectFailedDispose(game);
                    }
                    else rig.Receipts.FailAcknowledgement = true;
                    ExpectIOException(() => rig.Deliver(outcome,
                        () => economy.EarnOnce("coin", 40, rig.Controller.DeliveringReward.Value.Id, "reward")));
                    string id = rig.Controller.PendingReward.Value.Id;
                    economy.Dispose(); // keep a failed writer blocked through its final save
                    files.Close(game);
                    if (walletFault) files.Unblock(game, "NT_EcoWallet_coin");

                    var reopenedGame = files.OpenGame();
                    using var restartedEconomy = new EconomyService { PlayerPrefsService = reopenedGame };
                    using var restarted = new Rig(files.OpenReceipts());
                    Require(restarted.Controller.PendingReward.Value.Id == id && rig.Receipts.Acknowledgements == 0,
                        "the adapter must retain the exact receipt when economy confirmation or acknowledgement fails");
                    Require(restartedEconomy.GetBalance("coin") == (walletFault ? 100 : 140),
                        "abrupt reopen must expose the old wallet or the complete grant, never a partial receipt");
                    if (!walletFault) Require(restartedEconomy.Spend("coin", 30), "an unrelated spend may precede replay");
                    Require(restartedEconomy.EarnOnce("coin", 40, restarted.Controller.PendingReward.Value.Id) == walletFault,
                        "startup pays only a missing grant, regardless of the changed balance");
                    restarted.Controller.AcknowledgeReward(id);
                    restarted.Controller.AcknowledgeReward(id);
                    using var finalEconomy = new EconomyService { PlayerPrefsService = files.OpenGame() };
                    using var final = new Rig(files.OpenReceipts());
                    Require(finalEconomy.GetBalance("coin") == (walletFault ? 140 : 110)
                        && !final.Controller.PendingReward.HasValue && restarted.Receipts.Acknowledgements == 1,
                        "both callback APIs must close only after one durable currency grant and one acknowledgement");
                }
        }

        private static void CommitBeforeAckFailure(bool outcome)
        {
            using var files = new Files();
            var game = files.OpenGame();
            using var rig = new Rig(files.OpenReceipts());
            rig.Receipts.FailAcknowledgement = true;
            ExpectIOException(() => rig.Deliver(outcome, () => Grant(game, rig.Controller.DeliveringReward.Value)));
            string id = rig.Controller.PendingReward.Value.Id;
            var reopenedGame = files.OpenGame();
            using var restarted = new Rig(files.OpenReceipts());
            Require(ReadWallet(reopenedGame).Balance == 10 && restarted.Controller.PendingReward.Value.Id == id,
                "committed balance and unacknowledged receipt must survive reopen");
            Grant(reopenedGame, restarted.Controller.PendingReward.Value);
            restarted.Controller.AcknowledgeReward(id);
            restarted.Controller.AcknowledgeReward(id);
            var finalGame = files.OpenGame();
            using var final = new Rig(files.OpenReceipts());
            Require(ReadWallet(finalGame).Balance == 10 && !final.Controller.PendingReward.HasValue
                && restarted.Receipts.Acknowledgements == 1, "commit-before-ack replay must not duplicate the grant");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void LegacyVoidSave_RemainsUnconfirmed_AndNormalCallbackStillAcknowledges()
        {
            foreach (bool outcome in new[] { false, true })
            {
                using var files = new Files();
                var game = files.OpenGame();
                files.Block(game, "wallet");
                files.ExpectFailedDispose(game);
                using var rig = new Rig(files.OpenReceipts());
                rig.Deliver(outcome, () => { game.SetInt("wallet", 10); game.Save(); });
                Require(files.OpenGame().GetInt("wallet") == 0, "legacy failed Save must remain nonthrowing and uncommitted");
                using var reopened = new Rig(files.OpenReceipts());
                Require(!reopened.Controller.PendingReward.HasValue,
                    "legacy callbacks still acknowledge on normal return; consumers must opt in");
            }
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void LegacyThrowingCallbacks_RetainReceipt()
        {
            foreach (bool outcome in new[] { false, true })
            {
                using var files = new Files();
                using var rig = new Rig(files.OpenReceipts());
                ExpectIOException(() => rig.Deliver(outcome, () => throw new IOException("consumer failed")));
                using var reopened = new Rig(files.OpenReceipts());
                Require(reopened.Controller.PendingReward.HasValue, "legacy throwing callbacks must still retain their receipt");
            }
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void NonRewardOutcomes_DoNotGrantOrAcknowledge()
        {
            foreach (bool outcome in new[] { false, true })
                foreach (AdResult result in new[] { AdResult.Closed, AdResult.Failed, AdResult.NotReady, AdResult.Blocked })
                {
                    using var files = new Files();
                    using var rig = new Rig(files.OpenReceipts());
                    int grants = 0;
                    AdOutcome? heard = null;
                    bool? paid = null;
                    string placement = result == AdResult.Blocked ? "unknown" : "reward";
                    if (result == AdResult.NotReady) rig.RemoveFill();
                    if (outcome) rig.Adapter.RequestReward(placement, o => { heard = o; if (o == AdOutcome.Rewarded) grants++; });
                    else rig.Adapter.ShowRewarded(placement, b => { paid = b; if (b) grants++; });
                    if (result == AdResult.Closed || result == AdResult.Failed) rig.Complete(result);
                    Require(grants == 0 && rig.Receipts.Acknowledgements == 0 && !rig.Controller.PendingReward.HasValue,
                        "non-reward outcomes must not grant or acknowledge");
                    Require(outcome ? heard == NexusAdsAdapter.Map(result) : paid == false, "outcome mapping must remain unchanged");
                }
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void ReentrantCallback_DoesNotAcknowledgeANewerReceipt()
        {
            foreach (bool outcome in new[] { false, true })
            {
                using var files = new Files();
                using var rig = new Rig(files.OpenReceipts());
                string newerId = null;
                rig.Deliver(outcome, () =>
                {
                    ExpectIOException(() => rig.Deliver(outcome, () =>
                    {
                        newerId = rig.Controller.DeliveringReward.Value.Id;
                        throw new IOException("newer grant failed");
                    }));
                });
                using var reopened = new Rig(files.OpenReceipts());
                Require(reopened.Controller.PendingReward.HasValue && reopened.Controller.PendingReward.Value.Id == newerId,
                    "the older callback must not acknowledge the newer failed callback's receipt");
            }
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void BoolReadyChangedReentry_PreservesTheNewerFailedReceipt() => BeforeCallbackReentry(false, false);

#if UNITY_EDITOR
        [Test]
#endif
        public void OutcomeReadyChangedReentry_PreservesTheNewerFailedReceipt() => BeforeCallbackReentry(true, false);

#if UNITY_EDITOR
        [Test]
#endif
        public void BoolReadyChangedReentry_GrantsTheOriginalDeliveryId() => BeforeCallbackReentry(false, true);

#if UNITY_EDITOR
        [Test]
#endif
        public void OutcomeReadyChangedReentry_GrantsTheOriginalDeliveryId() => BeforeCallbackReentry(true, true);

#if UNITY_EDITOR
        [Test]
#endif
        public void TelemetryAndAdClosedReentry_KeepTheOriginalGrantAndTheNewerPendingReceipt()
        {
            foreach (bool outcome in new[] { false, true })
                foreach (string notification in new[] { "telemetry", "closed" })
                    BeforeCallbackReentry(outcome, true, notification);
        }

        private static void BeforeCallbackReentry(bool outcome, bool checkGrantId, string notification = "ready")
        {
            using var files = new Files();
            var game = files.OpenGame();
            var telemetry = new Telemetry();
            using var rig = new Rig(files.OpenReceipts(), telemetry);
            string originalId = null, newerId = null;
            Action reenter = null;
            Action<AdFormat, AdResult> closed = (_, result) => { if (result == AdResult.Rewarded) reenter(); };
            reenter = () =>
            {
                rig.Adapter.ReadyChanged -= reenter;
                rig.Controller.AdClosed -= closed;
                telemetry.OnRewarded = null;
                originalId = rig.Controller.PendingReward.Value.Id;
                if (notification != "ready") rig.Refill(); // provider reload before the controller's usual Preload step
                ExpectIOException(() => rig.Deliver(outcome, () =>
                {
                    newerId = rig.Controller.DeliveringReward.Value.Id;
                    throw new IOException("newer grant failed before the original callback");
                }));
            };
            if (notification == "ready") rig.Adapter.ReadyChanged += reenter;
            else if (notification == "closed") rig.Controller.AdClosed += closed;
            else telemetry.OnRewarded = reenter;
            rig.Deliver(outcome, () => Grant(game, rig.Controller.DeliveringReward.Value));
            var reopenedGame = files.OpenGame();
            var wallet = ReadWallet(reopenedGame);
            using var reopened = new Rig(files.OpenReceipts());
            string pendingId = reopened.Controller.PendingReward?.Id;
            Require(originalId != null && newerId != null && originalId != newerId, "both shows must produce distinct receipts");
            string detail = $"original={originalId}; newer={newerId}; wallet={wallet.LastReceiptId}; pending={pendingId ?? "none"}";
            if (checkGrantId)
                Require(wallet.Balance == 10 && wallet.LastReceiptId == originalId,
                    "the consumer must grant the original delivery id, not the replaced pending id: " + detail);
            else
                Require(pendingId == newerId && rig.Receipts.Acknowledgements == 0,
                    "the original callback must not acknowledge the newer failed receipt: " + detail);
            Grant(reopenedGame, reopened.Controller.PendingReward.Value);
            reopened.Controller.AcknowledgeReward(newerId);
            using var final = new Rig(files.OpenReceipts());
            Require(ReadWallet(files.OpenGame()).Balance == 20 && !final.Controller.PendingReward.HasValue,
                "replaying the newer failed receipt must add only its missing grant");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void DeliverySnapshot_RestoresAfterNestedAndNonRewardCallbacks_AndClearsAfterThrow()
        {
            foreach (bool outcome in new[] { false, true })
            {
                using var files = new Files();
                using var rig = new Rig(files.OpenReceipts());
                Require(!rig.Controller.DeliveringReward.HasValue, "no delivery snapshot before a callback");
                ExpectIOException(() => rig.Deliver(outcome, () =>
                {
                    string original = rig.Controller.DeliveringReward.Value.Id;
                    rig.Controller.Show("unknown", result =>
                    {
                        Require(result == AdResult.Blocked && !rig.Controller.DeliveringReward.HasValue,
                            "a non-reward nested callback must not inherit the outer receipt");
                    });
                    rig.Controller.Show("unknown", null); // existing Show(null callback) remains unambiguous
                    Require(rig.Controller.DeliveringReward.Value.Id == original, "outer receipt must be restored after non-reward delivery");
                    ExpectIOException(() => rig.Deliver(outcome, () =>
                    {
                        Require(rig.Controller.DeliveringReward.Value.Id != original, "nested reward must have its own snapshot");
                        throw new IOException("nested callback failed");
                    }));
                    Require(rig.Controller.DeliveringReward.Value.Id == original, "outer receipt must be restored after nested exception");
                    throw new IOException("outer callback failed");
                }));
                Require(!rig.Controller.DeliveringReward.HasValue, "no stale delivery snapshot after a throwing callback");
            }
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void ReceiptWriteFailure_DoesNotDeliverAnOlderPendingId()
        {
            foreach (bool outcome in new[] { false, true })
            {
                using var files = new Files();
                using var rig = new Rig(files.OpenReceipts());
                ExpectIOException(() => rig.Deliver(outcome, () => throw new IOException("leave first pending")));
                string original = rig.Controller.PendingReward.Value.Id;
                rig.Receipts.FailWrites = true;
                ExpectIOException(() => rig.Deliver(outcome, () =>
                {
                    Require(!rig.Controller.DeliveringReward.HasValue, "failed receipt write must not substitute an older pending id");
                    throw new IOException("receipt persistence was not confirmed");
                }));
                using var reopened = new Rig(files.OpenReceipts());
                Require(reopened.Controller.PendingReward.Value.Id == original && rig.Receipts.Acknowledgements == 0,
                    "the older pending receipt must remain intact when this show's receipt could not be written");
            }
        }

        private static void ExpectIOException(Action action)
        {
            try { action(); }
            catch (IOException) { return; }
            throw new InvalidOperationException("failed persistence must be observable as IOException");
        }

        public static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private sealed class Files : IDisposable
        {
            private readonly string _gameSalt = "338_game_" + Guid.NewGuid().ToString("N");
            private readonly string _receiptSalt = "338_receipts_" + Guid.NewGuid().ToString("N");
            private readonly List<EncryptedStorageService> _stores = new List<EncryptedStorageService>();
            private readonly HashSet<string> _folders = new HashSet<string>();
            private readonly HashSet<EncryptedStorageService> _failedDisposals = new HashSet<EncryptedStorageService>();
            private readonly HashSet<EncryptedStorageService> _closed = new HashSet<EncryptedStorageService>();
            public void ExpectFailedDispose(EncryptedStorageService store) => _failedDisposals.Add(store);
            public EncryptedStorageService OpenGame() => Open(_gameSalt);
            public EncryptedStorageService OpenReceipts() => Open(_receiptSalt);
            private EncryptedStorageService Open(string salt)
            {
                var store = new EncryptedStorageService(salt);
                _stores.Add(store);
                _folders.Add(Path.GetDirectoryName(PathFor(store, "wallet")));
                return store;
            }
            public void Block(EncryptedStorageService store, string key)
                => Directory.CreateDirectory(PathFor(store, key) + ".tmp");
            public void Unblock(EncryptedStorageService store, string key) => Directory.Delete(PathFor(store, key) + ".tmp");
            public static string PathFor(EncryptedStorageService store, string key)
                => (string)typeof(EncryptedStorageService).GetMethod("GetFilePath", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(store, new object[] { key });
            public void Dispose()
            {
                // Keep the collision in place through every Dispose; no retroactive successful flush.
                foreach (var store in _stores) Close(store);
                foreach (string folder in _folders)
                {
                    string full = Path.GetFullPath(folder);
                    string root = Path.GetFullPath(UnityEngine.Application.persistentDataPath) + Path.DirectorySeparatorChar;
                    Require(full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                        && (Path.GetFileName(full) == "SecureData_" + _gameSalt || Path.GetFileName(full) == "SecureData_" + _receiptSalt),
                        "cleanup must stay inside the uniquely salted fixture");
                    if (Directory.Exists(full)) Directory.Delete(full, true);
                }
                UnityEngine.PlayerPrefs.DeleteKey("NT_StorageSeed_" + _gameSalt);
                UnityEngine.PlayerPrefs.DeleteKey("NT_StorageSeed_" + _receiptSalt);
                UnityEngine.PlayerPrefs.Save();
            }
            public void Close(EncryptedStorageService store)
            {
                if (!_closed.Add(store)) return;
#if UNITY_EDITOR
                if (_failedDisposals.Contains(store))
                    UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Error,
                        new System.Text.RegularExpressions.Regex(@"\[EncryptedStorageService\] Disposed with .* final save could not write or delete"));
#endif
                store.Dispose();
            }
        }

        private sealed class ReceiptStore : IPacingStore
        {
            private readonly IPlayerPrefsService _store;
            public bool FailAcknowledgement;
            public bool FailWrites;
            public int Acknowledgements;
            public ReceiptStore(IPlayerPrefsService store) => _store = store;
            public string Get(string key) => _store.GetString(key, null);
            public void Set(string key, string value)
            {
                if (FailWrites) throw new IOException("receipt-store write failed");
                if (value == "")
                {
                    if (FailAcknowledgement) throw new IOException("receipt acknowledgement interrupted before commit");
                    Acknowledgements++;
                }
                _store.SetString(key, value);
                _store.SaveOrThrow();
            }
        }

        private sealed class Rig : IDisposable
        {
            private readonly SimulatorProvider _provider;
            public readonly AdController Controller;
            private readonly NexusAdsAdapter _adapter;
            public NexusAdsAdapter Adapter => _adapter;
            public readonly ReceiptStore Receipts;
            public Rig(IPlayerPrefsService receiptStore, IAdTelemetry telemetry = null)
            {
                var scheduler = new Scheduler();
                _provider = new SimulatorProvider(scheduler) { Interactive = true };
                var config = new AdsConfig(new[] { new AdPlacement("reward", AdFormat.Rewarded) }, AdPolicy.Default,
                    new ProviderConfig("key", new Dictionary<AdFormat, string>(), true, null, false, false, false, "G"), BuildKind.Editor);
                Receipts = new ReceiptStore(receiptStore);
                Controller = new AdController(config, _provider, new Clock(), new Visibility(), new MainThread(),
                    scheduler, Receipts, telemetry ?? new Telemetry(), _provider);
                _adapter = new NexusAdsAdapter(Controller);
                _adapter.Initialize(null);
            }
            public void Deliver(bool outcome, Action grant)
            {
                if (outcome) _adapter.RequestReward("reward", result => { if (result == AdOutcome.Rewarded) grant(); });
                else _adapter.ShowRewarded("reward", result => { if (result) grant(); });
                Require(_provider.CompleteShow(AdResult.Rewarded), "simulator must complete the shown reward");
            }
            public void Complete(AdResult result) => Require(_provider.CompleteShow(result), "simulator must answer");
            public void Refill() => _provider.Load(AdFormat.Rewarded);
            public void RemoveFill() { _provider.Scenario = SimulatorScenario.NoFill; _provider.Load(AdFormat.Rewarded); }
            public void Dispose() { _adapter.Dispose(); Controller.Dispose(); }
        }

        private sealed class Clock : IAdClock { public double Seconds => 100; public long UtcDay => 1; public double UtcSeconds => 86400; }
        private sealed class Visibility : IAppVisibility { public bool Visible => true; public event Action<bool> Changed { add { } remove { } } }
        private sealed class MainThread : IMainThread { public bool IsCurrent => true; public void Post(Action action) => action(); }
        private sealed class Scheduler : IAdScheduler
        {
            private sealed class Handle : IDisposable { public void Dispose() { } }
            public IDisposable Schedule(double seconds, Action action) => new Handle();
        }
        private sealed class Telemetry : IAdTelemetry
        {
            public Action OnRewarded;
            public void Record(in AdEvent e) { if (e.Kind == AdEventKind.Rewarded) OnRewarded?.Invoke(); }
        }
    }
}

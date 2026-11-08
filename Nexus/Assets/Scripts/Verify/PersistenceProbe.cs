#if UNITY_ANDROID && NEXUS_DEVICE_PROBE && !UNITY_EDITOR
using System;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using Nexus.Ads;
using Nexus.Ads.Bridge;
using Nexus.Ads.Unity;
using Nexus.Core.Services;
using UnityEngine;

namespace NexusVerify.Device
{
    // #338: intent-gated, salted fixtures only. The driver kills real processes, not a disposed/reopened rig.
    // This proves process restart with the simulator, not power-loss durability or a multi-key transaction.
    internal sealed class PersistenceProbe : MonoBehaviour
    {
        private const string Package = "com.UnityTechnologies.com.unity.template.urpblank";
        private string _run, _variant, _scenario, _phase, _expectedReceipt;
        private int _pid, _checks, _callbacks;
        private EncryptedStorageService _wallet, _encryptedReceipts;
        private ReceiptStore _receipts;
        private SimulatorProvider _provider;
        private AdController _controller;
        private NexusAdsAdapter _adapter;
        private IOException _expectedSaveFailure;
        private bool _committed;
        private string _walletSalt, _receiptSalt;
        private bool _telemetryFailed;

        [Serializable]
        public sealed class Wallet
        {
            public int Balance;
            public string LastReceiptId;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Create()
        {
            using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
            using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
            using var intent = activity.Call<AndroidJavaObject>("getIntent");
            if (intent.Call<string>("getStringExtra", "nexusProbe") != "persistence") return;
            var go = new GameObject("[NexusVerify.PersistenceProbe]");
            DontDestroyOnLoad(go);
            var probe = go.AddComponent<PersistenceProbe>();
            probe._run = intent.Call<string>("getStringExtra", "nexusRunToken");
            probe._variant = intent.Call<string>("getStringExtra", "nexusCallback");
            probe._scenario = intent.Call<string>("getStringExtra", "nexusFault");
            probe._phase = intent.Call<string>("getStringExtra", "nexusPhase");
            probe._expectedReceipt = intent.Call<string>("getStringExtra", "nexusReceiptId");
            using var process = new AndroidJavaClass("android.os.Process");
            probe._pid = process.CallStatic<int>("myPid");
        }

        private void Start()
        {
            try
            {
                Check(Application.identifier == Package && Debug.isDebugBuild, "framework-development-app");
#if ENABLE_IL2CPP
                Check(true, "il2cpp");
#else
                throw new InvalidOperationException("IL2CPP is required");
#endif
                Check(_run != null && Regex.IsMatch(_run, @"\A[A-Za-z0-9]{8,64}\z"), "run-token");
                Check(_variant == "bool" || _variant == "outcome", "callback-variant");
                Check(_scenario == "wallet" || _scenario == "ack", "fault-scenario");
                Check(_phase == "arm" || _phase == "recover" || _phase == "verify", "phase");
                Check(_phase == "arm" ? string.IsNullOrEmpty(_expectedReceipt)
                    : _expectedReceipt != null && Regex.IsMatch(_expectedReceipt, @"\A[A-Za-z0-9-]{1,80}\z"), "receipt-argument");
                Log("START", "il2cpp=1 development=1");
                _walletSalt = "338_probe_" + _run + "_" + _variant + "_" + _scenario + "_wallet";
                _receiptSalt = "338_probe_" + _run + "_" + _variant + "_" + _scenario + "_receipts";
                if (_phase == "arm")
                {
                    Check(FixtureAbsent(_walletSalt) && FixtureAbsent(_receiptSalt), "fresh-fixtures");
                }
                else
                {
                    Check(FixtureExists(_walletSalt) && FixtureExists(_receiptSalt), "existing-fixtures");
                }
                Open();
                switch (_phase)
                {
                    case "arm": Arm(); break;
                    case "recover": Recover(); break;
                    case "verify": Verify(); break;
                }
            }
            catch (Exception ex)
            {
                Log("FAILED", "reason=" + ex.GetType().Name + ":" + Clean(ex.Message));
            }
        }

        private static bool FixtureAbsent(string salt)
            => !Directory.Exists(Path.Combine(Application.persistentDataPath, "SecureData_" + salt))
                && !PlayerPrefs.HasKey("NT_StorageSeed_" + salt);

        private static bool FixtureExists(string salt)
            => Directory.Exists(Path.Combine(Application.persistentDataPath, "SecureData_" + salt))
                && !string.IsNullOrEmpty(PlayerPrefs.GetString("NT_StorageSeed_" + salt, null));

        private void Open()
        {
            _wallet = new EncryptedStorageService(_walletSalt);
            _encryptedReceipts = new EncryptedStorageService(_receiptSalt);
            _receipts = new ReceiptStore(_encryptedReceipts);
            var scheduler = gameObject.AddComponent<UnityAdScheduler>();
            _provider = new SimulatorProvider(scheduler) { Interactive = true };
            var config = new AdsConfig(new[] { new AdPlacement("reward", AdFormat.Rewarded) }, AdPolicy.Default,
                new ProviderConfig("probe", null, true, null, false, false, false, "G"), BuildKind.Development);
            _controller = new AdController(config, _provider, new UnityAdClock(),
                gameObject.AddComponent<UnityAppVisibility>(), new UnityMainThread(), scheduler, _receipts,
                new Telemetry(this), _provider);
            _adapter = new NexusAdsAdapter(_controller);
            _adapter.Initialize(null);
            Check(_controller.State == AdController.Phase.Running && !_telemetryFailed, "controller-running");
        }

        private void Arm()
        {
            Check(((IPlayerPrefsService)_wallet).TryGetString("wallet", out _) == StorageReadStatus.Absent
                && !_controller.PendingReward.HasValue, "empty-fixture-state");
            _wallet.SetString("wallet", JsonUtility.ToJson(new Wallet { Balance = 100 }));
            _wallet.SaveOrThrow();
            Check(ReadWallet().Balance == 100, "baseline-confirmed");
            Log("BASELINE", "committedBalance=100 pending=0");
            string path = null, baseline = null;
            if (_scenario == "wallet")
            {
                path = WalletPath();
                baseline = Convert.ToBase64String(File.ReadAllBytes(path));
                Check(!File.Exists(path + ".tmp") && !Directory.Exists(path + ".tmp"), "temp-path-absent");
                Directory.CreateDirectory(path + ".tmp");
                Check(Directory.Exists(path + ".tmp"), "wallet-collision-created");
            }
            else _receipts.FailAcknowledgement = true;

            string deliveredId = null;
            Action grant = () =>
            {
                _callbacks++;
                Check(_callbacks == 1, "single-reward-callback");
                var receipt = _controller.DeliveringReward;
                Check(receipt.HasValue && receipt.Value.Placement == "reward"
                    && Regex.IsMatch(receipt.Value.Id, @"\A[A-Za-z0-9-]{1,80}\z"), "delivering-receipt-readable");
                deliveredId = receipt.Value.Id;
                try { Grant(receipt.Value); }
                catch (IOException ex) { _expectedSaveFailure = ex; throw; }
            };
            if (_variant == "outcome")
                _adapter.RequestReward("reward", result => { Check(result == AdOutcome.Rewarded, "rewarded-outcome"); grant(); });
            else
                _adapter.ShowRewarded("reward", result => { Check(result, "rewarded-bool"); grant(); });
            Check(_provider.IsShowing, "simulator-show-open");
            Exception failure = null;
            try { Check(_provider.CompleteShow(AdResult.Rewarded), "simulator-completed"); }
            catch (IOException ex) { failure = ex; }
            Check(_callbacks == 1 && failure != null, "delivery-failed-observably");
            Check(!_provider.IsShowing, "simulator-completed-with-failure");
            Check(_scenario == "wallet"
                ? ReferenceEquals(failure, _expectedSaveFailure) && !_committed
                : ReferenceEquals(failure, _receipts.ExpectedFailure) && _committed, "expected-fault-origin");
            var pending = Pending();
            Check(pending.Id == deliveredId && !_controller.DeliveringReward.HasValue, "retained-delivered-receipt");
            Check(ReadWallet().Balance == 110 && ReadWallet().LastReceiptId == pending.Id, "cached-grant-with-id");
            Check(_receipts.Acknowledgements == 0 && !_telemetryFailed, "receipt-not-acknowledged");
            if (_scenario == "wallet")
                Check(Directory.Exists(path + ".tmp") && baseline == Convert.ToBase64String(File.ReadAllBytes(path)),
                    "baseline-file-unchanged");
            else Check(_receipts.FailedAcknowledgements == 1, "ack-interrupted-before-write");
            Log("EXPECTED_FAILURE", "stage=" + _scenario + " type=" + failure.GetType().Name);
            Finish("ARMED", _scenario == "wallet" ? 100 : 110, pending.Id, true);
            // No Dispose or unblock here, or in OnDestroy: the failed instance stays dirty and blocked until kill.
        }

        private void Recover()
        {
            var receipt = Pending();
            Check(receipt.Id == _expectedReceipt, "same-pending-receipt");
            var wallet = ReadWallet();
            Check(wallet.Balance == (_scenario == "wallet" ? 100 : 110), "reopened-balance");
            Check(_scenario == "wallet" ? string.IsNullOrEmpty(wallet.LastReceiptId)
                : wallet.LastReceiptId == receipt.Id, "reopened-receipt-id");
            Log("REOPENED", "committedBalance=" + wallet.Balance + " receipt=" + receipt.Id + " pending=1");
            if (_scenario == "wallet")
            {
                string temp = WalletPath() + ".tmp";
                Check(Directory.Exists(temp) && Directory.GetFileSystemEntries(temp).Length == 0, "named-empty-collision");
                Directory.Delete(temp); // non-recursive: only this run's known wallet fault, never another save.
            }
            Grant(receipt);
            Check(ReadWallet().Balance == 110 && ReadWallet().LastReceiptId == receipt.Id, "grant-or-dedup-confirmed");
            _controller.AcknowledgeReward(receipt.Id);
            _controller.AcknowledgeReward(receipt.Id);
            Check(!_controller.PendingReward.HasValue && _receipts.Acknowledgements == 1, "acknowledged-once");
            Finish("RECOVERED", 110, receipt.Id, false);
        }

        private void Verify()
        {
            var wallet = ReadWallet();
            Check(wallet.Balance == 110 && wallet.LastReceiptId == _expectedReceipt, "final-wallet-with-id");
            Check(!_controller.PendingReward.HasValue && _receipts.Acknowledgements == 0, "final-no-receipt-or-ack");
            Finish("VERIFIED", 110, wallet.LastReceiptId, false);
            // Fixtures intentionally retained as evidence. No whole-store reset or automatic cleanup.
        }

        private Wallet ReadWallet()
        {
            Check(((IPlayerPrefsService)_wallet).TryGetString("wallet", out string blob) == StorageReadStatus.Present,
                "wallet-readable");
            var wallet = JsonUtility.FromJson<Wallet>(blob);
            Check(wallet != null, "wallet-json");
            return wallet;
        }

        private RewardReceipt Pending()
        {
            var pending = _controller.PendingReward;
            Check(pending.HasValue && pending.Value.Placement == "reward"
                && Regex.IsMatch(pending.Value.Id, @"\A[A-Za-z0-9-]{1,80}\z"), "pending-receipt-readable");
            return pending.Value;
        }

        private void Grant(RewardReceipt receipt)
        {
            var wallet = ReadWallet();
            if (wallet.LastReceiptId != receipt.Id)
            {
                wallet.Balance += 10;
                wallet.LastReceiptId = receipt.Id;
                _wallet.SetString("wallet", JsonUtility.ToJson(wallet));
            }
            _wallet.SaveOrThrow(); // balance AND id in one atomic JSON blob, before callback return or explicit ack.
            _committed = true;
        }

        private string WalletPath()
        {
            // Reflection is fault-injection path discovery only, as in DurableRewardDeliveryTests.
            var method = typeof(EncryptedStorageService).GetMethod("GetFilePath", BindingFlags.Instance | BindingFlags.NonPublic);
            Check(method != null, "fault-path-method");
            string path = Path.GetFullPath((string)method.Invoke(_wallet, new object[] { "wallet" }));
            string folder = Path.GetFullPath(Path.Combine(Application.persistentDataPath, "SecureData_" + _walletSalt));
            Check(Path.GetDirectoryName(path) == folder && Path.GetExtension(path) == ".dat", "fault-path-contained");
            return path;
        }

        private void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException(name);
            _checks++;
            Log("CHECK", "name=" + name + " index=" + _checks);
        }

        private void Finish(string marker, int balance, string receipt, bool pending)
        {
            Check(!_telemetryFailed && _checks > 0, "phase-complete");
            Log(marker, "checks=" + _checks + " committedBalance=" + balance + " receipt=" + receipt
                + " pending=" + (pending ? 1 : 0) + " acks=" + _receipts.Acknowledgements);
        }

        private void Log(string marker, string detail)
            => Debug.Log("[PersistenceProbe] run=" + Clean(_run) + " variant=" + Clean(_variant)
                + " scenario=" + Clean(_scenario) + " phase=" + Clean(_phase) + " pid=" + _pid
                + " event=" + marker + " " + detail);

        private static string Clean(string value) => (value ?? "none").Replace("\r", " ").Replace("\n", " ");

        private sealed class ReceiptStore : IPacingStore
        {
            private readonly IPlayerPrefsService _store;
            public bool FailAcknowledgement;
            public int Acknowledgements, FailedAcknowledgements;
            public IOException ExpectedFailure;
            public ReceiptStore(IPlayerPrefsService store) => _store = store;
            public string Get(string key)
            {
                var status = _store.TryGetString(key, out string value);
                if (status == StorageReadStatus.Unreadable) throw new IOException("receipt store is unreadable");
                return status == StorageReadStatus.Absent ? null : value;
            }
            public void Set(string key, string value)
            {
                if (value == "" && FailAcknowledgement)
                {
                    FailedAcknowledgements++;
                    ExpectedFailure = new IOException("receipt acknowledgement interrupted before write");
                    throw ExpectedFailure;
                }
                _store.SetString(key, value);
                _store.SaveOrThrow();
                if (value == "") Acknowledgements++;
            }
        }

        private sealed class Telemetry : IAdTelemetry
        {
            private readonly PersistenceProbe _probe;
            public Telemetry(PersistenceProbe probe) => _probe = probe;
            public void Record(in AdEvent e)
            {
                if (e.Kind != AdEventKind.HandlerFailed && e.Kind != AdEventKind.InitFailed
                    && e.Kind != AdEventKind.ShowFailed && e.Kind != AdEventKind.Violation) return;
                _probe._telemetryFailed = true;
                _probe.Log("FAILED", "reason=ad-" + e.Kind + ":" + Clean(e.Detail));
            }
        }
    }
}
#endif

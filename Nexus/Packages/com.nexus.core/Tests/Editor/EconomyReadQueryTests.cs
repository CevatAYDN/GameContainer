using System;
using System.IO;
using System.Runtime.ExceptionServices;
using Nexus.Core;
using Nexus.Core.Services;
#if UNITY_EDITOR
using NUnit.Framework;
#endif

namespace Nexus.Editor.Tests
{
    public sealed class EconomyReadQueryTests
    {
#if UNITY_EDITOR
        [Test]
#endif
        public void UnreadableQuery_DoesNotUseExceptionControlFlow_AndCanRetry()
        {
            var store = new InMemoryPlayerPrefsService();
            store.SetLong("NT_Eco_coin", 500);
            store.SaveOrThrow();
            store.MarkUnreadable("NT_Eco_coin");
            using var service = new EconomyService { PlayerPrefsService = store };
            IEconomyService economy = service;
            int exceptions = 0;
            void OnException(object sender, FirstChanceExceptionEventArgs args)
            {
                if (args.Exception is IOException) exceptions++;
            }
            AppDomain.CurrentDomain.FirstChanceException += OnException;
            try
            {
                for (int i = 0; i < 10; i++)
                {
                    Require(!economy.TryGetBalance("coin", out _), "unreadable long balance must be unavailable");
                    Require(!economy.TryGetBigBalance("coin", out _), "unreadable promoted balance must be unavailable");
                }
            }
            finally { AppDomain.CurrentDomain.FirstChanceException -= OnException; }
            Require(exceptions == 0, $"queries must inspect read status, not throw/catch IOException every frame: {exceptions}");
            Require(store.Reopen().TryGetString("NT_Eco_coin", out _) == StorageReadStatus.Unreadable,
                "queries must not replace the durable unreadable value");
            store.Restore("NT_Eco_coin");
            Require(economy.TryGetBalance("coin", out long value) && value == 500,
                "retry after recovery must load the original balance, not a cached fallback");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void UnavailableBalance_ViewBindsOnce_ThenTracksRecoveryWithoutSceneReload()
        {
            var store = new InMemoryPlayerPrefsService();
            store.SetLong("NT_Eco_coin", 500);
            store.SaveOrThrow();
            store.MarkUnreadable("NT_Eco_coin");
            using var economy = new EconomyService { PlayerPrefsService = store };
            using var context = NexusTestHarness.CreateContext(builder => builder.BindInstance<IEconomyService>(economy));
            var view = new QueryView();
            context.Context.RegisterView(view);
            Require(ReferenceEquals(view.Context, context.Context) && view.Controller != null
                && view.Binds == 1 && !view.Amount.HasValue, "an unavailable wallet must not leave the view unbound");
            store.Restore("NT_Eco_coin");
            view.Controller.Refresh();
            Require(view.Amount == 500 && view.Binds == 1, "the same bound view must recover without a scene reload");
            economy.Earn("coin", 25);
            Require(view.Amount == 525 && view.Changes == 1, "the recovered observable must be tracked once");
            view.Controller.Refresh();
            economy.Earn("coin", 5);
            Require(view.Amount == 530 && view.Changes == 2, "repeated refresh must not duplicate subscriptions");
            context.Context.UnregisterView(view);
            economy.Earn("coin", 5);
            Require(view.Context == null && view.Amount == 530 && view.Changes == 2, "unbind must release the recovered subscription");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void ReadQueries_DistinguishAbsentInvalidDisposedAndMalformedCanonicalValues()
        {
            var store = new InMemoryPlayerPrefsService();
            using var economy = new EconomyService { PlayerPrefsService = store };
            Require(economy.TryGetBalance("absent", out long absent) && absent == 0
                && economy.TryGetBigBalance("missingBig", out var missingBig) && missingBig == BigDouble.Zero,
                "a missing currency is a valid zero, not unreadable");
            Require(!economy.TryGetBalance(null, out _) && !economy.TryGetBigBalance("", out _), "invalid ids must be unavailable");
            foreach (var key in new[] { "NT_Eco_bad", "NT_EcoBig_badBig", "NT_EcoWallet_badWallet" })
                store.SetString(key, "malformed");
            foreach (var currency in new[] { "bad", "badBig", "badWallet" })
            {
                Require(!economy.TryGetBalance(currency, out _) && !economy.TryGetBigBalance(currency, out _),
                    "malformed authoritative data must report unavailable without falling back");
                bool mutationThrew = false;
                try { economy.Earn(currency, 1); }
                catch (IOException) { mutationThrew = true; }
                Require(mutationThrew, "nonthrowing queries must not relax the mutation guard");
            }
            economy.Dispose();
            Require(!economy.TryGetBalance("absent", out _) && !economy.TryGetBigBalance("missingBig", out _),
                "disposed query must not report a valid zero");
            Require(store.GetString("NT_Eco_bad") == "malformed"
                && store.GetString("NT_EcoBig_badBig") == "malformed" && store.GetString("NT_EcoWallet_badWallet") == "malformed",
                "failed queries and disposal must preserve malformed values");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void LoadedReadQueries_DoNotAllocate_AndRespectCanonicalBigBalance()
        {
            var store = new InMemoryPlayerPrefsService();
            using var economy = new EconomyService { PlayerPrefsService = store };
            economy.SetBigBalance("coin", new BigDouble(1.25, 30));
            economy.GetBalance("coin");
            for (int i = 0; i < 100; i++)
            {
                economy.TryGetBalance("coin", out _);
                economy.TryGetBigBalance("coin", out _);
            }
            long before = GC.GetAllocatedBytesForCurrentThread();
            bool ok = true;
            for (int i = 0; i < 1000; i++)
                ok &= economy.TryGetBalance("coin", out long projected) && projected == long.MaxValue
                    && economy.TryGetBigBalance("coin", out var big) && big == new BigDouble(1.25, 30);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Require(ok && allocated == 0, $"loaded long/big read queries must allocate zero bytes, got {allocated}");
        }

        [Mediator(typeof(QueryMediator))]
        public sealed class QueryView : IView
        {
            public IContext Context { get; private set; }
            public QueryMediator Controller;
            public long? Amount;
            public int Binds, Changes;
            public void Bind(IContext context) { Context = context; Binds++; }
            public void Unbind() => Context = null;
        }

        public sealed class QueryMediator : Mediator<QueryView>
        {
            [Inject] public IEconomyService Economy { get; set; }
            private bool _tracking;
            protected override void OnBind() { View.Controller = this; Refresh(); }
            public void Refresh()
            {
                if (_tracking) return;
                if (!Economy.TryGetBalance("coin", out var balance)) { View.Amount = null; return; }
                View.Amount = balance;
                TrackObservable(Economy.GetObservableBalance("coin"), (_, value) => { View.Amount = value; View.Changes++; });
                _tracking = true;
            }
            protected override void OnReset() => _tracking = false;
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}

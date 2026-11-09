using System;
using System.IO;
using Nexus.Core;
using Nexus.Core.Services;
#if UNITY_EDITOR
using NUnit.Framework;
#endif

namespace Nexus.Editor.Tests
{
    public sealed class InMemoryPersistenceTests
    {
#if UNITY_EDITOR
        [Test]
#endif
        public void ConfirmedSave_CanBeUsedByARewardCallback()
        {
            IPlayerPrefsService store = new InMemoryPlayerPrefsService();
            store.SetLong("coins", 140);
            Require(store.TrySave(), "the shipped test store must support confirmed saves");
            store.SaveOrThrow();
            Require(store.GetLong("coins") == 140, "confirmation must preserve the value");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void Reopen_DropsUncommittedWritesAndDeletes_WithoutSharingLiveState()
        {
            var store = new InMemoryPlayerPrefsService();
            store.SetLong("coins", 100);
            store.SetString("delete", "original");
            store.SetString("tombstone", "original");
            store.SaveOrThrow();
            store.SetLong("coins", 140);
            store.SetBool("new", true);
            store.DeleteKey("delete");
            store.SetString("tombstone", null);
            var killed = store.Reopen();
            Require(killed.GetLong("coins") == 100 && !killed.HasKey("new")
                && killed.GetString("delete") == "original" && killed.GetString("tombstone") == "original",
                "reopen must expose the last commit, not live writes or deletions");
            store.SaveOrThrow();
            var committed = store.Reopen();
            Require(committed.GetLong("coins") == 140 && committed.GetBool("new")
                && !committed.HasKey("delete") && !committed.HasKey("tombstone"),
                "confirmed writes and deletions must survive reopen");
            committed.SetLong("coins", 3);
            committed.SaveOrThrow();
            Require(store.GetLong("coins") == 140 && store.Reopen().GetLong("coins") == 140,
                "the reopened session must not share mutable live or snapshot dictionaries");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void FailedSave_DoesNotCommit_AndSaveOrThrowRetainsChangesForRetry()
        {
            var store = new InMemoryPlayerPrefsService();
            store.SetLong("coins", 100);
            store.SetString("receipt", "old");
            store.Save();
            store.SetLong("coins", 140);
            store.DeleteKey("receipt");
            store.FailNextSaves(3);
            Require(!store.TrySave(), "the first injected failure must not confirm");
            bool threw = false;
            try { store.SaveOrThrow(); }
            catch (IOException) { threw = true; }
            store.Save();
            Require(threw && store.Saves == 2 && store.GetLong("coins") == 140 && !store.HasKey("receipt"),
                "void Save counts calls; failures keep the live mutations for retry");
            var killed = store.Reopen();
            Require(killed.GetLong("coins") == 100 && killed.GetString("receipt") == "old",
                "failed attempts must not commit writes or deletes");
            store.SaveOrThrow();
            Require(store.Reopen().GetLong("coins") == 140 && !store.Reopen().HasKey("receipt"),
                "the next successful attempt must commit every pending change");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void UnreadableKeys_SurviveReopen_AndRestoreWithoutCommittingPendingWrites()
        {
            var store = new InMemoryPlayerPrefsService();
            store.SetLong("coins", 100);
            store.SetInt("int", 7);
            store.SetFloat("float", 1.5f);
            store.SetBool("bool", true);
            store.SaveOrThrow();
            foreach (var key in new[] { "coins", "int", "float", "bool" }) store.MarkUnreadable(key);
            Require(store.TryGetString("coins", out var value) == StorageReadStatus.Unreadable && value == null
                && store.HasKey("coins") && store.GetLong("coins", -1) == -1
                && store.GetInt("int", -1) == -1 && store.GetFloat("float", -1) == -1 && !store.GetBool("bool"),
                "unreadable typed keys remain present but return defaults");
            Require(store.Reopen().TryGetString("coins", out _) == StorageReadStatus.Unreadable,
                "a restart must not cure corrupt/inaccessible data");
            store.SetInt("pending", 9);
            store.Restore("coins");
            Require(store.GetLong("coins") == 100 && store.Reopen().GetLong("coins") == 100
                && !store.Reopen().HasKey("pending"), "restore must recover the value without saving unrelated writes");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void WritingOrDeletingUnreadableKeys_ClearsOnlyTheLiveFaultUntilSave()
        {
            var store = new InMemoryPlayerPrefsService();
            store.SetLong("coins", 100);
            store.SetString("delete", "data");
            store.SaveOrThrow();
            store.MarkUnreadable("coins");
            store.MarkUnreadable("delete");
            store.SetLong("coins", 150);
            store.DeleteKey("delete");
            Require(store.TryGetString("coins", out _) == StorageReadStatus.Present && store.GetLong("coins") == 150
                && store.TryGetString("delete", out _) == StorageReadStatus.Absent && !store.HasKey("delete"),
                "writes/deletes clear the live unreadable mark");
            Require(store.Reopen().TryGetString("coins", out _) == StorageReadStatus.Unreadable
                && store.Reopen().TryGetString("delete", out _) == StorageReadStatus.Unreadable,
                "uncommitted recovery must not change the durable state");
            store.SaveOrThrow();
            Require(store.Reopen().GetLong("coins") == 150 && !store.Reopen().HasKey("delete"),
                "a confirmed recovery must survive reopening");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void UnreadableWallet_BlocksEconomy_ThenRetriesOnTheSameService()
        {
            var store = new InMemoryPlayerPrefsService();
            store.SetLong("NT_Eco_coin", 500);
            store.SaveOrThrow();
            store.MarkUnreadable("NT_Eco_coin");
            using var economy = new EconomyService { PlayerPrefsService = store };
            bool threw = false;
            try { economy.Earn("coin", 40); }
            catch (IOException) { threw = true; }
            Require(threw, "the shipped fake must exercise the real #339 unreadable-wallet guard");
            store.Restore("NT_Eco_coin");
            Require(economy.GetBalance("coin") == 500, "failed load must not cache zero");
            economy.Earn("coin", 40);
            Require(store.Reopen().GetLong("NT_Eco_coin") == 540, "recovery must permit a real durable earn");
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}

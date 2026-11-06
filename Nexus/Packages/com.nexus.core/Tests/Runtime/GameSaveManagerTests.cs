using NUnit.Framework;
using Nexus.Core;
using Nexus.Core.Extensions;
using Nexus.Core.Services;
using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.TestTools;

namespace Nexus.Tests
{
    [TestFixture]
    public class GameSaveManagerTests
    {
        private class TestSaveModel : ISaveDataProvider
        {
            public int BindCount;
            public byte[] CaptureSaveData() => System.Text.Encoding.UTF8.GetBytes("test");
            public void RestoreSaveData(byte[] data) => BindCount++;
        }

        private sealed class TextModel : ISaveDataProvider
        {
            public string Text;
            public byte[] CaptureSaveData() => System.Text.Encoding.UTF8.GetBytes(Text);
            public void RestoreSaveData(byte[] data) => Text = System.Text.Encoding.UTF8.GetString(data);
        }

        private static string SlotFile(string slot) => Path.Combine(Application.persistentDataPath, "saves", slot + ".sav");

        [Test]
        public async Task SaveAndLoad_FindsTheSlotAndRestoresTheRegisteredModelOnce()
        {
            var manager = new GameSaveManager();
            var model = new TestSaveModel();

            manager.RegisterModel(model);
            await manager.SaveAsync("slotA", CancellationToken.None);

            Assert.IsTrue(manager.SaveExists("slotA"));
            bool loaded = await manager.LoadAsync("slotA", CancellationToken.None);
            Assert.IsTrue(loaded);
            Assert.AreEqual(1, model.BindCount);
            manager.DeleteSave("slotA");
            manager.Dispose();
        }

        // Issue #23 (O1): SaveAsync marshals the model capture to the main thread for worker callers, but read
        // Application.persistentDataPath and Application.version on the worker.
        [Test]
        public async Task GameSaveManager_SaveFromWorkerThread_WritesTheSave()
        {
            var manager = new GameSaveManager();
            var model = new TestSaveModel();
            manager.RegisterModel(model);

            await Task.Run(() => manager.SaveAsync("workerSlot", CancellationToken.None));

            Assert.IsTrue(manager.SaveExists("workerSlot"));
            manager.DeleteSave("workerSlot");
            manager.Dispose();
        }

        // Issue #119: saves of one slot captured their snapshots in call order, but their writes took the lock in any order,
        // so an older snapshot could be written last
        [Test]
        public async Task SavesOfOneSlot_LandInCallOrder_WhateverOrderTheirWritesRun()
        {
            var manager = new GameSaveManager();
            var model = new TextModel();
            manager.RegisterModel(model);
            string large = new string('x', 1_000_000);
            int misordered = 0;
            try
            {
                for (int round = 0; round < 3; round++)
                {
                    // the older snapshot is large, so its write serializes longer before it reaches the lock than the newer one's
                    model.Text = large;
                    var older = manager.SaveAsync("orderSlot");
                    model.Text = "newest " + round;
                    var newer = manager.SaveAsync("orderSlot");
                    await Task.WhenAll(older, newer);
                    model.Text = null;
                    await manager.LoadAsync("orderSlot");
                    if (model.Text != "newest " + round) misordered++;
                }
                Assert.AreEqual(0, misordered, "rounds whose last save was not the one on disk");
            }
            finally
            {
                manager.DeleteSave("orderSlot");
                manager.Dispose();
            }
        }

        // Issue #119: where File.Replace is missing the save deletes the old file and then moves the staged one in; a crash
        // between the two left only the staged file, which the load never read, and the game saw no save at all
        [Test]
        public async Task ASaveStoppedBetweenItsDeleteAndItsMove_IsFoundAndLoadedFromTheStagedFile()
        {
            var manager = new GameSaveManager();
            var model = new TextModel { Text = "level 12" };
            manager.RegisterModel(model);
            string file = SlotFile("stagedSlot");
            try
            {
                await manager.SaveAsync("stagedSlot");
                File.Move(file, file + ".tmp"); // the old file deleted, the staged one not moved in yet

                Assert.IsTrue(manager.SaveExists("stagedSlot"), "the save is there, in the staged file");
                model.Text = null;
                LogAssert.Expect(LogType.Warning, new Regex(@"\[GameSaveManager\] Slot 'stagedSlot' was restored from its staged file"));
                Assert.IsTrue(await manager.LoadAsync("stagedSlot"));
                Assert.AreEqual("level 12", model.Text);
                Assert.IsTrue(File.Exists(file) && !File.Exists(file + ".tmp"), "the staged file became the save file");

                File.WriteAllText(file + ".tmp", "{}"); // a staged file a failed save left behind
                manager.DeleteSave("stagedSlot");
                Assert.IsFalse(manager.SaveExists("stagedSlot"), "a deleted slot is not found again through its staged file");
            }
            finally
            {
                manager.DeleteSave("stagedSlot");
                manager.Dispose();
            }
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase(".")]
        [TestCase("..")]
        public void GameSaveManager_InvalidSlotNames_AreRejected(string slotName)
        {
            var manager = new GameSaveManager();

            Assert.Throws<ArgumentException>(() => manager.SaveExists(slotName));
            Assert.Throws<ArgumentException>(() => manager.DeleteSave(slotName));
            manager.Dispose();
        }
    }
}

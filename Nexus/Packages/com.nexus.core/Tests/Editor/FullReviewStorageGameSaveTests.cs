using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Core.Extensions;
#if UNITY_EDITOR
using NUnit.Framework;
#endif

namespace Nexus.Editor.Tests
{
    public sealed class FullReviewStorageGameSaveTests
    {
#if UNITY_EDITOR
        [Test]
#endif
        public async Task ST04_NestedSaveDuringCapture_PreservesTheNewerSnapshot()
        {
            using var files = new Files();
            using var manager = new GameSaveManager();
            var model = new Model { Value = "old" };
            manager.RegisterModel(model);
            Task nested = null;
            model.DuringCapture = () =>
            {
                model.Value = "new";
                nested = manager.SaveAsync("slot");
            };
            Task outer = manager.SaveAsync("slot");
            await Task.WhenAll(outer, nested);
            model.Value = "not loaded";
            bool loaded = await manager.LoadAsync("slot");
            Require(loaded && model.Value == "new",
                $"The nested newer save must survive its outer capture: loaded={loaded}, restored={model.Value}; expected new.");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public async Task ST04_DeleteDuringCapture_DoesNotResurrectTheSlot()
        {
            using var files = new Files();
            using var manager = new GameSaveManager();
            var model = new Model { Value = "seed" };
            manager.RegisterModel(model);
            await manager.SaveAsync("slot");
            model.Value = "old";
            model.DuringCapture = () => manager.DeleteSave("slot");
            await manager.SaveAsync("slot");
            bool exists = manager.SaveExists("slot");
            model.Value = "not loaded";
            bool loaded = await manager.LoadAsync("slot");
            Require(!exists && !loaded && model.Value == "not loaded",
                $"A delete accepted during capture must win: exists={exists}, loaded={loaded}, restored={model.Value}.");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public async Task ST04_DeleteBeforePostedCapture_DoesNotResurrectTheSlot()
        {
            using var files = new Files();
            var context = new QueuedContext();
            var previous = SynchronizationContext.Current;
            GameSaveManager manager;
            SynchronizationContext.SetSynchronizationContext(context);
            try { manager = new GameSaveManager(); }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
            using (manager)
            using (var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            {
                manager.RegisterModel(new Model { Value = "old" });
                Task save = Task.Run(() => manager.SaveAsync("slot", cancellation.Token));
                try
                {
                    await Task.WhenAny(context.Posted, save);
                    Require(context.Posted.IsCompleted, "The worker must post its capture before the delete.");
                    manager.DeleteSave("slot");
                    context.RunPosted();
                    await save;
                    Require(!manager.SaveExists("slot"),
                        "A later delete must invalidate a save still waiting for its main-thread capture.");
                }
                finally
                {
                    cancellation.Cancel();
                    try { await save; }
                    catch (OperationCanceledException) { }
                }
            }
        }

#if UNITY_EDITOR
        [Test]
#endif
        public async Task ST04_NestedSaveAfterCaptureTimeDelete_CanCreateANewSlot()
        {
            using var files = new Files();
            using var manager = new GameSaveManager();
            var model = new Model { Value = "old" };
            manager.RegisterModel(model);
            Task nested = null;
            model.DuringCapture = () =>
            {
                manager.DeleteSave("slot");
                model.Value = "new";
                nested = manager.SaveAsync("slot");
            };
            Task outer = manager.SaveAsync("slot");
            await Task.WhenAll(outer, nested);
            model.Value = "not loaded";
            Require(await manager.LoadAsync("slot") && model.Value == "new",
                "A save accepted after a capture-time delete must still create the newer slot.");
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private sealed class Model : ISaveDataProvider
        {
            public string Value;
            public Action DuringCapture;

            public byte[] CaptureSaveData()
            {
                byte[] captured = Encoding.UTF8.GetBytes(Value);
                var callback = DuringCapture;
                DuringCapture = null;
                callback?.Invoke();
                return captured;
            }

            public void RestoreSaveData(byte[] data) => Value = Encoding.UTF8.GetString(data);
        }

        private sealed class QueuedContext : SynchronizationContext
        {
            private readonly TaskCompletionSource<bool> _posted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private Action _callback;
            public Task Posted => _posted.Task;

            public override void Post(SendOrPostCallback callback, object state)
            {
                _callback = () => callback(state);
                _posted.TrySetResult(true);
            }

            public void RunPosted()
            {
                var previous = Current;
                SetSynchronizationContext(this);
                try { _callback(); }
                finally { SetSynchronizationContext(previous); }
            }
        }

        private sealed class Files : IDisposable
        {
            private readonly string _name = "fullreview_gamesave_" + Guid.NewGuid().ToString("N");
            private readonly string _folder;
            private readonly string _previous;

            public Files()
            {
                _folder = Path.GetFullPath(Path.Combine(Path.GetTempPath(), _name));
                CheckFolder();
                Directory.CreateDirectory(_folder);
                _previous = GameSaveManager.TestSaveDirectory;
                GameSaveManager.TestSaveDirectory = _folder;
            }

            private void CheckFolder()
            {
                string root = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
                Require(string.Equals(Path.GetDirectoryName(_folder), root, StringComparison.OrdinalIgnoreCase)
                    && Path.GetFileName(_folder) == _name,
                    "Game-save file operations must stay inside the unique fixture.");
            }

            public void Dispose()
            {
                GameSaveManager.TestSaveDirectory = _previous;
                CheckFolder();
                if (Directory.Exists(_folder)) Directory.Delete(_folder, true);
            }
        }
    }
}

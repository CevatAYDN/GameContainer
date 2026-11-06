using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nexus.Core;
using Nexus.Core.Services;
using NUnit.Framework;

namespace Nexus.Tests
{
    [TestFixture]
    public class TickServiceStressTests
    {
        private class ConcurrentTickable : ITickable
        {
            public int TickCount;
            public void Tick(float deltaTime)
            {
                // tiny work
                TickCount++;
            }
        }

        [Test]
        public async Task TickService_SurvivesMassRegisterUnregister_AndDispatch()
        {
            var service = new TickService();
            const int tasks = 50;
            const int perTask = 200;
            var allTickables = new List<ConcurrentTickable>(tasks * perTask);
            var registerTasks = new List<Task>(tasks);

            for (int t = 0; t < tasks; t++)
            {
                registerTasks.Add(Task.Run(() =>
                {
                    for (int i = 0; i < perTask; i++)
                    {
                        var tickable = new ConcurrentTickable();
                        lock (allTickables) { allTickables.Add(tickable); }
                        service.RegisterTickable(tickable);
                    }
                }));
            }

            await Task.WhenAll(registerTasks);

            // every registration from the worker tasks must tick once per frame: a lost one stays at 0, a doubled one passes 10
            for (int f = 0; f < 10; f++)
            {
                service.OnTick(0.016f);
            }
            lock (allTickables)
            {
                Assert.AreEqual(tasks * perTask, allTickables.Count);
                foreach (var tickable in allTickables) Assert.AreEqual(10, tickable.TickCount, "each registered tickable ticks once per frame");
            }

            // concurrently unregister half
            var removed = new List<ConcurrentTickable>(tasks * perTask / 2);
            var unregisterTasks = new List<Task>(tasks);
            for (int t = 0; t < tasks; t++)
            {
                unregisterTasks.Add(Task.Run(() =>
                {
                    for (int i = 0; i < perTask / 2; i++)
                    {
                        ConcurrentTickable toRemove = null;
                        lock (allTickables)
                        {
                            if (allTickables.Count > 0)
                            {
                                toRemove = allTickables[allTickables.Count - 1];
                                allTickables.RemoveAt(allTickables.Count - 1);
                                removed.Add(toRemove);
                            }
                        }
                        if (toRemove != null) service.UnregisterTickable(toRemove);
                    }
                }));
            }

            await Task.WhenAll(unregisterTasks);

            for (int f = 0; f < 5; f++) service.OnTick(0.016f);

            lock (allTickables)
            {
                Assert.AreEqual(tasks * perTask / 2, removed.Count);
                foreach (var tickable in removed) Assert.AreEqual(10, tickable.TickCount, "an unregistered tickable stops ticking");
                foreach (var tickable in allTickables) Assert.AreEqual(15, tickable.TickCount, "a surviving tickable keeps ticking");
            }
            service.Dispose();
        }
    }
}

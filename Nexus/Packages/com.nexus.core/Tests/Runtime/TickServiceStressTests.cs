using System;
using System.Collections.Generic;
using System.Threading.Tasks;
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

            // dispatch several frames
            for (int f = 0; f < 10; f++)
            {
                service.OnTick(0.016f);
            }

            // concurrently unregister half
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
                            }
                        }
                        if (toRemove != null) service.UnregisterTickable(toRemove);
                    }
                }));
            }

            await Task.WhenAll(unregisterTasks);

            // dispatch additional frames and ensure no exceptions and some tick calls occurred
            for (int f = 0; f < 5; f++) service.OnTick(0.016f);

            int totalTicks = 0;
            lock (allTickables)
            {
                foreach (var tt in allTickables) totalTicks += tt.TickCount;
            }

            Assert.Greater(totalTicks, 0, "Some tickables should have been ticked at least once");
            service.Dispose();
        }
    }
}

using System;
using System.Collections.Generic;
using Nexus.Core;
using Nexus.Netcode;
#if UNITY_EDITOR
using NUnit.Framework;
#endif

namespace Nexus.Editor.Tests
{
    public sealed class NetcodeReplayRegressionTests
    {
        private readonly struct Add : INetworkSignal
        {
            public readonly int Amount;
            public Add(int amount) => Amount = amount;
        }

        private readonly struct Set : INetworkSignal
        {
            public readonly int Value;
            public Set(int value) => Value = value;
        }

        private readonly struct Derive : INetworkSignal { }
        private readonly struct Schedule : INetworkSignal { }
        private readonly struct ScriptInput : INetworkSignal
        {
            public readonly int Kind, Amount;
            public ScriptInput(int kind, int amount) { Kind = kind; Amount = amount; }
        }

        private sealed class Model : ISnapshotableModel<int>
        {
            public int Value = 1;
            public int CaptureSnapshot() => Value;
            public void RestoreSnapshot(int value) => Value = value;
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void Rollback_RecomputesNestedPayload_AfterLateInput()
        {
            foreach (bool atTick in new[] { false, true })
            {
                using var context = new Context();
                var model = new Model();
                var network = Bind(context, model, atTick);
                network.SetTick(0);
                network.SetTick(1);
                network.Fire(new Derive());
                Require(model.Value == 10, "the original live input must produce 10");
                network.FireAtTick(new Add(2), 0);

                using var referenceContext = new Context();
                var reference = new Model();
                var referenceNetwork = Bind(referenceContext, reference, atTick);
                referenceNetwork.SetTick(0);
                referenceNetwork.Fire(new Add(2));
                referenceNetwork.SetTick(1);
                referenceNetwork.Fire(new Derive());
                Require(reference.Value == 30, "the chronological reference must produce 30");

                for (int repeat = 0; repeat < 3; repeat++)
                {
                    network.RollbackAndResimulate(0, 1);
                    Require(model.Value == reference.Value,
                        $"late input replay produced {model.Value}, chronological reference {reference.Value}; FireAtTick={atTick}");
                    var history = (NetworkSignalHistory<Set>)network.Histories[typeof(Set)];
                    Require(history.Signals.Count == 1 && history.Signals[0].Signal.Value == 30,
                        "the derived history must contain exactly the corrected payload, without accumulated children");
                }
            }
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void Rollback_RebuildsChangedBranch_AndDropsOldDescendants()
        {
            using var context = new Context();
            var model = new Model();
            var network = new NetworkSignalBus(context.SignalBus);
            network.RegisterModel(model);
            context.SignalBus.Subscribe<Add>(signal => model.Value += signal.Amount);
            context.SignalBus.Subscribe<Set>(signal =>
            {
                model.Value = signal.Value;
                if (signal.Value == 10) network.Fire(new Add(5));
            });
            context.SignalBus.Subscribe<Derive>(_ =>
            {
                if (model.Value < 3)
                {
                    network.Fire(new Set(10));
                    network.Fire(new Set(11));
                }
                else network.Fire(new Add(100));
            });
            network.SetTick(0);
            network.SetTick(1);
            network.Fire(new Derive());
            Require(model.Value == 11, "the old branch must execute before correction");
            network.FireAtTick(new Add(2), 0);
            for (int repeat = 0; repeat < 3; repeat++)
            {
                network.RollbackAndResimulate(0, 1);
                Require(model.Value == 103, $"the changed branch must produce 103, got {model.Value}");
                Require(((NetworkSignalHistory<Set>)network.Histories[typeof(Set)]).Signals.Count == 0,
                    "the old branch's Set children must leave the history");
                Require(((NetworkSignalHistory<Add>)network.Histories[typeof(Add)]).Signals.Count == 2,
                    "only the external correction and the recomputed Add may remain");
            }
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void Rollback_NewBranch_CanGenerateAdditionalChildren()
        {
            using var context = new Context();
            var model = new Model();
            var network = new NetworkSignalBus(context.SignalBus);
            network.RegisterModel(model);
            context.SignalBus.Subscribe<Add>(signal => model.Value += signal.Amount);
            context.SignalBus.Subscribe<Set>(signal => model.Value = signal.Value);
            context.SignalBus.Subscribe<Derive>(_ =>
            {
                if (model.Value < 3) return;
                network.Fire(new Set(model.Value * 10));
                network.Fire(new Add(7));
            });
            network.SetTick(0);
            network.SetTick(1);
            network.Fire(new Derive());
            network.FireAtTick(new Add(2), 0);
            for (int repeat = 0; repeat < 3; repeat++)
            {
                network.RollbackAndResimulate(0, 1);
                Require(model.Value == 37, $"newly required children must produce 37, got {model.Value}");
            }
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void Rollback_RegeneratesFuturePayload_InItsOriginalOrder()
        {
            foreach (bool externalFirst in new[] { false, true })
            {
                using var context = new Context();
                var model = new Model();
                var network = new NetworkSignalBus(context.SignalBus);
                network.RegisterModel(model);
                context.SignalBus.Subscribe<Add>(signal => model.Value += signal.Amount);
                context.SignalBus.Subscribe<Set>(signal => model.Value = signal.Value);
                context.SignalBus.Subscribe<Derive>(_ => network.FireAtTick(new Set(model.Value * 10), 3));
                network.SetTick(0);
                if (externalFirst) network.FireAtTick(new Add(5), 3);
                network.SetTick(1);
                network.Fire(new Derive());
                if (!externalFirst) network.FireAtTick(new Add(5), 3);
                network.FireAtTick(new Add(2), 0);
                int expected = externalFirst ? 30 : 35;
                for (int repeat = 0; repeat < 3; repeat++)
                {
                    network.RollbackAndResimulate(0, 3);
                    Require(model.Value == expected,
                        $"future payload/order changed: externalFirst={externalFirst}, expected {expected}, got {model.Value}");
                }
            }
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void PartialRollback_KeepsPendingSchedule_FromAnEarlierPrunedParent()
        {
            using var context = new Context();
            var model = new Model();
            var network = new NetworkSignalBus(context.SignalBus);
            network.RegisterModel(model);
            context.SignalBus.Subscribe<Derive>(_ => network.FireAtTick(new Set(10), 2));
            context.SignalBus.Subscribe<Set>(signal => model.Value = signal.Value);
            network.SetTick(0);
            network.Fire(new Derive());
            network.SetTick(1);
            network.PruneHistory(0);
            network.RollbackAndResimulate(1, 1);
            Require(((NetworkSignalHistory<Set>)network.Histories[typeof(Set)]).Signals.Count == 1,
                "a schedule produced before rollback must not vanish when its target is after this replay horizon");
            for (int repeat = 0; repeat < 3; repeat++)
            {
                network.RollbackAndResimulate(1, 2);
                Require(model.Value == 10, "the retained schedule must still execute exactly once");
            }
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void Rollback_ChangedFutureTick_DoesNotExecuteTheOldSchedule()
        {
            using var context = new Context();
            var model = new Model();
            var network = new NetworkSignalBus(context.SignalBus);
            network.RegisterModel(model);
            context.SignalBus.Subscribe<Add>(signal => model.Value += signal.Amount);
            context.SignalBus.Subscribe<Set>(signal => model.Value = signal.Value);
            context.SignalBus.Subscribe<Derive>(_ => network.FireAtTick(new Set(model.Value * 10), model.Value + 1));
            network.SetTick(0);
            network.SetTick(1);
            network.Fire(new Derive()); // old target: 2
            network.FireAtTick(new Add(2), 0);
            network.RollbackAndResimulate(0, 3); // new target: 4
            Require(model.Value == 3, "the invalid old tick-2 child must not execute");
            var history = (NetworkSignalHistory<Set>)network.Histories[typeof(Set)];
            Require(history.Signals.Count == 1 && history.Signals[0].Tick == 4 && history.Signals[0].Signal.Value == 30,
                "the new future schedule must be retained beyond the partial replay horizon");
            network.RollbackAndResimulate(3, 4);
            Require(model.Value == 30, "the rescheduled child must execute at tick 4");
        }

#if UNITY_EDITOR && !NEXUS_DEBUG
        [Test]
#endif
        public void CorrectedNestedRollback_AfterWarmup_DoesNotAllocate()
        {
            using var context = new Context();
            var model = new Model();
            var network = Bind(context, model, false);
            network.SetTick(0);
            network.SetTick(1);
            network.Fire(new Derive());
            network.FireAtTick(new Add(2), 0);
            for (int i = 0; i < 100; i++) network.RollbackAndResimulate(0, 1);
#if UNITY_EDITOR
            using var probe = new GcAllocationProbe();
#else
            long before = GC.GetAllocatedBytesForCurrentThread();
#endif
            for (int i = 0; i < 1000; i++) network.RollbackAndResimulate(0, 1);
#if UNITY_EDITOR
            long allocated = probe.Stop();
#else
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
#endif
            Require(allocated == 0, $"warmed corrected rollback allocated {allocated} bytes/samples");
            Require(model.Value == 30, "the allocation probe must still execute the corrected branch");
        }

        private static NetworkSignalBus Bind(Context context, Model model, bool atTick)
        {
            var network = new NetworkSignalBus(context.SignalBus);
            network.RegisterModel(model);
            context.SignalBus.Subscribe<Add>(signal => model.Value += signal.Amount);
            context.SignalBus.Subscribe<Set>(signal => model.Value = signal.Value);
            context.SignalBus.Subscribe<Derive>(_ =>
            {
                var derived = new Set(model.Value * 10);
                if (atTick) network.FireAtTick(derived, network.CurrentTick);
                else network.Fire(derived);
            });
            return network;
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void History_NestedReplayAfterClearOrPrune_KeepsOuterSnapshot()
        {
            foreach (bool prune in new[] { false, true })
            {
                using var context = new Context();
                var history = new NetworkSignalHistory<Set>(2);
                var observed = new List<int>();
                history.Add(1, new Set(1));
                history.Add(1, new Set(2));
                context.SignalBus.Subscribe<Set>(signal =>
                {
                    observed.Add(signal.Value);
                    if (signal.Value != 1) return;
                    if (prune) history.Prune(1); else history.Clear();
                    history.Add(2, new Set(20));
                    history.Add(2, new Set(21));
                    history.ReplaySignals(2, context.SignalBus);
                });
                history.ReplaySignals(1, context.SignalBus);
                Require(string.Join(",", observed) == "1,20,21,2",
                    $"nested replay changed the outer snapshot: {string.Join(",", observed)}; prune={prune}");
            }
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void History_ThreeReplayDepths_KeepIndependentGrowingBuffers()
        {
            using var context = new Context();
            var history = new NetworkSignalHistory<Set>(1);
            var observed = new List<int>();
            history.Add(1, new Set(1));
            history.Add(1, new Set(2));
            context.SignalBus.Subscribe<Set>(signal =>
            {
                observed.Add(signal.Value);
                if (signal.Value != 1 && signal.Value != 20) return;
                int tick = signal.Value == 1 ? 2 : 3;
                history.Clear();
                for (int i = 0; i < tick; i++) history.Add(tick, new Set(tick * 10 + i));
                history.ReplaySignals(tick, context.SignalBus);
            });
            history.ReplaySignals(1, context.SignalBus);
            Require(string.Join(",", observed) == "1,20,30,31,32,21,2",
                "deeper replay growth must not replace an outer call's buffer");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void History_FailedNestedReplay_ReleasesItsBuffer_AndKeepsOuterSnapshot()
        {
            using var context = new Context();
            var history = new NetworkSignalHistory<Set>(2);
            var observed = new List<int>();
            bool failed = false;
            history.Add(1, new Set(1));
            history.Add(1, new Set(2));
            context.SignalBus.Subscribe<Set>(signal =>
            {
                observed.Add(signal.Value);
                if (signal.Value == 20) throw new OperationCanceledException("replay cancellation probe");
                if (signal.Value != 1) return;
                history.Clear();
                history.Add(2, new Set(20));
                history.Add(2, new Set(21));
                try { history.ReplaySignals(2, context.SignalBus); }
                catch (OperationCanceledException) { failed = true; }
            });
            history.ReplaySignals(1, context.SignalBus);
            Require(failed && string.Join(",", observed) == "1,20,2", "failure must not corrupt the outer snapshot");
            history.Clear();
            history.Add(3, new Set(30));
            history.ReplaySignals(3, context.SignalBus);
            Require(string.Join(",", observed) == "1,20,2,30", "replay must remain usable after the failed nested call");
        }

#if UNITY_EDITOR && !NEXUS_DEBUG
        [Test]
#endif
        public void History_NestedReplay_AfterWarmup_DoesNotAllocate()
        {
            using var context = new Context();
            var history = new NetworkSignalHistory<Set>(2);
            int total = 0;
            history.Add(0, new Set(1));
            history.Add(1, new Set(2));
            context.SignalBus.Subscribe<Set>(signal =>
            {
                total += signal.Value;
                if (signal.Value == 1) history.ReplaySignals(1, context.SignalBus);
            });
            for (int i = 0; i < 100; i++) history.ReplaySignals(0, context.SignalBus);
            int initial = total;
#if UNITY_EDITOR
            using var probe = new GcAllocationProbe();
#else
            long before = GC.GetAllocatedBytesForCurrentThread();
#endif
            for (int i = 0; i < 1000; i++) history.ReplaySignals(0, context.SignalBus);
#if UNITY_EDITOR
            long allocated = probe.Stop();
#else
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
#endif
            Require(allocated == 0 && total == initial + 3000,
                $"nested replay must deliver every signal without warmed allocation; allocated={allocated}, total={total}");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void NewFutureChild_UsesCorrectedProductionTime_NotItsNewIdentity()
        {
            using var context = new Context();
            var model = new Model();
            var network = new NetworkSignalBus(context.SignalBus);
            network.RegisterModel(model);
            context.SignalBus.Subscribe<Add>(signal => model.Value += signal.Amount);
            context.SignalBus.Subscribe<Set>(signal => model.Value = signal.Value);
            context.SignalBus.Subscribe<Derive>(_ =>
            {
                if (model.Value >= 3) network.FireAtTick(new Set(model.Value * 10), 3);
            });
            network.SetTick(0);
            network.SetTick(1);
            network.Fire(new Derive());
            network.SetTick(2);
            network.FireAtTick(new Add(5), 3);
            network.FireAtTick(new Add(2), 0);
            for (int repeat = 0; repeat < 3; repeat++)
            {
                network.RollbackAndResimulate(0, 3);
                Require(model.Value == 35,
                    $"the child produced at tick 1 must precede the external input recorded at tick 2; got {model.Value}");
            }
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void RetimedParent_OrdersFutureChildrenByTheirNewProductionTime()
        {
            using var context = new Context();
            var model = new Model();
            var network = new NetworkSignalBus(context.SignalBus);
            network.RegisterModel(model);
            context.SignalBus.Subscribe<Add>(signal => model.Value += signal.Amount);
            context.SignalBus.Subscribe<Set>(signal => model.Value = signal.Value);
            context.SignalBus.Subscribe<Derive>(_ => network.FireAtTick(new Schedule(), model.Value + 1));
            context.SignalBus.Subscribe<Schedule>(_ => network.FireAtTick(new Set(model.Value * 10), 5));
            network.SetTick(0);
            network.SetTick(1);
            network.Fire(new Derive());
            network.RollbackAndResimulate(0, 2); // the old parent at 2 has scheduled Set(10) at 5
            network.FireAtTick(new Add(5), 5); // recorded at tick 3
            network.FireAtTick(new Add(2), 0);
            for (int repeat = 0; repeat < 3; repeat++)
            {
                network.RollbackAndResimulate(0, 5);
                Require(model.Value == 30,
                    $"the retimed parent's child is now produced at tick 4, after the external schedule at 3; got {model.Value}");
            }
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void PartialRollback_InvalidatesSnapshotsOfTheAbandonedFuture()
        {
            using var context = new Context();
            var model = new Model();
            var network = Bind(context, model, false);
            network.SetTick(0);
            network.SetTick(1);
            network.Fire(new Derive());
            network.SetTick(2);
            network.SetTick(3);
            network.FireAtTick(new Add(2), 0);
            network.RollbackAndResimulate(0, 1);
            Require(model.Value == 30, "the corrected partial replay must finish at 30");
            for (int tick = 2; tick <= 3; tick++)
            {
                bool refused = false;
                try { network.RollbackAndResimulate(tick, tick); }
                catch (InvalidOperationException) { refused = true; }
                Require(refused && model.Value == 30,
                    $"abandoned snapshot {tick} must be refused without changing corrected state, got {model.Value}");
            }
            network.SetTick(2);
            network.RollbackAndResimulate(2, 2);
            Require(model.Value == 30, "a newly captured snapshot remains usable");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void RegeneratedTypedHistory_PreservesLogicalRecordingOrder()
        {
            using var context = new Context();
            var model = new Model();
            var network = new NetworkSignalBus(context.SignalBus);
            network.RegisterModel(model);
            context.SignalBus.Subscribe<Set>(signal => model.Value = signal.Value);
            context.SignalBus.Subscribe<Derive>(_ => network.Fire(new Set(10)));
            network.SetTick(0);
            network.Fire(new Derive());
            network.Fire(new Set(20));
            network.RollbackAndResimulate(0, 0);
            Require(model.Value == 20, "the main journal must preserve parent then external input");
            var history = (NetworkSignalHistory<Set>)network.Histories[typeof(Set)];
            model.Value = 0;
            history.ReplaySignals(0, context.SignalBus);
            Require(model.Value == 20, $"the public typed replay must not reverse regenerated payloads; got {model.Value}");
            var snapshot = history.Signals;
            Require(snapshot.Count == 2 && snapshot[0].Signal.Value == 10 && snapshot[1].Signal.Value == 20,
                "the public inspection snapshot must expose that same logical recording order");
        }

#if UNITY_EDITOR && !NEXUS_DEBUG
        [Test]
#endif
        public void TwoDerivedChildren_JournalAndTypedReplay_AfterWarmup_DoNotAllocate()
        {
            using var context = new Context();
            var model = new Model();
            var network = new NetworkSignalBus(context.SignalBus);
            network.RegisterModel(model);
            int received = 0;
            context.SignalBus.Subscribe<Set>(signal => { model.Value = signal.Value; received++; });
            context.SignalBus.Subscribe<Derive>(_ => { network.Fire(new Set(1)); network.Fire(new Set(2)); });
            network.SetTick(0);
            network.Fire(new Derive());
            var history = (NetworkSignalHistory<Set>)network.Histories[typeof(Set)];
            for (int i = 0; i < 2000; i++) { network.RollbackAndResimulate(0, 0); history.ReplaySignals(0, context.SignalBus); }
            int initial = received;
#if UNITY_EDITOR
            using var probe = new GcAllocationProbe();
#else
            long before = GC.GetAllocatedBytesForCurrentThread();
#endif
            for (int i = 0; i < 1000; i++) { network.RollbackAndResimulate(0, 0); history.ReplaySignals(0, context.SignalBus); }
#if UNITY_EDITOR
            long allocated = probe.Stop();
#else
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
#endif
            Require(allocated == 0 && received == initial + 4000 && model.Value == 2,
                $"two-child journal/typed replay allocated {allocated} bytes/samples; received {received - initial}");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void CorrectedScheduling_MatchesAnIndependentChronologicalQueue()
        {
            for (int seed = 0; seed < 6; seed++)
            {
                var inputs = new List<(int recorded, int target, ScriptInput input)>();
                for (int tick = 0; tick < 5; tick++)
                {
                    inputs.Add((tick, tick, new ScriptInput(0, (seed + tick) % 4 + 1)));
                    inputs.Add((tick, tick + 2, new ScriptInput(1, (seed * 3 + tick) % 7 + 10)));
                    inputs.Add((tick, tick, new ScriptInput(2, 0)));
                }
                int correction = seed % 3 + 1;
                var reference = new Model();
                var pending = new List<(int tick, ScriptInput input)>();
                int now = 0;
                Action<ScriptInput, int> emit = null;
                emit = (signal, target) =>
                {
                    if (target == now) ApplyScript(reference, signal, now, emit);
                    else pending.Add((target, signal));
                };
                for (now = 0; now <= 8; now++)
                {
                    // This reference uses append-only creation order, with no journal,
                    // identities, sorting, snapshots or framework replay implementation.
                    for (int i = 0; i < pending.Count; i++)
                        if (pending[i].tick == now) ApplyScript(reference, pending[i].input, now, emit);
                    foreach (var input in inputs)
                        if (input.recorded == now) emit(input.input, input.target);
                    if (now == 0) emit(new ScriptInput(0, correction), 0);
                }

                using var context = new Context();
                var actual = new Model();
                var network = new NetworkSignalBus(context.SignalBus);
                network.RegisterModel(actual);
                Action<ScriptInput, int> networkEmit = (signal, target) => network.FireAtTick(signal, target);
                context.SignalBus.Subscribe<ScriptInput>(signal => ApplyScript(actual, signal, network.CurrentTick, networkEmit));
                for (int tick = 0; tick < 5; tick++)
                {
                    network.SetTick(tick);
                    foreach (var input in inputs)
                        if (input.recorded == tick) network.FireAtTick(input.input, input.target);
                }
                network.FireAtTick(new ScriptInput(0, correction), 0);
                for (int repeat = 0; repeat < 3; repeat++)
                {
                    network.RollbackAndResimulate(0, 8);
                    Require(actual.Value == reference.Value,
                        $"script {seed}, repeat {repeat}: reference {reference.Value}, replay {actual.Value}");
                }
            }
        }

        private static void ApplyScript(Model model, ScriptInput input, int tick, Action<ScriptInput, int> emit)
        {
            if (input.Kind == 0) model.Value += input.Amount;
            else if (input.Kind == 1) model.Value = input.Amount;
            else if (input.Kind == 2)
            {
                if ((model.Value & 1) == 0)
                {
                    emit(new ScriptInput(0, model.Value % 3 + 1), tick);
                    emit(new ScriptInput(3, model.Value % 4 + 1), tick + 1 + (model.Value & 1));
                }
                else emit(new ScriptInput(1, model.Value + 2), tick + 1);
            }
            else emit(new ScriptInput(1, model.Value * 2 + input.Amount), tick + 1 + (input.Amount & 1));
        }
    }
}

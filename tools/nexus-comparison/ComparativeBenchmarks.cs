using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
#if !UNITY_5_3_OR_NEWER
using System.Text.Json;
#endif
using Nexus.Core;
using VContainer;
using Zenject;

public sealed class BenchLeaf { public readonly int Value = 7; }
public sealed class BenchMiddle { public readonly BenchLeaf Leaf; public BenchMiddle(BenchLeaf leaf) { Leaf = leaf; } }
public sealed class BenchRoot { public readonly BenchMiddle Middle; public BenchRoot(BenchMiddle middle) { Middle = middle; } }

public static class ComparativeBenchmarks
{
    private static object s_sink;
    private sealed class Candidate : IDisposable
    {
        public string Name;
        public Func<BenchRoot> Resolve;
        public Action DisposeAction;
        public void Dispose() => DisposeAction?.Invoke();
    }
    [Serializable]
    private sealed class Measurement
    {
        public string Library, Scenario;
        public int OperationsPerRound, Rounds;
        public double MedianNsPerOperation, MinNsPerOperation, MaxNsPerOperation, BytesPerOperation;
        public double[] RoundNsPerOperation;
    }

    [Serializable]
    private sealed class Report
    {
        public string Runtime, OS, Architecture, Scope, UnityVersion, Backend;
        public bool DevelopmentBuild, NexusDebug;
        public bool AllocationCounterValid;
        public long PositiveControlMeasuredBytes;
        public Measurement[] Measurements;
        public string VContainerCommit = "5401e5a7ebc4980a2b82141ffc26391a6547edd7";
        public string ZenjectCommit = "c2e33500a84f9408a809deca2af2c55494ab2482";
    }

#if UNITY_5_3_OR_NEWER
    [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void RunPlayer()
    {
        try
        {
            string destination = Environment.GetEnvironmentVariable("NEXUS_COMPARE_RESULT");
            if (string.IsNullOrEmpty(destination)) throw new InvalidOperationException("NEXUS_COMPARE_RESULT is required.");
            Main(new[] { destination });
            UnityEngine.Application.Quit(0);
        }
        catch (Exception error)
        {
            UnityEngine.Debug.LogException(error);
            UnityEngine.Application.Quit(2);
        }
    }
#endif

    private static Candidate Create(string name, bool transient)
    {
        if (name == "Nexus")
        {
            var di = new NexusDI();
            var life = transient ? Nexus.Core.Lifetime.Transient : Nexus.Core.Lifetime.Singleton;
            di.Bind<BenchLeaf>(life); di.Bind<BenchMiddle>(life); di.Bind<BenchRoot>(life);
            return new Candidate { Name = name, Resolve = () => di.Resolve<BenchRoot>(), DisposeAction = di.Dispose };
        }
        if (name == "VContainer")
        {
            var builder = new ContainerBuilder();
            var life = transient ? VContainer.Lifetime.Transient : VContainer.Lifetime.Singleton;
            builder.Register<BenchLeaf>(life); builder.Register<BenchMiddle>(life); builder.Register<BenchRoot>(life);
            var di = builder.Build();
            return new Candidate { Name = name, Resolve = () => di.Resolve<BenchRoot>(), DisposeAction = di.Dispose };
        }
        var zen = new DiContainer();
        if (transient)
        {
            zen.Bind<BenchLeaf>().AsTransient(); zen.Bind<BenchMiddle>().AsTransient(); zen.Bind<BenchRoot>().AsTransient();
        }
        else
        {
            zen.Bind<BenchLeaf>().AsSingle(); zen.Bind<BenchMiddle>().AsSingle(); zen.Bind<BenchRoot>().AsSingle();
        }
        return new Candidate { Name = name, Resolve = () => zen.Resolve<BenchRoot>() };
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Consume(BenchRoot root)
    {
        if (root.Middle.Leaf.Value != 7) throw new InvalidOperationException("Dependency graph is incorrect.");
        s_sink = root;
    }

    public static int Main(string[] args)
    {
        // Unity may auto-generate/register a Nexus binder when scripts reload.
        // This isolated benchmark compares all candidates without generated bindings.
        NexusDI.ClearCaches();
        long before = GC.GetAllocatedBytesForCurrentThread();
        var positiveControl = new byte[4096]; GC.KeepAlive(positiveControl);
        long positiveControlBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        bool allocationCounterValid = positiveControlBytes >= 4096;
#if !UNITY_5_3_OR_NEWER
        if (!allocationCounterValid) throw new InvalidOperationException("Allocation counter is inert.");
#endif
        const int rounds = 7;
        string[] names = { "Nexus", "VContainer", "Zenject" };
        var output = new List<Measurement>();
        foreach (string scenario in new[] { "cached-resolve", "transient-three-node-graph", "register-build-first-resolve-warm-metadata" })
        {
            int count = scenario == "cached-resolve" ? 200000 : scenario.StartsWith("transient") ? 20000 : 1000;
            var times = names.Select(_ => new List<double>()).ToArray();
            var allocations = new long[3];
            var candidates = names.Select(name => Create(name, scenario.StartsWith("transient"))).ToArray();
            try
            {
                for (int i = 0; i < candidates.Length; i++)
                {
                    var first = candidates[i].Resolve(); var second = candidates[i].Resolve();
                    if (ReferenceEquals(first, second) != (scenario != "transient-three-node-graph"))
                        throw new InvalidOperationException("Lifetime mismatch: " + candidates[i].Name);
                    for (int warm = 0; warm < 4000; warm++) Consume(candidates[i].Resolve());
                    if (scenario.StartsWith("register"))
                        for (int warm = 0; warm < 100; warm++) { using var build = Create(names[i], false); Consume(build.Resolve()); }
                }
                for (int round = 0; round < rounds; round++)
                    for (int offset = 0; offset < names.Length; offset++)
                    {
                        int i = (round + offset) % names.Length; // Rotate order between rounds.
                        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                        long bytes = GC.GetAllocatedBytesForCurrentThread();
                        long started = Stopwatch.GetTimestamp();
                        if (scenario.StartsWith("register"))
                            for (int op = 0; op < count; op++) { using var build = Create(names[i], false); Consume(build.Resolve()); }
                        else
                            for (int op = 0; op < count; op++) Consume(candidates[i].Resolve());
                        long elapsed = Stopwatch.GetTimestamp() - started;
                        allocations[i] += GC.GetAllocatedBytesForCurrentThread() - bytes;
                        times[i].Add(elapsed * 1e9 / Stopwatch.Frequency / count);
                    }
                for (int i = 0; i < names.Length; i++)
                {
                    double[] sorted = times[i].OrderBy(value => value).ToArray();
                    output.Add(new Measurement
                    {
                        Library = names[i], Scenario = scenario, OperationsPerRound = count, Rounds = rounds,
                        MedianNsPerOperation = sorted[rounds / 2], MinNsPerOperation = sorted[0], MaxNsPerOperation = sorted[rounds - 1],
                        BytesPerOperation = allocationCounterValid ? (double)allocations[i] / count / rounds : -1,
                        RoundNsPerOperation = times[i].ToArray()
                    });
                }
            }
            finally { foreach (var candidate in candidates) candidate.Dispose(); }
        }
        var report = new Report
        {
            Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            OS = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            Architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            Scope = "Core DI only. Unity bridges excluded from competitors. Nexus registered binder/factory caches cleared before warmup; no source generation/reflection baking for any library. Register/build/first resolve includes wrapper and available disposal (Zenject has no disposal action). Full Context startup and game frame times are outside this workload.",
            Measurements = output.ToArray(),
            AllocationCounterValid = allocationCounterValid,
            PositiveControlMeasuredBytes = positiveControlBytes,
#if UNITY_5_3_OR_NEWER
            UnityVersion = UnityEngine.Application.unityVersion,
            DevelopmentBuild = UnityEngine.Debug.isDebugBuild,
#if ENABLE_IL2CPP
            Backend = "IL2CPP",
#else
            Backend = "Mono",
#endif
#if NEXUS_DEBUG
            NexusDebug = true,
#endif
#else
            Backend = ".NET host",
#endif
        };
#if UNITY_5_3_OR_NEWER
        string json = UnityEngine.JsonUtility.ToJson(report, true);
#else
        string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true });
#endif
        File.WriteAllText(args.Length > 0 ? args[0] : "comparative-results.json", json);
        foreach (var row in output)
            Console.WriteLine($"{row.Library,-12} {row.Scenario,-42} {row.MedianNsPerOperation,10:F2} ns/op {row.BytesPerOperation,10:F2} B/op");
        GC.KeepAlive(s_sink);
        return 0;
    }
}

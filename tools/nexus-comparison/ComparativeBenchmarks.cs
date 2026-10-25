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
        public string ReflexCommit = "70c9b4e46a3b08ee8a13a61b71bb264f14446a7c";
        // Configuration: with NEXUS_COMPARE_GENERATED the candidates that have code generation use it
        // (Nexus constructor factories from its binder, VContainer's source-generated injectors); the
        // two booleans are observed at runtime, not assumed. Reflex's generator covers attribute
        // injection only (no effect on this constructor graph); Zenject has no generator.
        public bool Generated;
        public bool NexusGeneratedFactories;
        public bool VContainerGeneratedInjectors;
    }

#if UNITY_5_3_OR_NEWER
    [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void RunPlayer()
    {
        try
        {
            // Desktop players receive the result path through the environment; a device player has no
            // environment, so it writes next to its persistent data (adb reads it back) and logs a marker.
            string destination = UnityEngine.Application.platform == UnityEngine.RuntimePlatform.Android
                ? Path.Combine(UnityEngine.Application.persistentDataPath, "unity-results.json")
                : Environment.GetEnvironmentVariable("NEXUS_COMPARE_RESULT");
            if (string.IsNullOrEmpty(destination)) throw new InvalidOperationException("NEXUS_COMPARE_RESULT is required.");
            Main(new[] { destination });
            UnityEngine.Debug.Log("NEXUS_COMPARE_DONE " + destination);
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
        if (name == "Reflex")
        {
            // Lazy singletons match the other candidates (Transient + Eager is rejected by Reflex).
            var builder = new Reflex.Core.ContainerBuilder();
            var life = transient ? Reflex.Enums.Lifetime.Transient : Reflex.Enums.Lifetime.Singleton;
            builder.RegisterType(typeof(BenchLeaf), life, Reflex.Enums.Resolution.Lazy);
            builder.RegisterType(typeof(BenchMiddle), life, Reflex.Enums.Resolution.Lazy);
            builder.RegisterType(typeof(BenchRoot), life, Reflex.Enums.Resolution.Lazy);
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
#if NEXUS_COMPARE_GENERATED
        const bool generated = true;
#if !UNITY_5_3_OR_NEWER
        // Host: the Nexus source generator emitted the binder into this compilation; Unity runs its
        // editor-generated binder through RuntimeInitializeOnLoadMethod before this method.
        Nexus.Generated.NexusGeneratedBinder.Initialize();
#endif
#else
        const bool generated = false;
        // Unity may auto-generate/register a Nexus binder when scripts reload.
        // The reflection configuration compares all candidates without generated bindings.
        NexusDI.ClearGeneratedRegistrations();
#endif
        bool nexusGenerated = NexusDI.HasRegisteredConstructorFactory(typeof(BenchRoot))
            && NexusDI.HasRegisteredConstructorFactory(typeof(BenchMiddle))
            && NexusDI.HasRegisteredConstructorFactory(typeof(BenchLeaf));
        bool vcontainerGenerated = typeof(BenchRoot).Assembly.GetType("BenchRootGeneratedInjector", false) != null
            && typeof(BenchRoot).Assembly.GetType("BenchMiddleGeneratedInjector", false) != null;
        if (generated != nexusGenerated || generated != vcontainerGenerated)
            throw new InvalidOperationException($"Configuration mismatch: generated={generated}, Nexus factories={nexusGenerated}, VContainer injectors={vcontainerGenerated}.");
        long before = GC.GetAllocatedBytesForCurrentThread();
        var positiveControl = new byte[4096]; GC.KeepAlive(positiveControl);
        long positiveControlBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        bool allocationCounterValid = positiveControlBytes >= 4096;
#if !UNITY_5_3_OR_NEWER
        if (!allocationCounterValid) throw new InvalidOperationException("Allocation counter is inert.");
#endif
        const int rounds = 7;
        string[] names = { "Nexus", "VContainer", "Zenject", "Reflex" };
        var output = new List<Measurement>();
        foreach (string scenario in new[] { "cached-resolve", "transient-three-node-graph", "register-build-first-resolve-warm-metadata" })
        {
            int count = scenario == "cached-resolve" ? 200000 : scenario.StartsWith("transient") ? 20000 : 1000;
            var times = names.Select(_ => new List<double>()).ToArray();
            var allocations = new long[names.Length];
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
            Scope = "Core DI only. Unity bridges excluded from competitors. " + (generated
                ? "Generated configuration: Nexus runs its generated binder and VContainer its source generator; Zenject and Reflex use reflection. "
                : "Nexus registered binder/factory caches cleared before warmup; no source generation/reflection baking for any library. ")
                + "Register/build/first resolve includes wrapper and available disposal (Zenject has no disposal action; Reflex's Dispose log is silenced: host shim logger / player LogLevel=Warning). Full Context startup and game frame times are outside this workload.",
            Measurements = output.ToArray(),
            AllocationCounterValid = allocationCounterValid,
            PositiveControlMeasuredBytes = positiveControlBytes,
            Generated = generated,
            NexusGeneratedFactories = nexusGenerated,
            VContainerGeneratedInjectors = vcontainerGenerated,
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
        Console.WriteLine(generated ? "Configuration: generated bindings (Nexus factories, VContainer injectors)" : "Configuration: reflection (no generated bindings)");
        foreach (var row in output)
            Console.WriteLine($"{row.Library,-12} {row.Scenario,-42} {row.MedianNsPerOperation,10:F2} ns/op {row.BytesPerOperation,10:F2} B/op");
        GC.KeepAlive(s_sink);
        return 0;
    }
}

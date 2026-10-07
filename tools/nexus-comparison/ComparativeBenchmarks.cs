using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
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
    private sealed class Measurement
    {
        public string Library, Scenario;
        public int OperationsPerRound, Rounds;
        public double MedianNsPerOperation, MinNsPerOperation, MaxNsPerOperation, BytesPerOperation;
        public double[] RoundNsPerOperation;
    }

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
        long before = GC.GetAllocatedBytesForCurrentThread();
        var positiveControl = new byte[4096]; GC.KeepAlive(positiveControl);
        if (GC.GetAllocatedBytesForCurrentThread() - before < 4096) throw new InvalidOperationException("Allocation counter is inert.");
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
                        BytesPerOperation = (double)allocations[i] / count / rounds, RoundNsPerOperation = times[i].ToArray()
                    });
                }
            }
            finally { foreach (var candidate in candidates) candidate.Dispose(); }
        }
        var report = new
        {
            Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            OS = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            Architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            Scope = "Host .NET core DI only. Unity bridges excluded. No source generation/reflection baking for any library. Startup validation is outside this DI-only workload. Not Unity/IL2CPP device or game frame-time evidence.",
            VContainer = new { Version = "1.19.0", Commit = "5401e5a7ebc4980a2b82141ffc26391a6547edd7" },
            Zenject = new { Version = "9.2.0", Commit = "c2e33500a84f9408a809deca2af2c55494ab2482" },
            Measurements = output
        };
        string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true });
        File.WriteAllText(args.Length > 0 ? args[0] : "comparative-results.json", json);
        foreach (var row in output)
            Console.WriteLine($"{row.Library,-12} {row.Scenario,-42} {row.MedianNsPerOperation,10:F2} ns/op {row.BytesPerOperation,10:F2} B/op");
        GC.KeepAlive(s_sink);
        return 0;
    }
}

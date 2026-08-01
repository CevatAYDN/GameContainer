// Machine-readable result capture for --json mode. Every suite's Report/Check helper
// pushes into this sink; Program emits a JSON document at the end of the run so CI
// dashboards and cross-machine comparisons can consume the same data the console shows.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace NexusBench
{
    public static class ResultSink
    {
        public sealed class Result
        {
            public string Suite;
            public string Name;
            public bool Ok;
            public string Detail;
        }

        private static readonly List<Result> s_results = new();

        public static void Capture(string suite, string name, bool ok, string detail)
        {
            lock (s_results)
            {
                s_results.Add(new Result { Suite = suite, Name = name, Ok = ok, Detail = detail });
            }
        }

        public static void Clear()
        {
            lock (s_results) s_results.Clear();
        }

        public static string ToJson()
        {
            lock (s_results)
            {
                var doc = new
                {
                    generatedAtUtc = DateTime.UtcNow.ToString("O"),
                    totalTests = s_results.Count,
                    failed = s_results.Count(r => !r.Ok),
                    suites = s_results
                        .GroupBy(r => r.Suite)
                        .Select(g => new
                        {
                            name = g.Key,
                            total = g.Count(),
                            failed = g.Count(r => !r.Ok),
                            tests = g.Select(r => new
                            {
                                name = r.Name,
                                ok = r.Ok,
                                detail = r.Detail
                            }).ToList()
                        })
                        .ToList()
                };
                return JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
            }
        }
    }
}

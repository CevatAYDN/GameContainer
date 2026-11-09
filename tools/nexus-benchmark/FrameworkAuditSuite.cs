using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace NexusBench
{
    public static class FrameworkAuditSuite
    {
        public static int Run(string group = null)
        {
            int failures = 0, count = 0;
            var fixtures = typeof(FrameworkAuditSuite).Assembly.GetTypes()
                .Where(type => type.IsClass && !type.IsAbstract && !type.IsNested && type.Namespace == "Nexus.Editor.Tests"
                    && type.Name.StartsWith("FullReview", StringComparison.Ordinal)
                    && type.Name.EndsWith("Tests", StringComparison.Ordinal)
                    && (group == null || type.Name.IndexOf(group, StringComparison.OrdinalIgnoreCase) >= 0))
                .OrderBy(type => type.FullName, StringComparer.Ordinal);
            foreach (var fixture in fixtures)
            {
                foreach (var method in fixture.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    .Where(method => !method.IsSpecialName && method.GetParameters().Length == 0
                        && (method.ReturnType == typeof(void) || typeof(Task).IsAssignableFrom(method.ReturnType)))
                    .OrderBy(method => method.Name, StringComparer.Ordinal))
                {
                    count++;
                    string name = fixture.Name + "." + method.Name;
                    string detail = "public boundary regression passed";
                    bool ok = true;
                    try
                    {
                        var result = method.Invoke(Activator.CreateInstance(fixture), null);
                        if (result is Task task) task.GetAwaiter().GetResult();
                    }
                    catch (Exception ex)
                    {
                        ok = false;
                        failures++;
                        detail = (ex is TargetInvocationException invocation && invocation.InnerException != null
                            ? invocation.InnerException : ex).ToString();
                    }
                    Console.WriteLine($"[FrameworkAudit] {(ok ? "PASS" : "FAIL")} {name}: {detail}");
                    ResultSink.Capture("FrameworkAudit", name, ok, detail);
                }
            }
            if (count == 0 && group != null)
            {
                Console.WriteLine("[FrameworkAudit] FAIL No matching regressions: " + group);
                ResultSink.Capture("FrameworkAudit", "NoMatchingRegressions", false, group);
                return 1;
            }
            return failures;
        }
    }
}

using System;
using System.Collections.Generic;
using Nexus.Core;
using Nexus.Firebase.Crash;

namespace Nexus.Firebase.Mocks
{
    /// <summary>Records the non-fatals, custom keys and log lines the game reports, for the editor, development builds and tests.</summary>
    public sealed class MockCrashReporter : NexusService<ICrashReporter>, ICrashReporter
    {
        private readonly List<Exception> _recorded = new List<Exception>();
        private readonly Dictionary<string, string> _keys = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly List<string> _logged = new List<string>();
        private readonly CrashContext _context = new CrashContext();

        public MockCrashReporter()
        {
            _context.Open((key, value) => _keys[key] = value, _logged.Add, _recorded.Add);
        }

        public IReadOnlyList<Exception> Recorded => _recorded;
        public IReadOnlyDictionary<string, string> Keys => _keys;
        public IReadOnlyList<string> Logged => _logged;

        public void RecordNonFatal(Exception exception) => _context.RecordNonFatal(exception);

        public void SetCustomKey(string key, string value) => _context.SetCustomKey(key, value);

        public void Log(string message) => _context.Log(message);
    }
}

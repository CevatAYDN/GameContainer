using System;
using System.Collections.Generic;
using Nexus.Core.Services;

namespace Nexus.Firebase.Crash
{
    /// <summary>
    /// The custom keys and log lines of an <see cref="ICrashReporter"/>, held until the reporter can take them (issue #199):
    /// services initialize one by one in bind order, so a service bound before the reporter can set a key before the SDK is
    /// ready. Each key keeps its last value; at most <see cref="HeldLines"/> lines are held, the oldest dropped first.
    /// Safe to call from any thread.
    /// </summary>
    public sealed class CrashContext
    {
        public const int HeldLines = 64;

        private readonly object _gate = new object();
        private Dictionary<string, string> _keys = new Dictionary<string, string>(StringComparer.Ordinal);
        private Queue<string> _lines = new Queue<string>();
        private Action<string, string> _setKey;
        private Action<string> _log;

        public void SetCustomKey(string key, string value)
        {
            if (string.IsNullOrEmpty(key))
            {
                NexusLog.WarnAlways("[CrashReporter] A custom key needs a name: the value '" + value + "' was not set.");
                return;
            }
            lock (_gate)
            {
                if (_setKey != null) _setKey(key, value ?? "");
                else if (_keys != null) _keys[key] = value ?? "";
            }
        }

        public void Log(string message)
        {
            if (message == null) return;
            lock (_gate)
            {
                if (_log != null) _log(message);
                else if (_lines != null)
                {
                    if (_lines.Count == HeldLines) _lines.Dequeue();
                    _lines.Enqueue(message);
                }
            }
        }

        /// <summary>Hands what is held to the reporter, keys first and lines in order, and every later call straight to it.</summary>
        public void Open(Action<string, string> setKey, Action<string> log)
        {
            lock (_gate)
            {
                foreach (var pair in _keys ?? new Dictionary<string, string>()) setKey(pair.Key, pair.Value);
                foreach (var line in _lines ?? new Queue<string>()) log(line);
                _keys = null;
                _lines = null;
                _setKey = setKey;
                _log = log;
            }
        }

        /// <summary>The reporter will never be ready (the SDK is unavailable): drops what is held and holds nothing more.</summary>
        public void Close()
        {
            lock (_gate)
            {
                _keys = null;
                _lines = null;
            }
        }
    }
}

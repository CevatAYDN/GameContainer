using System;
using System.Collections.Generic;

namespace Nexus.Core.Services
{
    /// <summary>
    /// An <see cref="IAnalyticsService"/> that sends every call to several backends in order, for example Firebase and
    /// a development build's local file (#159). It forwards calls only: it does not initialize or dispose its targets,
    /// so bind each target as a service of its own (<c>BindServiceInstance&lt;LocalFileSink&gt;(sink)</c>) and pass the
    /// same instance here. Validation and consent stay with each target: a target receives what the game sends.
    /// A target that throws is reported once with <see cref="NexusLog.WarnAlways"/> and the targets after it still
    /// receive the call.
    /// </summary>
    public sealed class AnalyticsFanOut : NexusService<IAnalyticsService>, IAnalyticsService
    {
        private readonly IAnalyticsService[] _targets;
        private readonly bool[] _reported;

        public AnalyticsFanOut(params IAnalyticsService[] targets)
        {
            if (targets == null) throw new ArgumentNullException(nameof(targets));
            for (int i = 0; i < targets.Length; i++)
                if (targets[i] == null) throw new ArgumentNullException(nameof(targets), $"Target {i} is null.");
            _targets = (IAnalyticsService[])targets.Clone();
            _reported = new bool[_targets.Length];
        }

        /// <summary>The backends, in the order they receive each call.</summary>
        public IReadOnlyList<IAnalyticsService> Targets => _targets;

        public void LogEvent(string eventName)
        {
            for (int i = 0; i < _targets.Length; i++)
            {
                try { _targets[i].LogEvent(eventName); }
                catch (Exception ex) { Report(i, $"LogEvent('{eventName}')", ex); }
            }
        }

        public void LogEvent(string eventName, Dictionary<string, object> parameters)
        {
            for (int i = 0; i < _targets.Length; i++)
            {
                try { _targets[i].LogEvent(eventName, parameters); }
                catch (Exception ex) { Report(i, $"LogEvent('{eventName}')", ex); }
            }
        }

        public void SetUserProperty(string key, string value)
        {
            for (int i = 0; i < _targets.Length; i++)
            {
                try { _targets[i].SetUserProperty(key, value); }
                catch (Exception ex) { Report(i, $"SetUserProperty('{key}')", ex); }
            }
        }

        // Once per target: a broken backend would otherwise write one warning per event.
        private void Report(int index, string call, Exception ex)
        {
            if (_reported[index]) return;
            _reported[index] = true;
            NexusLog.WarnAlways($"[NexusAnalytics] {_targets[index].GetType().Name}.{call} threw {ex.GetType().Name}: {ex.Message}. " +
                "The other targets still receive every call; later failures of this target are not reported.");
        }
    }
}

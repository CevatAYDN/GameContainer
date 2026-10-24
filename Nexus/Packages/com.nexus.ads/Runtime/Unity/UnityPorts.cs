using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace Nexus.Ads.Unity
{
    // The Unity implementations of the core's ports (Runtime/Core/Ports.cs). Each is the smallest
    // thing that does the job; AdsBootstrap wires them.

    /// <summary>Unity's unscaled real-time clock and the UTC calendar day.</summary>
    public sealed class UnityAdClock : IAdClock
    {
        public double Seconds => Time.realtimeSinceStartupAsDouble;
        public long UtcDay => DateTime.UtcNow.Ticks / TimeSpan.TicksPerDay;
    }

    /// <summary>Foreground state from the application pause and focus callbacks.</summary>
    [DisallowMultipleComponent]
    public sealed class UnityAppVisibility : MonoBehaviour, IAppVisibility
    {
        private bool _visible = true;
        public bool Visible => _visible;
        public event Action<bool> Changed;

        private void OnApplicationPause(bool paused) => Set(!paused);
        private void OnApplicationFocus(bool focused) { if (focused) Set(true); }

        private void Set(bool visible)
        {
            if (_visible == visible) return;
            _visible = visible;
            Changed?.Invoke(visible);
        }
    }

    /// <summary>The Unity main thread, captured where the bootstrap is created.</summary>
    public sealed class UnityMainThread : IMainThread
    {
        private readonly SynchronizationContext _context;
        private readonly int _threadId;

        public UnityMainThread()
        {
            _context = SynchronizationContext.Current;
            _threadId = Thread.CurrentThread.ManagedThreadId;
        }

        public bool IsCurrent => Thread.CurrentThread.ManagedThreadId == _threadId;

        public void Post(Action action)
        {
            if (_context != null) _context.Post(_ => action(), null);
            else action(); // no context captured (headless test): run where we are rather than drop the callback
        }
    }

    /// <summary>Delays as coroutines on unscaled time, so a paused game (time scale 0 during an ad) still retries.</summary>
    [DisallowMultipleComponent]
    public sealed class UnityAdScheduler : MonoBehaviour, IAdScheduler
    {
        private sealed class Handle : IDisposable
        {
            public Coroutine Routine;
            public UnityAdScheduler Owner;
            public bool Done;
            public void Dispose() { if (!Done && Owner != null && Routine != null) Owner.StopCoroutine(Routine); Done = true; }
        }

        public IDisposable Schedule(double seconds, Action action)
        {
            var handle = new Handle { Owner = this };
            handle.Routine = StartCoroutine(Wait(handle, (float)seconds, action));
            return handle;
        }

        private static IEnumerator Wait(Handle handle, float seconds, Action action)
        {
            yield return new WaitForSecondsRealtime(seconds);
            if (handle.Done) yield break;
            handle.Done = true;
            action();
        }
    }

    /// <summary>Pacing counters in PlayerPrefs; the keys are the core's.</summary>
    public sealed class PlayerPrefsPacingStore : IPacingStore
    {
        public string Get(string key) => PlayerPrefs.HasKey(key) ? PlayerPrefs.GetString(key) : null;
        public void Set(string key, string value) { PlayerPrefs.SetString(key, value); PlayerPrefs.Save(); }
    }

    /// <summary>
    /// Fans every ad event out to the registered sinks (analytics bridges, the debug panel) and logs
    /// it in the editor and development builds. Violations are errors there and silent in release.
    /// </summary>
    public sealed class UnityAdTelemetry : IAdTelemetry
    {
        private readonly List<IAdTelemetry> _sinks = new List<IAdTelemetry>();
        private readonly bool _verbose;

        public UnityAdTelemetry(bool verbose) => _verbose = verbose;

        public void AddSink(IAdTelemetry sink) { if (sink != null && !_sinks.Contains(sink)) _sinks.Add(sink); }
        public void RemoveSink(IAdTelemetry sink) => _sinks.Remove(sink);

        public void Record(in AdEvent e)
        {
            if (_verbose)
            {
                if (e.Kind == AdEventKind.Violation) Debug.LogError("[Nexus Ads] PolicyViolation " + e);
                else Debug.Log("[Nexus Ads] " + e);
            }
            for (int i = 0; i < _sinks.Count; i++) _sinks[i].Record(e);
        }
    }
}

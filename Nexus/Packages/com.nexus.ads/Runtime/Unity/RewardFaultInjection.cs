// A runtime Debug.isDebugBuild check would leave the reward fault API in release assemblies (#412).
#if UNITY_EDITOR || NEXUS_ADS_FAULT_TESTS
using System;
using System.Globalization;
using UnityEngine;

namespace Nexus.Ads.Unity
{
    /// <summary>Development-only controls. Only a received SDK reward can be delayed or replayed.</summary>
    public interface IRewardFaultDebug
    {
        RewardFaultInjection RewardFaults { get; }
    }

    /// <summary>One armed show, one held reward, one replay slot; all calls are on the Unity main thread.</summary>
    public sealed class RewardFaultInjection : IDisposable
    {
        public sealed class Show
        {
            internal int Id, Mode;
            internal double Delay;
            internal string Placement;
            internal bool Closed, Received;
            internal Action Deliver;
            internal Func<bool> Released;
        }

        private IAdScheduler _scheduler;
        private GameObject _schedulerHost;
        private readonly string _provider;
        private int _nextId, _armedMode;
        private double _delay;
        private Show _pending, _last;
        private IDisposable _timer;
        private bool _disposed;

        public RewardFaultInjection(string provider, IAdScheduler scheduler = null)
        { _provider = provider; _scheduler = scheduler; }

        public void DebugDelayReward(TimeSpan delay)
        {
            if (delay.TotalSeconds < 0 || delay.TotalSeconds > 120) throw new ArgumentOutOfRangeException(nameof(delay));
            Arm(1, delay.TotalSeconds);
        }

        public void DebugDeliverRewardDuringNextShow() => Arm(2, 0);

        private void Arm(int mode, double delay)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(RewardFaultInjection));
            if (_pending != null) throw new InvalidOperationException("Deliver or reset the pending reward before arming another fault.");
            _armedMode = mode;
            _delay = delay;
            Log(null, "armed mode=" + mode + " delay=" + delay.ToString(CultureInfo.InvariantCulture));
        }

        public Show BeginShow(string placement, Action deliver, Func<bool> released)
        {
            var show = new Show { Id = ++_nextId, Placement = placement, Deliver = deliver, Released = released,
                Mode = _armedMode, Delay = _delay };
            _armedMode = 0;
            return show;
        }

        public void Receive(Show show)
        {
            if (_disposed || show == null || show.Received) return;
            show.Received = true;
            _last = show;
            Log(show, "captured-real-reward");
            if (show.Mode == 0) { Deliver(show); return; }
            // Never drop a received reward if a second already-armed show reaches this boundary.
            if (_pending != null) { Deliver(show); return; }
            _pending = show;
            if (show.Closed && show.Mode == 1) Schedule(show);
        }

        public void Closed(Show show)
        {
            if (_disposed || show == null || show.Closed) return;
            show.Closed = true;
            Log(show, "closed");
            if (ReferenceEquals(_pending, show) && show.Mode == 1) Schedule(show);
        }

        public void Opened(Show show)
        {
            if (_disposed) return;
            Log(show, "opened");
            if (_pending != null && _pending.Mode == 2 && !ReferenceEquals(_pending, show)) Deliver(_pending);
        }

        private void Schedule(Show show)
        {
            if (_scheduler == null)
            {
                _schedulerHost = new GameObject("Nexus reward fault scheduler");
                UnityEngine.Object.DontDestroyOnLoad(_schedulerHost);
                _scheduler = _schedulerHost.AddComponent<UnityAdScheduler>();
            }
            _timer = _scheduler.Schedule(show.Delay, () => { _timer = null; if (!_disposed) Deliver(show); });
        }

        private void Deliver(Show show)
        {
            if (ReferenceEquals(_pending, show)) _pending = null;
            Log(show, "deliver");
            show.Deliver();
        }

        public bool DebugReplayLastReward()
        {
            if (_disposed || _last == null || ReferenceEquals(_last, _pending)) return false;
            Log(_last, "replay");
            _last.Deliver();
            return true;
        }

        /// <summary>Resets the experiment, delivering any received held reward first.</summary>
        public void DebugReset()
        {
            _armedMode = 0;
            _timer?.Dispose();
            _timer = null;
            if (_pending != null && !_disposed) Deliver(_pending);
            _last = null;
        }

        private void Log(Show show, string step) => Debug.Log("[Nexus Ads Fault] provider=" + _provider +
            " t=" + Time.realtimeSinceStartupAsDouble.ToString("F3", CultureInfo.InvariantCulture) +
            " show=" + (show?.Id ?? 0) + " placement=" + (show?.Placement ?? "-") +
            " released=" + (show?.Released?.Invoke() ?? false) + " " + step);

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
            _pending = _last = null;
            if (_schedulerHost != null) UnityEngine.Object.Destroy(_schedulerHost);
            _schedulerHost = null;
        }
    }
}
#endif

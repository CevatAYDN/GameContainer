using System;
#if UNITY_5_3_OR_NEWER
using Unity.Profiling;
#endif

namespace Nexus.Core
{
    /// <summary>Test-only allocation measurement with a positive control; never trusts an inert counter.</summary>
    public sealed class GcAllocationProbe : IDisposable
    {
#if UNITY_5_3_OR_NEWER
        private const int Capacity = 100000;
        private ProfilerRecorder _recorder;
#else
        private readonly long _start;
#endif
        private bool _stopped;
        private long _allocations;

        public GcAllocationProbe()
        {
#if UNITY_5_3_OR_NEWER
            _recorder = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "GC.Alloc", Capacity,
                ProfilerRecorderOptions.CollectOnlyOnCurrentThread);
            try
            {
                if (!_recorder.Valid) throw new InvalidOperationException("GC.Alloc recorder is unavailable.");
                var control = new byte[4096];
                GC.KeepAlive(control);
                _recorder.Stop();
                if (ReadCount() == 0) throw new InvalidOperationException("GC.Alloc recorder failed its positive control.");
                _recorder.Reset();
                _recorder.Start();
            }
            catch { _recorder.Dispose(); throw; }
#else
            long before = GC.GetAllocatedBytesForCurrentThread();
            var control = new byte[4096];
            GC.KeepAlive(control);
            if (GC.GetAllocatedBytesForCurrentThread() - before < 4096)
                throw new InvalidOperationException("Managed allocation counter failed its positive control.");
            _start = GC.GetAllocatedBytesForCurrentThread();
#endif
        }

        /// <summary>Returns allocation samples in Unity, or a nonzero allocation indicator on .NET. Only zero is comparable across runtimes.</summary>
        public long Stop()
        {
            if (_stopped) return _allocations;
#if UNITY_5_3_OR_NEWER
            _recorder.Stop();
            _allocations = ReadCount();
#else
            _allocations = GC.GetAllocatedBytesForCurrentThread() == _start ? 0 : 1;
#endif
            _stopped = true;
            return _allocations;
        }

#if UNITY_5_3_OR_NEWER
        private long ReadCount()
        {
            if (_recorder.Count >= Capacity)
                throw new InvalidOperationException("GC.Alloc recorder capacity exceeded; measurement is incomplete.");
            return _recorder.Count;
        }
#endif
        public void Dispose()
        {
#if UNITY_5_3_OR_NEWER
            _recorder.Dispose();
#endif
        }
    }
}

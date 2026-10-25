namespace Nexus.Core.Services
{
    public enum HapticType
    {
        Light,      // UI Impact (Light)
        Medium,     // UI Impact (Medium)
        Heavy,      // UI Impact (Heavy)
        Warning,    // UI Notification (Warning)
        Success,    // UI Notification (Success)
        Selection,  // UI Selection
    }

    /// <summary>A haptic effect made once with <see cref="IHapticService.CreateEffect"/> or
    /// <see cref="IHapticService.CreateWaveform"/> and played with <see cref="IHapticService.Play"/>.</summary>
    public readonly struct HapticEffect
    {
        public HapticEffect(int id) { Id = id; }
        public int Id { get; }
        public bool IsValid => Id > 0;
    }

    public interface IHapticService
    {
        void Vibrate(HapticType type);

        /// <summary>Haptics on or off. Persisted (<c>NT_HapticsDisabled</c>).</summary>
        bool IsEnabled { get; set; }

        /// <summary>A one-shot effect: <paramref name="milliseconds"/> above zero, <paramref name="amplitude"/> 1-255
        /// or -1 for the device default (a device without amplitude control always uses its default).</summary>
        HapticEffect CreateEffect(long milliseconds, int amplitude = -1);

        /// <summary>A waveform: <paramref name="timings"/> in milliseconds, alternating off and on starting with off,
        /// and one amplitude per timing (0-255, or -1 for the device default). The arrays are copied.</summary>
        HapticEffect CreateWaveform(long[] timings, int[] amplitudes);

        /// <summary>Plays an effect. On Android 8+ the effect was built once and this call does not allocate;
        /// in the editor and on desktop it does nothing.</summary>
        void Play(HapticEffect effect);
    }
}

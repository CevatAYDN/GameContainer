namespace Nexus.Core.Services
{
    /// <summary>
    /// Where the audio, haptic, economy and progression services keep their state. Optional: bind one with
    /// <c>builder.BindInstance(new SettingsStores { Economy = mySaveStore })</c>; a slot left null uses the bound
    /// <see cref="IPlayerPrefsService"/>. The services keep their key names (<c>NT_Audio*</c>, <c>NT_HapticsDisabled</c>,
    /// <c>NT_Eco_*</c> / <c>NT_EcoBig_*</c>, <c>NT_Prog_*</c>), so a game's store maps those keys onto its own save.
    /// </summary>
    public sealed class SettingsStores
    {
        public IPlayerPrefsService Audio;
        public IPlayerPrefsService Haptics;
        public IPlayerPrefsService Economy;
        public IPlayerPrefsService Progression;
    }
}

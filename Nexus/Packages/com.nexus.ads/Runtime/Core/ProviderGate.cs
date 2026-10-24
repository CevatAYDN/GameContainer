namespace Nexus.Ads
{
    /// <summary>What the host should run: the real SDK, the simulator, or nothing.</summary>
    public enum GateDecision
    {
        Provider,
        Simulator,
        Blocked,
    }

    /// <summary>What the host runs after the gate: the real SDK, the labelled simulator, or nothing (the controller never starts and every show answers NotReady).</summary>
    public enum HostMode
    {
        Provider,
        Simulator,
        Idle,
    }

    public readonly struct GateResult
    {
        public readonly GateDecision Decision;
        /// <summary>The <see cref="PolicyRules"/> id behind a Simulator or Blocked decision; null for Provider.</summary>
        public readonly string Rule;
        public readonly string Reason;

        public GateResult(GateDecision decision, string rule, string reason)
        {
            Decision = decision;
            Rule = rule;
            Reason = reason;
        }
    }

    /// <summary>
    /// Decides before the SDK starts whether this build on this device may talk to it
    /// (<see cref="PolicyRules.TestModeRequired"/>, <see cref="PolicyRules.UnregisteredDevice"/>).
    /// The editor always runs the simulator; a release build never carries test configuration; a
    /// development build talks to the SDK only with published test data or on a listed device.
    /// </summary>
    public static class ProviderGate
    {
        public static GateResult Evaluate(BuildKind build, ProviderConfig config, string deviceAdvertisingId)
        {
            switch (build)
            {
                case BuildKind.Editor:
                    return new GateResult(GateDecision.Simulator, null, "the editor runs the simulator");

                case BuildKind.Release:
                    if (config.TestMode)
                        return new GateResult(GateDecision.Blocked, PolicyRules.TestModeRequired, "a release build has test mode on");
                    if (config.TestDeviceIds.Count > 0)
                        return new GateResult(GateDecision.Blocked, PolicyRules.TestModeRequired, "a release build carries test devices");
                    if (config.UsesPublishedTestData)
                        return new GateResult(GateDecision.Blocked, PolicyRules.TestModeRequired, "a release build uses the provider's sample units");
                    return new GateResult(GateDecision.Provider, null, null);

                default:
                    if (config.UsesPublishedTestData)
                        return new GateResult(GateDecision.Provider, null, null);
                    if (!config.TestMode)
                        return new GateResult(GateDecision.Blocked, PolicyRules.TestModeRequired, "a development build needs the provider's test mode");
                    if (ProviderConfig.IsUnavailableAdvertisingId(deviceAdvertisingId))
                        return new GateResult(GateDecision.Simulator, PolicyRules.UnregisteredDevice,
                            "the device's advertising id is unavailable (Google Play services did not give one, or ad personalization is off and the id is all zeros), so it cannot be a test device");
                    if (config.IsTestDevice(deviceAdvertisingId))
                        return new GateResult(GateDecision.Provider, null, null);
                    return new GateResult(GateDecision.Simulator, PolicyRules.UnregisteredDevice,
                        "device '" + (deviceAdvertisingId ?? "") + "' is not in the test device list; add it on the Test tab");
            }
        }

        /// <summary>
        /// The simulator runs only where the gate chose it or, outside a release build, while no mediation is
        /// selected. A blocked gate, or a selected provider that is not in the player (package missing, assembly
        /// stripped), runs nothing: a simulated ad never stands in for a real one, and a release build never simulates.
        /// </summary>
        public static HostMode Host(BuildKind build, GateResult gate, bool mediationSelected, bool providerRegistered)
        {
            if (build != BuildKind.Release && (gate.Decision == GateDecision.Simulator || !mediationSelected)) return HostMode.Simulator;
            return gate.Decision == GateDecision.Provider && mediationSelected && providerRegistered ? HostMode.Provider : HostMode.Idle;
        }
    }
}

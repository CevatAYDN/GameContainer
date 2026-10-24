namespace Nexus.Ads
{
    /// <summary>What the host should run: the real SDK, the simulator, or nothing.</summary>
    public enum GateDecision
    {
        Provider,
        Simulator,
        Blocked,
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
                    if (config.IsTestDevice(deviceAdvertisingId))
                        return new GateResult(GateDecision.Provider, null, null);
                    return new GateResult(GateDecision.Simulator, PolicyRules.UnregisteredDevice,
                        "device '" + (deviceAdvertisingId ?? "") + "' is not in the test device list; add it on the Test tab");
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Nexus.Ads.Unity")]

namespace Nexus.Ads
{
    /// <summary>
    /// Writes the ad failures a release player's log must show (issue #164), once per cause, because a load retries
    /// with a back-off for as long as the game runs: a load that failed or found no fill once per format and reason,
    /// a refused consent once, a consent flow or an SDK start still unanswered after its wait once each (issue #180), a failed
    /// start once until the SDK starts and then one line saying it did, a policy block
    /// once per rule and placement (only with <c>violations</c>: outside a release build the bootstrap writes every
    /// block as an error), every failed show, which only the player's tap starts, every reward receipt written over an unacknowledged
    /// one (issue #178) and every unreadable reward row moved aside (#415), a subscriber or sink that threw once per step and exception (issue #176), and every turn of the remote
    /// kill switch on or off (issue #234), a configuration that changes what runs. Successes are not written.
    /// </summary>
    internal sealed class AdFailureLog
    {
        /// <summary>Distinct causes written in a session; a provider whose reasons carry ids cannot fill the log or the memory.</summary>
        internal const int MaxCauses = 64;

        private const string Prefix = "[Nexus Ads] ";
        private const string Once = " (first this session; repeats are not logged)";

        private readonly Action<string> _write;
        private readonly bool _violations;
        private readonly HashSet<string> _written = new HashSet<string>();
        private bool _startFailing;
        private bool _capped;

        public AdFailureLog(Action<string> write, bool violations)
        {
            _write = write ?? throw new ArgumentNullException(nameof(write));
            _violations = violations;
        }

        public void Record(in AdEvent e)
        {
            switch (e.Kind)
            {
                case AdEventKind.ShowFailed:
                    _write(Prefix + e);
                    return;
                case AdEventKind.InitFailed:
                    if (_startFailing) return;
                    _startFailing = true;
                    _write(Prefix + "InitFailed: " + e.Detail + " (later failed starts are not logged until the SDK starts)");
                    return;
                case AdEventKind.Initialized:
                    if (!_startFailing) return;
                    _startFailing = false;
                    _write(Prefix + "Initialized: " + e.Detail);
                    return;
                case AdEventKind.ConsentRefused:
                    WriteOnce("ConsentRefused", Prefix + "ConsentRefused: " + e.Detail + Once);
                    return;
                case AdEventKind.Unanswered:
                    WriteOnce("Unanswered|" + e.Detail, Prefix + "Unanswered: " + e.Detail + Once);
                    return;
                case AdEventKind.RewardReplaced:
                case AdEventKind.RewardQuarantined:
                    _write(Prefix + e);
                    return;
                case AdEventKind.KillSwitch:
                    _write(Prefix + (e.Detail == "on" ? "KillSwitch on: the remote configuration turned every ad off" : "KillSwitch off: ads load and show again"));
                    return;
                case AdEventKind.HandlerFailed:
                    WriteOnce("HandlerFailed|" + e.Detail, Prefix + e + Once);
                    return;
                case AdEventKind.LoadFailed:
                case AdEventKind.NoFill:
                    WriteOnce(e.Kind + "|" + e.Format + "|" + e.Detail, Prefix + e + Once);
                    return;
                case AdEventKind.Violation:
                    if (!_violations) return;
                    string detail = e.Detail ?? "";
                    int colon = detail.IndexOf(": ", StringComparison.Ordinal);
                    string rule = colon < 0 ? detail : detail.Substring(0, colon);
                    WriteOnce("Violation|" + rule + "|" + e.Placement,
                        Prefix + "PolicyViolation " + rule + " (" + e.Format + " '" + e.Placement + "')" + (colon < 0 ? "" : detail.Substring(colon))
                        + " (first for this rule and placement this session; repeats are not logged)");
                    return;
            }
        }

        private void WriteOnce(string cause, string line)
        {
            if (_capped || _written.Contains(cause)) return;
            if (_written.Count == MaxCauses)
            {
                _capped = true;
                _write(Prefix + MaxCauses + " distinct ad failures were logged this session; new ones are not logged");
                return;
            }
            _written.Add(cause);
            _write(line);
        }
    }
}

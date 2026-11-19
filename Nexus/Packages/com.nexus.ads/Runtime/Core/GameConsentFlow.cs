using System;
using System.Collections.Generic;
using System.Globalization;

namespace Nexus.Ads
{
    /// <summary>
    /// What the game-drawn consent screen says (issue #430). The game fills it from its own localization; the
    /// flow asks for it each time the screen opens, so a language changed in the menu applies to the next screen.
    /// </summary>
    public sealed class ConsentTexts
    {
        public string Title = "";
        public string Body = "";
        public string Accept = "";
        public string Reject = "";
        public string PrivacyPolicyLabel = "";
        public string PrivacyPolicyUrl = "";
        public string PartnersHeading = "";
        public IReadOnlyList<string> Partners = Array.Empty<string>();
    }

    /// <summary>
    /// Draws the screen for <see cref="GameConsentFlow"/>. The screen carries no default answer and nothing closes it
    /// without a choice: <paramref name="onChoice"/> runs once, with true for Accept and false for Reject.
    /// </summary>
    public interface IConsentScreen
    {
        void Show(ConsentTexts texts, Action<bool> onChoice);
        /// <summary>Removes the screen without an answer (the flow was disposed).</summary>
        void Hide();
    }

    /// <summary>
    /// The player's answer, stored per device with the version of the question. An answer given under another version
    /// reads as no answer, so new partners or new texts ask again. The same store backs the flow and the analytics
    /// reader, so both always agree.
    /// </summary>
    public sealed class ConsentAnswerStore
    {
        public const string Key = "nexus.ads.consent.game";

        private readonly IPacingStore _store;

        public ConsentAnswerStore(IPacingStore store, int version)
        {
            if (version < 1) throw new ArgumentOutOfRangeException(nameof(version), "the question's version starts at 1");
            _store = store ?? throw new ArgumentNullException(nameof(store));
            Version = version;
        }

        public int Version { get; }

        /// <summary>Raised after <see cref="Write"/>.</summary>
        public event Action Changed;

        /// <summary>True when the player answered this version; <paramref name="accepted"/> is that answer.</summary>
        public bool TryRead(out bool accepted)
        {
            accepted = false;
            string raw = _store.Get(Key);
            if (string.IsNullOrEmpty(raw)) return false;
            int split = raw.IndexOf(':');
            if (split <= 0 || split != raw.Length - 2) return false;
            if (!int.TryParse(raw.Substring(0, split), NumberStyles.None, CultureInfo.InvariantCulture, out int version) || version != Version) return false;
            char answer = raw[raw.Length - 1];
            if (answer != '1' && answer != '0') return false;
            accepted = answer == '1';
            return true;
        }

        public void Write(bool accepted)
        {
            _store.Set(Key, Version.ToString(CultureInfo.InvariantCulture) + (accepted ? ":1" : ":0"));
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// The consent flow of a game that draws its own screen (<c>Consent: GameFlow</c>, issue #430): no AdMob account and no
    /// UMP message. The first launch shows the screen; later launches apply the stored answer without it. The answer is
    /// forwarded to the mediation through <c>apply</c> before <see cref="Request"/> completes, so it reaches the SDK before the
    /// SDK starts. Reject still lets ads load (non-personalised), so <see cref="CanRequestAds"/> is true after either answer.
    /// There is no region detection: without UMP there is no reliable EEA/UK signal, so every player is asked once.
    /// The game decides when <see cref="Request"/> runs (its own start of the ad family), and the flow never shows over gameplay
    /// by itself. <see cref="ShowPrivacyOptions"/> reopens the screen; the new answer is applied at once and raises
    /// <see cref="Changed"/>.
    /// </summary>
    public sealed class GameConsentFlow : IConsentFlow, IDisposable
    {
        private readonly ConsentAnswerStore _answers;
        private readonly IConsentScreen _screen;
        private readonly Func<ConsentTexts> _texts;
        private readonly Action<bool> _apply;
        private readonly bool _noPersonalisation;
        private bool _requested, _answered, _showing, _disposed;
        private Action _onAnswered, _onClosed;

        /// <param name="answers">Where the answer lives.</param>
        /// <param name="screen">Draws the screen.</param>
        /// <param name="texts">The screen's texts, read each time it opens.</param>
        /// <param name="noPersonalisation">A child-directed or under-age build: no screen, no answer stored, and <c>apply(false)</c>
        /// at every start, because personalised ads are not allowed whatever a player could tap.</param>
        /// <param name="apply">Forwards the answer to the mediation (true: accepted); for LevelPlay <c>LevelPlayProvider.ApplyConsent</c>.</param>
        public GameConsentFlow(ConsentAnswerStore answers, IConsentScreen screen, Func<ConsentTexts> texts, Action<bool> apply, bool noPersonalisation = false)
        {
            _noPersonalisation = noPersonalisation;
            _answers = answers ?? throw new ArgumentNullException(nameof(answers));
            _screen = screen ?? throw new ArgumentNullException(nameof(screen));
            _texts = texts ?? throw new ArgumentNullException(nameof(texts));
            _apply = apply ?? throw new ArgumentNullException(nameof(apply));
        }

        public bool Completed => !_disposed && _answered;
        public bool CanRequestAds => Completed;
        /// <summary>True once the player answered: the game's "Ad privacy options" entry then reopens the screen.</summary>
        public bool PrivacyOptionsRequired => Completed && !_noPersonalisation;
        public event Action Changed;

        public void Request(Action onCompleted)
        {
            if (_disposed) return;
            if (_answered) { onCompleted?.Invoke(); return; }
            _onAnswered += onCompleted;
            if (_requested) return;
            _requested = true;
            if (_noPersonalisation)
            {
                _apply(false);
                Complete();
                return;
            }
            if (_answers.TryRead(out bool stored))
            {
                _apply(stored);
                Complete();
                return;
            }
            Ask(accepted =>
            {
                _answers.Write(accepted);
                _apply(accepted);
                Complete();
                var closed = _onClosed; // an options request that arrived while the first screen was open
                _onClosed = null;
                closed?.Invoke();
                Changed?.Invoke();
            });
        }

        public void ShowPrivacyOptions(Action onClosed)
        {
            if (_disposed) return;
            if (_noPersonalisation) { onClosed?.Invoke(); return; }
            _onClosed += onClosed;
            // a screen already open answers every waiting caller
            if (_showing) return;
            Ask(accepted =>
            {
                _answers.Write(accepted);
                _apply(accepted);
                var closed = _onClosed;
                _onClosed = null;
                closed?.Invoke();
                Changed?.Invoke();
            });
        }

        private void Ask(Action<bool> onAnswer)
        {
            _showing = true;
            bool done = false;
            _screen.Show(_texts(), accepted =>
            {
                if (done || _disposed) return;
                done = true;
                _showing = false;
                onAnswer(accepted);
            });
        }

        private void Complete()
        {
            if (_disposed || _answered) return;
            _answered = true;
            var callback = _onAnswered;
            _onAnswered = null;
            callback?.Invoke();
        }

        void IDisposable.Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _onAnswered = null;
            _onClosed = null;
            Changed = null;
            if (_showing) _screen.Hide();
            _showing = false;
        }
    }
}

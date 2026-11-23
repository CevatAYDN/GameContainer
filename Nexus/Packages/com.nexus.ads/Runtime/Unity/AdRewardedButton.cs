using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace Nexus.Ads.Unity
{
    /// <summary>What the button looks like while its placement cannot show (#440).</summary>
    public enum NotReadyBehavior
    {
        /// <summary>Invisible, not interactable, and it lets taps through. The behaviour before #440.</summary>
        Hide = 0,
        /// <summary>Visible but dimmed and not interactable, with the optional not-ready label shown, so the player and a
        /// reviewer can see the rewarded option exists and why it cannot be used right now.</summary>
        ShowDisabled = 1,
    }

    /// <summary>How a button presents itself for one state; a plain value so it can be tested without a scene.</summary>
    public readonly struct RewardedButtonPresentation
    {
        public readonly float Alpha;
        public readonly bool Interactable;
        public readonly bool BlocksRaycasts;
        /// <summary>True only when the placement is not ready and the button is shown: not while an ad is on screen.</summary>
        public readonly bool ShowNotReadyLabel;

        public RewardedButtonPresentation(float alpha, bool interactable, bool blocksRaycasts, bool showNotReadyLabel)
        { Alpha = alpha; Interactable = interactable; BlocksRaycasts = blocksRaycasts; ShowNotReadyLabel = showNotReadyLabel; }
    }

    /// <summary>
    /// A rewarded-ad button: the game never offers a reward it cannot pay. Put it on the Button; the
    /// GameObject stays active and a CanvasGroup changes it, so it can come back by itself when an ad loads.
    /// While nothing can show it either hides (<see cref="NotReadyBehavior.Hide"/>) or stays visible and disabled with
    /// an optional label (<see cref="NotReadyBehavior.ShowDisabled"/>, #440). While its own ad is on screen it is
    /// never interactable. The button's own label states the reward and the amount (a policy requirement the
    /// component cannot check).
    /// </summary>
    [RequireComponent(typeof(Button))]
    [RequireComponent(typeof(CanvasGroup))]
    public sealed class AdRewardedButton : MonoBehaviour
    {
        [Tooltip("A rewarded placement name from the generated AdPlacements constants")]
        [SerializeField] private string _placement = "";
        [Tooltip("What the button does while no ad can show. Hide keeps the behaviour of earlier versions; ShowDisabled keeps it visible, dimmed and not interactable. A button added in the editor starts on ShowDisabled; one saved by an older version stays on Hide.")]
        [SerializeField] private NotReadyBehavior _whenNotReady = NotReadyBehavior.Hide;
        [Tooltip("Optional child object (for example a Text/TextMeshPro or localised label reading \"No ad available right now\") that is active only while the button is shown because no ad is ready. Leave empty for a dimmed button without text.")]
        [SerializeField] private GameObject _notReadyLabel;
        [SerializeField] private UnityEvent _onRewarded = new UnityEvent();
        [SerializeField] private UnityEvent _onNotRewarded = new UnityEvent();

        private Button _button;
        private CanvasGroup _group;
        private System.Action _unsubscribe;
        private bool _inFlight;

        public string Placement { get => _placement; set { _placement = value; Refresh(); } }
        /// <summary>The wording for the not-ready state; use it as the default text or the localisation key's source string.</summary>
        public const string NotReadyText = "No ad available right now";
        public NotReadyBehavior WhenNotReady { get => _whenNotReady; set { _whenNotReady = value; Refresh(); } }
        public UnityEvent OnRewarded => _onRewarded;
        public UnityEvent OnNotRewarded => _onNotRewarded;

        // Reset runs only when the component is added or reset in the editor, never for a scene or prefab saved earlier,
        // so new buttons get the visible state and existing games do not change by surprise (#440).
        private void Reset() => _whenNotReady = NotReadyBehavior.ShowDisabled;

        private void Awake()
        {
            _button = GetComponent<Button>();
            _group = GetComponent<CanvasGroup>();
            _button.onClick.AddListener(Click);
        }

        private void OnEnable()
        {
            _unsubscribe = Ads.OnReadyChanged(Refresh);
            Refresh();
        }

        private void OnDisable()
        {
            _unsubscribe?.Invoke();
            _unsubscribe = null;
        }

        private void Refresh()
        {
            // IsReady is asked only while no show runs: during a show the button is not ready by definition.
            var view = Present(_inFlight, !_inFlight && Ads.IsReady(_placement), _whenNotReady);
            _group.alpha = view.Alpha;
            _group.interactable = view.Interactable;
            _group.blocksRaycasts = view.BlocksRaycasts;
            if (_notReadyLabel != null) _notReadyLabel.SetActive(view.ShowNotReadyLabel);
        }

        /// <summary>The state table: ready is fully shown; otherwise Hide hides it and ShowDisabled dims it, labelled
        /// unless the not-ready cause is this button's own ad being on screen.</summary>
        public static RewardedButtonPresentation Present(bool inFlight, bool ready, NotReadyBehavior whenNotReady)
        {
            if (ready && !inFlight) return new RewardedButtonPresentation(1f, true, true, false);
            if (whenNotReady == NotReadyBehavior.Hide) return new RewardedButtonPresentation(0f, false, false, false);
            return new RewardedButtonPresentation(0.5f, false, true, !inFlight);
        }

        private void Click()
        {
            if (_inFlight) return;
            _inFlight = true;
            Refresh();
            var controller = AdsBootstrap.Instance != null ? AdsBootstrap.Instance.Controller : null;
            Ads.Show(_placement, result =>
            {
                _inFlight = false;
                if (result == AdResult.Rewarded)
                {
                    var receipt = controller?.DeliveringReward;
                    _onRewarded.Invoke();
                    // A listener can deliver a newer reward; acknowledge only this callback's captured receipt.
                    if (receipt.HasValue) controller.AcknowledgeReward(receipt.Value.Id);
                }
                else _onNotRewarded.Invoke();
                Refresh();
            });
        }
    }
}

using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace Nexus.Ads.Unity
{
    /// <summary>
    /// A rewarded-ad button that is visible only while its placement can show: the game never offers
    /// a reward it cannot pay. Put it on the Button; the GameObject stays active and a CanvasGroup
    /// hides it, so it can come back by itself when an ad loads. The button's own label states the
    /// reward and the amount (a policy requirement the component cannot check).
    /// </summary>
    [RequireComponent(typeof(Button))]
    [RequireComponent(typeof(CanvasGroup))]
    public sealed class AdRewardedButton : MonoBehaviour
    {
        [Tooltip("A rewarded placement name from the generated AdPlacements constants")]
        [SerializeField] private string _placement = "";
        [SerializeField] private UnityEvent _onRewarded = new UnityEvent();
        [SerializeField] private UnityEvent _onNotRewarded = new UnityEvent();

        private Button _button;
        private CanvasGroup _group;
        private System.Action _unsubscribe;
        private bool _inFlight;

        public string Placement { get => _placement; set { _placement = value; Refresh(); } }
        public UnityEvent OnRewarded => _onRewarded;
        public UnityEvent OnNotRewarded => _onNotRewarded;

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
            bool ready = !_inFlight && Ads.IsReady(_placement);
            _group.alpha = ready ? 1f : 0f;
            _group.interactable = ready;
            _group.blocksRaycasts = ready;
        }

        private void Click()
        {
            if (_inFlight) return;
            _inFlight = true;
            Refresh();
            Ads.Show(_placement, result =>
            {
                _inFlight = false;
                if (result == AdResult.Rewarded) _onRewarded.Invoke(); else _onNotRewarded.Invoke();
                Refresh();
            });
        }
    }
}

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace Nexus.Core.Services
{
    /// <summary>
    /// UI Toolkit input through the <see cref="EventSystem"/> without uGUI's allocation on every frame (ADR-0003, decision
    /// 7). Add it to the EventSystem's GameObject. uGUI's bridge retries each panel's event objects on every frame and
    /// allocates 32 B per panel doing it (uGUI 2.6.0); this component turns the bridge's
    /// <see cref="PanelInputConfiguration.autoCreatePanelComponents"/> off, adding a <see cref="PanelInputConfiguration"/>
    /// when the scene has none, and gives each panel one GameObject with <see cref="PanelEventHandler"/> and
    /// <see cref="PanelRaycaster"/> itself, once.
    /// <para>
    /// It finds the panels of the <see cref="UIDocument"/>s in each scene that loads, of every <see cref="ToolkitScreen"/>,
    /// and of the documents passed to <see cref="Attach"/>. A document created after its scene loaded that is not a
    /// screen's, on a <see cref="PanelSettings"/> no other document uses, is passed to <see cref="Attach"/>, or its panel
    /// gets no EventSystem input. Disabled, the component gives the configuration its previous value back.
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ToolkitPanelInput : MonoBehaviour
    {
        private static ToolkitPanelInput s_current;

        private readonly List<UIDocument> _pending = new List<UIDocument>();
        // By identity, not by parent: the bridge puts the objects it makes under the EventSystem too.
        private readonly HashSet<GameObject> _made = new HashSet<GameObject>();
        private PanelInputConfiguration _configuration;
        private bool _previousAutoCreate;

        /// <summary>The enabled component, or null.</summary>
        internal static ToolkitPanelInput Current => s_current != null ? s_current : null;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatic() => s_current = null;

        /// <summary>Gives the document's panel its event objects, now or on the first frame the panel is there.</summary>
        public void Attach(UIDocument document)
        {
            if (document == null || _pending.Contains(document)) return;
            if (!Take(document.runtimePanel)) _pending.Add(document);
        }

        private void OnEnable()
        {
            s_current = this;
            _configuration = FindAnyObjectByType<PanelInputConfiguration>();
            if (_configuration == null)
            {
                _configuration = gameObject.AddComponent<PanelInputConfiguration>();
                _configuration.panelInputRedirection = PanelInputConfiguration.PanelInputRedirection.AutoSwitch;
            }
            // shortcut: world-space panels get no event objects here (no game has one), add them when one does.
            _previousAutoCreate = _configuration.autoCreatePanelComponents;
            _configuration.autoCreatePanelComponents = false;
            SceneManager.sceneLoaded += OnSceneLoaded;
            AttachSceneDocuments();
        }

        private void OnDisable()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            if (_configuration != null) _configuration.autoCreatePanelComponents = _previousAutoCreate;
            _configuration = null;
            _pending.Clear();
            if (s_current == this) s_current = null;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode) => AttachSceneDocuments();

        private void AttachSceneDocuments()
        {
            foreach (var document in FindObjectsByType<UIDocument>(FindObjectsInactive.Include))
                Attach(document);
        }

        private void LateUpdate()
        {
            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                var document = _pending[i];
                if (document == null || Take(document.runtimePanel)) _pending.RemoveAt(i);
            }
        }

        /// <summary>The event objects are this component's.</summary>
        internal bool Owns(GameObject events) => events != null && _made.Contains(events);

        /// <summary>True when the panel has this component's event objects, made now or before.</summary>
        private bool Take(IRuntimePanel panel)
        {
            if (panel == null) return false;
            var current = panel.selectableGameObject;
            // The bridge drops its own objects as the configuration turns it off, so other objects here are the game's,
            // or the bridge's under another configuration that turned it on again: the panel keeps them.
            if (current != null) return Owns(current);
            var events = new GameObject(panel.panelSettings != null ? panel.panelSettings.name : "Panel",
                typeof(PanelEventHandler), typeof(PanelRaycaster));
            events.transform.SetParent(transform, false);
            _made.Add(events);
            events.GetComponent<PanelEventHandler>().panel = panel;
            events.GetComponent<PanelRaycaster>().panel = panel;
            panel.selectableGameObject = events;
            return true;
        }
    }
}

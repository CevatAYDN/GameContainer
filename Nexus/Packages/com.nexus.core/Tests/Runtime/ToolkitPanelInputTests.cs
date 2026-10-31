using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using Nexus.Core;
using Nexus.Core.Services;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace Nexus.Tests
{
    /// <summary>
    /// Issue #76 (ADR-0003 decision 7): <see cref="ToolkitPanelInput"/> turns uGUI's per-frame panel objects off and gives
    /// each panel its EventSystem event objects once, so the EventSystem's frame allocates nothing and every panel still
    /// gets input: the scene's, a screen's made later, one whose bridge objects came first, and one after a scene change.
    /// </summary>
    [TestFixture]
    [Category("RequiresPlayMode")]
    public sealed class ToolkitPanelInputTests
    {
        private readonly List<GameObject> _objects = new List<GameObject>();
        private readonly List<Object> _assets = new List<Object>();

        [SetUp]
        public void SetUp() => ScreenStackTests.Cleanup();

        [TearDown]
        public void TearDown()
        {
            ScreenStackTests.Cleanup();
            foreach (var go in _objects) if (go != null) Object.DestroyImmediate(go);
            foreach (var asset in _assets) if (asset != null) Object.DestroyImmediate(asset);
            _objects.Clear();
            _assets.Clear();
        }

        private EventSystem CreateEventSystem()
        {
            var go = new GameObject("EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));
            _objects.Add(go);
            return go.GetComponent<EventSystem>();
        }

        private PanelSettings CreatePanel()
        {
            var panel = ScriptableObject.CreateInstance<PanelSettings>();
            panel.themeStyleSheet = ScriptableObject.CreateInstance<ThemeStyleSheet>();
            _assets.Add(panel);
            _assets.Add(panel.themeStyleSheet);
            return panel;
        }

        /// <summary>A document whose root fills the panel and can be picked, so a raycast at the centre reaches it.</summary>
        private UIDocument CreateDocument(PanelSettings panel)
        {
            var go = new GameObject("Document");
            _objects.Add(go);
            var document = go.AddComponent<UIDocument>();
            document.panelSettings = panel;
            document.rootVisualElement.style.flexGrow = 1;
            var surface = new VisualElement { pickingMode = PickingMode.Position };
            surface.style.position = Position.Absolute;
            surface.style.left = surface.style.top = surface.style.right = surface.style.bottom = 0;
            document.rootVisualElement.Add(surface);
            return document;
        }

        private static GameObject EventObjects(UIDocument document) => document.runtimePanel?.selectableGameObject;

        // Owns, not the parent: the bridge's objects sit under the EventSystem too.
        private static bool MadeBy(ToolkitPanelInput input, GameObject events) =>
            input.Owns(events) && events.GetComponent<PanelEventHandler>() != null && events.GetComponent<PanelRaycaster>() != null;

        private static GameObject RaycastAtCentre(EventSystem eventSystem)
        {
            var hits = new List<RaycastResult>();
            eventSystem.RaycastAll(new PointerEventData(eventSystem) { position = new Vector2(Screen.width / 2f, Screen.height / 2f) }, hits);
            return hits.Count > 0 ? hits[0].gameObject : null;
        }

        private static Action Method(object target, string name) => (Action)Delegate.CreateDelegate(typeof(Action), target,
            target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public));

        [UnityTest]
        public IEnumerator TheDocumentsOfOnePanelSettings_ShareOneEventObject_ThatTheEventSystemsRaycastHits()
        {
            var eventSystem = CreateEventSystem();
            var shared = CreatePanel();
            var first = CreateDocument(shared);
            var second = CreateDocument(shared);
            var other = CreateDocument(CreatePanel());
            var input = eventSystem.gameObject.AddComponent<ToolkitPanelInput>();
            yield return ScreenStackTests.WaitUntil(() => MadeBy(input, EventObjects(first)) && MadeBy(input, EventObjects(other)),
                "both panels have the component's event objects");

            Assert.That(EventObjects(second), Is.SameAs(EventObjects(first)));
            Assert.That(EventObjects(other), Is.Not.SameAs(EventObjects(first)));
            Assert.That(eventSystem.GetComponent<PanelInputConfiguration>().autoCreatePanelComponents, Is.False);
            Object.DestroyImmediate(other.gameObject);
            yield return null;
            Assert.That(RaycastAtCentre(eventSystem), Is.SameAs(EventObjects(first)));
        }

        [UnityTest]
        public IEnumerator TheEventSystemsFrame_AllocatesNothing_OnceTheComponentHasTakenThePanelFromTheBridge()
        {
            var eventSystem = CreateEventSystem();
            var document = CreateDocument(CreatePanel());
            // The bridge makes the panel's objects first and retries them on every frame from then on.
            yield return ScreenStackTests.WaitUntil(() => EventObjects(document) != null, "the bridge made the panel's objects");
            var bridges = EventObjects(document);
            var input = eventSystem.gameObject.AddComponent<ToolkitPanelInput>();
            yield return ScreenStackTests.WaitUntil(() => MadeBy(input, EventObjects(document)), "the component took the panel");

            Assert.That(bridges == null, Is.True, "the bridge destroyed the objects it made");
            Assert.That(RaycastAtCentre(eventSystem), Is.SameAs(EventObjects(document)));
            var eventSystemFrame = Method(eventSystem, "Update");
            var inputFrame = Method(input, "LateUpdate");
            eventSystemFrame();
            inputFrame();
            long allocations;
            using (var probe = new GcAllocationProbe())
            {
                for (int frame = 0; frame < 10; frame++)
                {
                    eventSystemFrame();
                    inputFrame();
                }
                allocations = probe.Stop();
            }
            Assert.That(allocations, Is.Zero, "the EventSystem's frame allocates");
        }

        [UnityTest]
        public IEnumerator AScreenOnAPanelSettingsOfItsOwn_MadeAfterTheComponent_GetsItsPanelsEventObjects()
        {
            var eventSystem = CreateEventSystem();
            var input = eventSystem.gameObject.AddComponent<ToolkitPanelInput>();
            yield return null;
            var root = ScreenStackTests.CreateRoot("PanelInput", null, ScreenStackTests.BindStack);
            yield return ScreenStackTests.WaitUntil(() => root.IsInitialized, "the context started");
            var screen = ScreenStackTests.AddScreen<ScreenStackTests.MenuScreen>(root, CreatePanel());
            var document = screen.GetComponent<UIDocument>();
            yield return ScreenStackTests.WaitUntil(() => MadeBy(input, EventObjects(document)), "the screen's panel has the component's objects");
        }

        [UnityTest]
        public IEnumerator Disabled_TheComponentGivesTheConfigurationBack_SoTheBridgeServesANewPanel()
        {
            var eventSystem = CreateEventSystem();
            var input = eventSystem.gameObject.AddComponent<ToolkitPanelInput>();
            yield return null;
            input.enabled = false;
            var configuration = eventSystem.GetComponent<PanelInputConfiguration>();
            Assert.That(configuration.autoCreatePanelComponents, Is.True);
            var document = CreateDocument(CreatePanel());
            yield return ScreenStackTests.WaitUntil(() => EventObjects(document) != null, "the bridge made the new panel's objects");
            Assert.That(MadeBy(input, EventObjects(document)), Is.False);
        }

        [UnityTest]
        public IEnumerator APanelWhoseEventObjectsWentWithTheLastScenesEventSystem_GetsNewOnesFromTheNext()
        {
            var document = CreateDocument(CreatePanel());
            var first = CreateEventSystem();
            first.gameObject.AddComponent<ToolkitPanelInput>();
            yield return ScreenStackTests.WaitUntil(() => EventObjects(document) != null, "the first component took the panel");
            Object.DestroyImmediate(first.gameObject);
            Assert.That(EventObjects(document) == null, Is.True, "the panel's objects went with their EventSystem");

            var next = CreateEventSystem();
            var input = next.gameObject.AddComponent<ToolkitPanelInput>();
            yield return ScreenStackTests.WaitUntil(() => MadeBy(input, EventObjects(document)), "the next component took the panel");
            Assert.That(RaycastAtCentre(next), Is.SameAs(EventObjects(document)));
        }
    }
}

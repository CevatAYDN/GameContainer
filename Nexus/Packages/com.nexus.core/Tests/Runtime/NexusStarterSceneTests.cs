#if UNITY_EDITOR
using System.Collections;
using System.IO;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Nexus.Tests.Runtime
{
    /// <summary>
    /// The development project's starter scene, which the Setup Wizard generates (#193 phase 7): its GameView builds a
    /// UI Toolkit label and button when it binds, a click fires GameSignal, GameCommand raises the model, and the
    /// mediator shows the new value. The scene is the canonical example the Android player of gates 9 and 12 runs.
    /// </summary>
    public sealed class NexusStarterSceneTests
    {
        private const string ScenePath = "Assets/Scenes/NexusStarter.unity";
        private Scene _scene;

        [UnityTearDown]
        public IEnumerator UnloadTheScene()
        {
            if (_scene.IsValid() && _scene.isLoaded) yield return SceneManager.UnloadSceneAsync(_scene);
        }

        [UnityTest]
        public IEnumerator TheStarterScene_ShowsTheCounter_AndCountsAClickOnItsButton()
        {
            Assert.IsTrue(File.Exists(ScenePath), "the development project's starter scene");
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode(ScenePath, new LoadSceneParameters(LoadSceneMode.Additive));
            _scene = SceneManager.GetSceneByPath(ScenePath);

            UIDocument document = null;
            foreach (var go in _scene.GetRootGameObjects())
                if (go.TryGetComponent(out document)) break;
            Assert.IsNotNull(document, "the GamePanel's UIDocument");

            Button button = null;
            float until = Time.realtimeSinceStartup + 5f; // the context starts asynchronously; Root gives a sibling 15 s
            while ((button = document.rootVisualElement?.Q<Button>()) == null && Time.realtimeSinceStartup < until) yield return null;
            Assert.IsNotNull(button, "the GameView bound and built its button");
            var counter = document.rootVisualElement.Q<Label>();
            Assert.AreEqual("Counter: 0", counter.text);

            using (var submit = NavigationSubmitEvent.GetPooled())
            {
                submit.target = button;
                button.SendEvent(submit);
            }
            until = Time.realtimeSinceStartup + 2f;
            while (counter.text == "Counter: 0" && Time.realtimeSinceStartup < until) yield return null;

            Assert.AreEqual("Counter: 1", counter.text);
        }
    }
}
#endif

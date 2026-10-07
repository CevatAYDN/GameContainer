using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Nexus.Core;
using Nexus.Core.Components;
using Nexus.Editor.Inspector;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Nexus.Editor.Tests
{
    [Serializable] public struct InspectorRegressionSetting { public int Value; }
    public class InspectorRegressionData : ContextData { public InspectorRegressionSetting ExtraSetting; }
    public class InspectorRegressionRoot : Root { public InspectorRegressionSetting ExtraSetting; }
    public class InspectorRegressionScope : NexusLifetimeScope { public InspectorRegressionSetting ExtraSetting; }
    public class InspectorRegressionTarget : MonoBehaviour { [Inject] public InspectorRegressionService Service; }
    public class InspectorRegressionService { }

    [CustomPropertyDrawer(typeof(InspectorRegressionSetting))]
    public class InspectorRegressionSettingDrawer : PropertyDrawer
    {
        public static int Draws;
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            Draws++;
            EditorGUI.PropertyField(position, property.FindPropertyRelative("Value"), label);
        }
    }

    public class InspectorRegressionWindow : EditorWindow
    {
        public UnityEditor.Editor Inspector;
        public Exception Failure;
        private void OnGUI()
        {
            if (Inspector == null) return;
            try { Inspector.OnInspectorGUI(); }
            catch (Exception error) { Failure = error; }
        }
    }

    public class LatestCommitEditorRegressionTests
    {
        [Test]
        public void ContextDataInspector_BindsActualSerializedFieldsAndPreservesUndo()
        {
            var data = ScriptableObject.CreateInstance<ContextData>();
            var editor = UnityEditor.Editor.CreateEditor(data);
            Undo.IncrementCurrentGroup();
            try
            {
                foreach (string field in new[] { "_scopeTagProp", "_enableAutoDiscoveryProp", "_assemblyScopesProp" })
                    Assert.That(typeof(ContextDataEditor).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(editor), Is.Not.Null, field);
                editor.serializedObject.FindProperty(nameof(ContextData.ScopeTag)).stringValue = "Regression";
                editor.serializedObject.FindProperty(nameof(ContextData.EnableAutoDiscovery)).boolValue = false;
                var scopes = editor.serializedObject.FindProperty(nameof(ContextData.AssemblyScopes));
                scopes.arraySize = 1;
                scopes.GetArrayElementAtIndex(0).stringValue = "Game.Assembly";
                editor.serializedObject.ApplyModifiedProperties();
                Assert.That(data.ScopeTag, Is.EqualTo("Regression"));
                Assert.That(data.EnableAutoDiscovery, Is.False);
                Assert.That(data.AssemblyScopes, Is.EqualTo(new[] { "Game.Assembly" }));
                Undo.FlushUndoRecordObjects();
                Undo.PerformUndo();
                Assert.That(data.ScopeTag, Is.Null.Or.Empty);
                Assert.That(data.EnableAutoDiscovery, Is.True);
            }
            finally
            {
                Undo.ClearUndo(data);
                UnityEngine.Object.DestroyImmediate(editor);
                UnityEngine.Object.DestroyImmediate(data);
            }
        }

        [UnityTest]
        public IEnumerator Inspectors_DrawAdditionalNestedSerializedSettings()
        {
            if (Application.isBatchMode && SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
                Assert.Ignore("IMGUI rendering requires an Editor graphics surface.");
            var data = ScriptableObject.CreateInstance<InspectorRegressionData>();
            var go = new GameObject("Inspector regression");
            var root = go.AddComponent<InspectorRegressionRoot>();
            var scopeGo = new GameObject("Scope inspector regression");
            var scope = scopeGo.AddComponent<InspectorRegressionScope>();
            var objects = new UnityEngine.Object[] { data, root, scope };
            var editorTypes = new[] { typeof(ContextDataEditor), typeof(RootEditor), typeof(NexusLifetimeScopeEditor) };
            InspectorRegressionWindow window = null;
            UnityEditor.Editor editor = null;
            try
            {
                for (int i = 0; i < objects.Length; i++)
                {
                    editor = UnityEditor.Editor.CreateEditor(objects[i], editorTypes[i]);
                    Assert.That(editor.serializedObject.FindProperty("ExtraSetting.Value"), Is.Not.Null);
                    InspectorRegressionSettingDrawer.Draws = 0;
                    window = ScriptableObject.CreateInstance<InspectorRegressionWindow>();
                    window.Inspector = editor;
                    window.Show();
                    for (int frame = 0; frame < 10 && InspectorRegressionSettingDrawer.Draws == 0 && window.Failure == null; frame++)
                    {
                        window.Repaint();
                        yield return null;
                    }
                    Assert.That(window.Failure, Is.Null, editorTypes[i].Name);
                    Assert.That(InspectorRegressionSettingDrawer.Draws, Is.GreaterThan(0), editorTypes[i].Name);
                    window.Close();
                    window = null;
                    UnityEngine.Object.DestroyImmediate(editor);
                    editor = null;
                }
            }
            finally
            {
                if (window != null) window.Close();
                if (editor != null) UnityEngine.Object.DestroyImmediate(editor);
                UnityEngine.Object.DestroyImmediate(scopeGo);
                UnityEngine.Object.DestroyImmediate(go);
                UnityEngine.Object.DestroyImmediate(data);
            }
        }

        [TestCase(InjectionScope.Self, false)]
        [TestCase(InjectionScope.Children, false)]
        [TestCase(InjectionScope.Hierarchy, false)]
        [TestCase(InjectionScope.Self, true)]
        [TestCase(InjectionScope.Children, true)]
        [TestCase(InjectionScope.Hierarchy, true)]
        public void BindingPreview_MatchesActualInjectionTargets(InjectionScope scope, bool custom)
        {
            var data = ScriptableObject.CreateInstance<ContextData>();
            data.EnableAutoDiscovery = false;
            var go = new GameObject("Binding self");
            var child = new GameObject("Inactive child");
            child.transform.SetParent(go.transform);
            child.SetActive(false);
            var remote = new GameObject("Custom target");
            var context = ContextFactory.Create(contextData: data);
            try
            {
                var service = new InspectorRegressionService();
                context.Container.BindInstance(service);
                context.Configure();
                var binding = go.AddComponent<NexusBinding>();
                binding.Scope = scope;
                var targets = new[] { go.AddComponent<InspectorRegressionTarget>(), child.AddComponent<InspectorRegressionTarget>(), remote.AddComponent<InspectorRegressionTarget>() };
                if (custom)
                {
                    var serialized = new SerializedObject(binding);
                    var array = serialized.FindProperty("_customTargets");
                    array.arraySize = 2;
                    array.GetArrayElementAtIndex(0).objectReferenceValue = targets[2];
                    array.GetArrayElementAtIndex(1).objectReferenceValue = null;
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                }
                var preview = NexusBindingEditor.DiscoverInjectables(binding).Select(item => item.Component).ToArray();
                binding.InjectNow(context);
                CollectionAssert.AreEquivalent(targets.Where(target => ReferenceEquals(target.Service, service)).ToArray(), preview);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(remote);
                UnityEngine.Object.DestroyImmediate(go);
                context.Dispose();
                UnityEngine.Object.DestroyImmediate(data);
            }
        }

        [Test]
        public void HierarchyMenus_KeepGlobalRootTopLevelAndUndoCreatedScope()
        {
            var parent = new GameObject("Selected parent");
            GameObject global = null;
            GameObject child = null;
            try
            {
                NexusHierarchyMenus.CreateGlobalProjectRoot(new MenuCommand(parent));
                global = Selection.activeGameObject;
                Assert.That(global.transform.parent, Is.Null);
                Assert.That(global.GetComponent<Root>().IsGlobalContext, Is.True);
                Undo.IncrementCurrentGroup();
                NexusHierarchyMenus.CreateLifetimeScope(new MenuCommand(parent));
                child = Selection.activeGameObject;
                Assert.That(child.transform.parent, Is.EqualTo(parent.transform));
                Assert.That(child.GetComponent<NexusLifetimeScope>(), Is.Not.Null);
                Undo.PerformUndo();
                Assert.That(child == null, Is.True, "Created scope should be removed by Undo.");
            }
            finally
            {
                if (child != null) UnityEngine.Object.DestroyImmediate(child);
                if (global != null) { Undo.ClearUndo(global); UnityEngine.Object.DestroyImmediate(global); }
                UnityEngine.Object.DestroyImmediate(parent);
            }
        }
    }

    public class LatestCommitEditorCreationLifecycleTests
    {
        [UnityTearDown]
        public IEnumerator LeavePlayModeAfterFailureOrSuccess()
        {
            if (Application.isPlaying) yield return new ExitPlayMode();
        }

        [UnityTest]
        public IEnumerator MenuCreation_ConfiguresDataAndHierarchyBeforeAwake()
        {
            yield return new EnterPlayMode();
            var data = ScriptableObject.CreateInstance<ContextData>();
            data.ScopeTag = "EditorMenuRegression";
            data.EnableAutoDiscovery = false;
            var parentGo = new GameObject("Parent root");
            parentGo.SetActive(false);
            var parent = parentGo.AddComponent<Root>();
            parent.SetUp(data);
            parentGo.SetActive(true);
            GameObject child = null;
            Root configured = null;
            GameObject global = null;
            try
            {
                NexusHierarchyMenus.CreateLifetimeScope(new MenuCommand(parentGo));
                child = Selection.activeGameObject;
                Assert.That(child.GetComponent<NexusLifetimeScope>().Context.Parent, Is.SameAs(parent.Context));
                configured = NexusHierarchyMenus.CreateSceneRootWithAsset(data);
                Assert.That(configured.ContextData, Is.SameAs(data));
                Assert.That(configured.Context.ScopeTag, Is.EqualTo(data.ScopeTag));
                NexusHierarchyMenus.CreateGlobalProjectRoot(new MenuCommand(parentGo));
                global = Selection.activeGameObject;
                Assert.That(global.transform.parent, Is.Null);
                Assert.That(NexusRuntime.GlobalRoot, Is.SameAs(global.GetComponent<Root>()));
                Assert.That(global.GetComponent<Root>().Context.Parent, Is.Null);
            }
            finally
            {
                if (global != null) UnityEngine.Object.DestroyImmediate(global);
                if (configured != null) UnityEngine.Object.DestroyImmediate(configured.gameObject);
                if (child != null) UnityEngine.Object.DestroyImmediate(child);
                UnityEngine.Object.DestroyImmediate(parentGo);
                UnityEngine.Object.DestroyImmediate(data);
            }
        }
    }
}

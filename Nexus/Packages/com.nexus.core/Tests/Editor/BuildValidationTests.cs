using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Nexus.Core;
using Nexus.Editor;
using Nexus.Editor.Validation.Rules;

namespace Nexus.Editor.Tests
{
    [TestFixture]
    public class BuildValidationTests
    {
        // --- Dummy types for Mixed-Mode validation ---
        public struct MixedSignal { }
 
        [SignalHandler(typeof(MixedSignal), Mode = ExecutionMode.Sequential)]
        public class MixedCommandSeq : ICommand<MixedSignal>
        {
            public void Execute(MixedSignal signal) {}
        }
 
        [SignalHandler(typeof(MixedSignal), Mode = ExecutionMode.Concurrent)]
        public class MixedCommandConc : ICommand<MixedSignal>
        {
            public void Execute(MixedSignal signal) {}
        }
 
        // --- Dummy types for Exclusive-Mode validation ---
        public struct ExclusiveSignal { }
 
        [SignalHandler(typeof(ExclusiveSignal), Mode = ExecutionMode.Exclusive)]
        public class ExclusiveCommandA : ICommand<ExclusiveSignal>
        {
            public void Execute(ExclusiveSignal signal) {}
        }
 
        [SignalHandler(typeof(ExclusiveSignal), Mode = ExecutionMode.Exclusive)]
        public class ExclusiveCommandB : ICommand<ExclusiveSignal>
        {
            public void Execute(ExclusiveSignal signal) {}
        }
 
        // --- Dummy types for Equal Priority validation ---
        public struct EqualPrioritySignal { }
 
        [SignalHandler(typeof(EqualPrioritySignal), Mode = ExecutionMode.Sequential, Priority = 5)]
        public class PriorityCommandA : ICommand<EqualPrioritySignal>
        {
            public void Execute(EqualPrioritySignal signal) {}
        }
 
        [SignalHandler(typeof(EqualPrioritySignal), Mode = ExecutionMode.Sequential, Priority = 5)]
        public class PriorityCommandB : ICommand<EqualPrioritySignal>
        {
            public void Execute(EqualPrioritySignal signal) {}
        }
 
        // --- Dummy types for Concurrent Model Write validation ---
        public interface ISomeModel
        {
            int Value { get; set; } // Settable property indicates writable model
        }
 
        public struct ConcurrentWriteSignal { }
 
        [SignalHandler(typeof(ConcurrentWriteSignal), Mode = ExecutionMode.Concurrent)]
        public class ConcurrentWriteCommand : ICommand<ConcurrentWriteSignal>
        {
            [Inject]
            private ISomeModel _model; // Injects writable model
 
            public void Execute(ConcurrentWriteSignal signal) {}
        }
 
        // --- A writable model reached through the constructor, [OptionalInject], an [Inject] method or a base field (#247) ---
        public struct ConcurrentWaysSignal { }
        public class ConcurrentCtorCommand : ICommand<ConcurrentWaysSignal>
        {
            public ConcurrentCtorCommand(ISomeModel model) { }
            public void Execute(ConcurrentWaysSignal signal) {}
        }
        public class ConcurrentOptionalCommand : ICommand<ConcurrentWaysSignal>
        {
            [OptionalInject] public ISomeModel Model;
            public void Execute(ConcurrentWaysSignal signal) {}
        }
        public class ConcurrentMethodCommand : ICommand<ConcurrentWaysSignal>
        {
            [Inject] public void Construct(ISomeModel model) { }
            public void Execute(ConcurrentWaysSignal signal) {}
        }
        public class ConcurrentCommandBase
        {
            [Inject] private ISomeModel _model;
            protected ISomeModel Model => _model;
        }
        public class ConcurrentInheritedCommand : ConcurrentCommandBase, ICommand<ConcurrentWaysSignal>
        {
            public void Execute(ConcurrentWaysSignal signal) {}
        }

        // --- [OptionalInject] members, which the pool clears on return as it clears [Inject] ones (#248) ---
        public class OptionalInjectPooledCommand : ICommand<MixedSignal>
        {
            [OptionalInject] private ISomeModel _optional;
            [OptionalInject] public ISomeModel OptionalProperty { get; set; }
            public bool HasOptional => _optional != null;
            public void Execute(MixedSignal signal) {}
        }

        // --- Detached handlers (#155): beside Sequential at the same priority, a sync one, one beside Exclusive ---
        public struct DetachedBesideSequentialSignal { }
        [SignalHandler(typeof(DetachedBesideSequentialSignal))]
        public class BesideSequentialCommand : ICommand<DetachedBesideSequentialSignal>
        {
            public void Execute(DetachedBesideSequentialSignal signal) {}
        }
        [SignalHandler(typeof(DetachedBesideSequentialSignal), Mode = ExecutionMode.Detached)]
        public class BesideSequentialDetachedCommand : IAsyncCommand<DetachedBesideSequentialSignal>
        {
            public System.Threading.Tasks.ValueTask ExecuteAsync(DetachedBesideSequentialSignal signal, System.Threading.CancellationToken ct) => default;
        }

        public struct DetachedSyncSignal { }
        [SignalHandler(typeof(DetachedSyncSignal), Mode = ExecutionMode.Detached)]
        public class DetachedSyncCommand : ICommand<DetachedSyncSignal>
        {
            public void Execute(DetachedSyncSignal signal) {}
        }

        public struct DetachedExclusiveSignal { }
        [SignalHandler(typeof(DetachedExclusiveSignal), Mode = ExecutionMode.Exclusive)]
        public class ExclusiveBesideDetachedCommand : IAsyncCommand<DetachedExclusiveSignal>
        {
            public System.Threading.Tasks.ValueTask ExecuteAsync(DetachedExclusiveSignal signal, System.Threading.CancellationToken ct) => default;
        }
        [SignalHandler(typeof(DetachedExclusiveSignal), Mode = ExecutionMode.Detached)]
        public class DetachedBesideExclusiveCommand : IAsyncCommand<DetachedExclusiveSignal>
        {
            public System.Threading.Tasks.ValueTask ExecuteAsync(DetachedExclusiveSignal signal, System.Threading.CancellationToken ct) => default;
        }

        // --- Commands declared with [RegisterCommand]: the context's scan registers them as it does [SignalHandler] ones (#303) ---
        public struct RegisteredExclusiveSignal { }

        [RegisterCommand(typeof(RegisteredExclusiveSignal), Mode = ExecutionMode.Exclusive)]
        public class RegisteredExclusiveCommandA : ICommand<RegisteredExclusiveSignal>
        {
            public void Execute(RegisteredExclusiveSignal signal) {}
        }

        [RegisterCommand(typeof(RegisteredExclusiveSignal), Mode = ExecutionMode.Exclusive)]
        public class RegisteredExclusiveCommandB : ICommand<RegisteredExclusiveSignal>
        {
            public void Execute(RegisteredExclusiveSignal signal) {}
        }

        // --- Dummy types for Command State Leak validation ---
        public class StateLeakCommand : ICommand<MixedSignal>
        {
            private string _mutableState; // Mutable, non-injected field (not readonly)
 
            public void Execute(MixedSignal signal) {}
        }

        [SetUp]
        public void Setup()
        {
            BuildValidation.IncludeTestAssemblies = true;
            BuildValidation.InfoLogger = msg => { };
            BuildValidation.WarningLogger = msg => { };
            BuildValidation.ErrorLogger = msg => { };
        }

        [TearDown]
        public void TearDown()
        {
            BuildValidation.IncludeTestAssemblies = false;
            BuildValidation.InfoLogger = UnityEngine.Debug.Log;
            BuildValidation.WarningLogger = UnityEngine.Debug.LogWarning;
            BuildValidation.ErrorLogger = UnityEngine.Debug.LogError;
        }

        [Test]
        public void ValidateHandlers_DetectsViolationsInTestAssembly()
        {
            // The other fixtures of this class (the Detached ones, the [RegisterCommand] ones) add errors of their own, so a count
            // would stay satisfied while a rule stopped firing: each documented rule must name its own fixture (#311).
            var errors = new List<string>();
            BuildValidation.ErrorLogger = errors.Add;
            var args = new object[] { 0, 0 };
            GetPrivateStatic("ValidateHandlers").Invoke(null, args);

            string all = string.Join("\n", errors);
            // the count is what fails the build: every logged error is counted, as the old assertion required (at least four)
            Assert.AreEqual(errors.Count, (int)args[0], "each logged error counts toward the failure:\n" + all);
            Assert.GreaterOrEqual((int)args[0], 4, all);
            Assert.IsTrue(errors.Exists(e => e.Contains("Mixed-Mode Violation") && e.Contains(typeof(MixedSignal).FullName)),
                "Sequential and Concurrent on MixedSignal:\n" + all);
            Assert.IsTrue(errors.Exists(e => e.Contains("Exclusive Mode Violation") && e.Contains(typeof(ExclusiveSignal).FullName)),
                "two Exclusive handlers on ExclusiveSignal:\n" + all);
            Assert.IsTrue(errors.Exists(e => e.Contains("Equal Priority Violation") && e.Contains(typeof(EqualPrioritySignal).FullName)),
                "priority 5 on both handlers of EqualPrioritySignal:\n" + all);
            Assert.IsTrue(errors.Exists(e => e.Contains("Concurrent Command Model Write Violation") && e.Contains(typeof(ConcurrentWriteCommand).FullName)),
                "ConcurrentWriteCommand injects the writable ISomeModel:\n" + all);
        }

        [Test]
        public void HandlerRules_SeeCommandsDeclaredWithRegisterCommand_AsTheContextScanRegistersThem()
        {
            // Two Exclusive [RegisterCommand] commands on one signal threw in the player's scan ("Exclusive execution mode
            // violation") and killed the Root, while the build passed: the rules read [SignalHandler] only (#303).
            var errors = new List<string>();
            BuildValidation.ErrorLogger = errors.Add;
            GetPrivateStatic("ValidateHandlers").Invoke(null, new object[] { 0, 0 });

            Assert.IsTrue(errors.Exists(e => e.Contains("Exclusive Mode Violation") && e.Contains(typeof(RegisteredExclusiveSignal).FullName)),
                string.Join("\n", errors));
        }

        [Test]
        public void DetachedHandler_JoinsASequentialSignal_ButNotAsASyncCommandOrBesideExclusive()
        {
            var errors = new List<string>();
            BuildValidation.ErrorLogger = errors.Add;
            var method = typeof(BuildValidation).GetMethod("ValidateHandlers", BindingFlags.NonPublic | BindingFlags.Static);
            method.Invoke(null, new object[] { 0, 0 });

            Assert.IsFalse(errors.Exists(e => e.Contains(nameof(DetachedBesideSequentialSignal))),
                "A Detached handler beside a Sequential one at the same priority is valid: " + string.Join(" | ", errors));
            Assert.IsTrue(errors.Exists(e => e.Contains("Detached Mode Violation") && e.Contains(nameof(DetachedSyncSignal))),
                "A sync ICommand marked Detached must be reported.");
            Assert.IsTrue(errors.Exists(e => e.Contains("Exclusive Mode Violation") && e.Contains(nameof(DetachedExclusiveSignal))),
                "A Detached handler beside an Exclusive one must be reported.");
        }

        [Test]
        public void ConcurrentModelWriteRule_SeesTheConstructor_OptionalInject_AnInjectMethod_AndABaseField()
        {
            var errors = new List<string>();
            BuildValidation.ErrorLogger = errors.Add;
            var rule = GetPrivateStatic("ValidateConcurrentCommandInjection");

            foreach (var command in new[] { typeof(ConcurrentCtorCommand), typeof(ConcurrentOptionalCommand), typeof(ConcurrentMethodCommand), typeof(ConcurrentInheritedCommand) })
            {
                errors.Clear();
                var args = new object[] { command, 0 };
                rule.Invoke(null, args);
                Assert.AreEqual(1, (int)args[1], command.Name + ": " + string.Join("\n", errors));
                StringAssert.Contains(command.FullName + " injects writeable model type ISomeModel", errors[0]);
            }
        }

        [Test]
        public void StateLeakRule_SkipsOptionalInjectMembers_WhichThePoolClears()
        {
            var warnings = new List<string>();
            BuildValidation.WarningLogger = warnings.Add;

            GetPrivateStatic("ValidateCommandStateLeak").Invoke(null, new object[] { 0, 0 });

            CollectionAssert.IsEmpty(warnings.FindAll(w => w.Contains(typeof(OptionalInjectPooledCommand).FullName + " ")));
            Assert.IsTrue(warnings.Exists(w => w.Contains(typeof(StateLeakCommand).FullName + " ")), "a mutable field is still reported");
        }

        // --- Dummy type for the stub-service check (issue #104) ---
        [StubService("a game's placeholder")]
        public class GameStubService { }

        [Test]
        public void StubServiceWarning_NamesOnlyTypesMarkedAsStubs_NotTheFrameworksServices()
        {
            // Every build warned that AdService, IapService, AnalyticsService and FeedbackService were stubs,
            // whatever the game had connected (issue #104); a game's own placeholder must still be named.
            var warnings = new List<string>();
            BuildValidation.WarningLogger = warnings.Add;

            GetPrivateStatic("ValidateNoStubServices").Invoke(null, new object[] { 0, 0 });

            var stubWarnings = warnings.FindAll(w => w.Contains("Stub Service:"));
            Assert.AreEqual(1, stubWarnings.Count, string.Join("\n", stubWarnings));
            StringAssert.Contains(typeof(GameStubService).FullName, stubWarnings[0]);
        }

        [Test]
        public void Validate_RunSilent_PublishesAReadableSummary()
        {
            BuildValidation.IncludeTestAssemblies = true;

            var previousInfo = BuildValidation.InfoLogger;
            var previousWarning = BuildValidation.WarningLogger;
            var previousError = BuildValidation.ErrorLogger;

            try
            {
                BuildValidation.InfoLogger = _ => { };
                BuildValidation.WarningLogger = _ => { };
                BuildValidation.ErrorLogger = _ => { };

                BuildValidation.RunSilent();

                Assert.IsTrue(BuildValidation.HasRun, "Validation should mark HasRun after RunSilent.");
                Assert.IsNotNull(BuildValidation.LastRunSummary, "Validation summary should always be available after a run.");
                Assert.IsNotEmpty(BuildValidation.LastRunSummary, "Validation summary should not be empty after a run.");
                Assert.That(BuildValidation.LastRunSummary, Does.Contain("errors").IgnoreCase);
                Assert.That(BuildValidation.LastRunSummary, Does.Contain("warnings").IgnoreCase);
            }
            finally
            {
                BuildValidation.InfoLogger = previousInfo;
                BuildValidation.WarningLogger = previousWarning;
                BuildValidation.ErrorLogger = previousError;
                BuildValidation.IncludeTestAssemblies = false;
            }
        }

        // A project rule from this test assembly; it acts only while a test sets its mode (#193)
        public sealed class ProbeRule : IBuildValidationRule
        {
            public enum Mode { Off, Report, Throw }
            public static Mode Current;
            public string Name => "Probe Rule";

            public IEnumerable<ValidationIssue> Evaluate()
            {
                if (Current == Mode.Throw) throw new InvalidOperationException("probe rule crashed on purpose");
                if (Current == Mode.Report)
                {
                    yield return new ValidationIssue { Message = "probe error", IsError = true };
                    yield return new ValidationIssue { Message = "probe warning", IsError = false };
                }
            }
        }

        private static (int Errors, int Warnings) RunSilentWithProbe(ProbeRule.Mode mode)
        {
            ProbeRule.Current = mode;
            try { BuildValidation.RunSilent(); }
            finally { ProbeRule.Current = ProbeRule.Mode.Off; }
            return (BuildValidation.LastErrorCount, BuildValidation.LastWarningCount);
        }

        [Test]
        public void AProjectRule_RunsInValidation_AndItsIssuesCountAndAreListedUnderItsName()
        {
            BuildValidation.IncludeTestAssemblies = true;
            try
            {
                var without = RunSilentWithProbe(ProbeRule.Mode.Off);
                var with = RunSilentWithProbe(ProbeRule.Mode.Report);

                Assert.AreEqual(without.Errors + 1, with.Errors, "the rule's error counts");
                Assert.AreEqual(without.Warnings + 1, with.Warnings, "the rule's warning counts");
                Assert.IsFalse(BuildValidation.LastRunPassed);
                CollectionAssert.IsSubsetOf(new[] { ("Probe Rule", true, "[Nexus Error] Probe Rule: probe error"), ("Probe Rule", false, "[Nexus Warning] Probe Rule: probe warning") },
                    System.Linq.Enumerable.ToList(System.Linq.Enumerable.Select(BuildValidation.LastResults, e => (e.Rule, e.IsError, e.Message))));
            }
            finally { BuildValidation.IncludeTestAssemblies = false; }
        }

        [Test]
        public void AValidationThatCrashes_FailsWithEveryErrorCounted_AndTheCrashListed()
        {
            BuildValidation.IncludeTestAssemblies = true;
            try
            {
                var complete = RunSilentWithProbe(ProbeRule.Mode.Off);
                var crashed = RunSilentWithProbe(ProbeRule.Mode.Throw);

                Assert.IsFalse(BuildValidation.LastRunPassed, "a run that crashed did not check everything");
                Assert.AreEqual(complete.Errors + 1, crashed.Errors, "the errors found before the crash and the crash itself");
                Assert.AreEqual(complete.Warnings, crashed.Warnings);
                Assert.IsTrue(System.Linq.Enumerable.Any(BuildValidation.LastResults, e => e.IsError && e.Rule == "ValidationException" && e.Message.Contains("probe rule crashed on purpose")),
                    "the crash is one of the results");
            }
            finally { BuildValidation.IncludeTestAssemblies = false; }
        }

        // --- Dummy types for the DI scan-filter predicates ---
        public class DiScanPlainType { }
        public class DiScanAttribute : Attribute { }
        public delegate void DiScanDelegate();
        [CompilerGenerated]
        public class DiScanGeneratedType { }
        public class DiScanMarkerFree { }
        public class DiScanFieldInjected
        {
            [Inject] public ISomeModel Model;
        }
        public class DiScanPropertyInjected
        {
            [Inject] public ISomeModel Model { get; private set; }
        }
        public class DiScanCtorInjected
        {
            [Inject] public DiScanCtorInjected(ISomeModel model) { }
        }
        public class DiScanOptionalField
        {
            [OptionalInject] public ISomeModel Model;
        }
        public class DiScanMethodParamInjected
        {
            // [OptionalInject] is the only inject-marker that AttributeUsage permits on
            // parameters (InjectAttribute targets Constructor|Field|Property|Method).
            public void Setup([OptionalInject] ISomeModel model) { }
        }
        public class DiScanMissingAssemblyType { }

        private static bool InvokeBool(MethodInfo method, params object[] args)
            => (bool)method.Invoke(null, args);

        private static MethodInfo GetPrivateStatic(string name)
        {
            var method = typeof(BuildValidation).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method, $"{name} method not found via reflection.");
            return method;
        }

        [Test]
        public void IsDiInspectableType_AcceptsPlainGameTypes()
        {
            var method = GetPrivateStatic("IsDiInspectableType");
            Assert.IsTrue(InvokeBool(method, typeof(DiScanPlainType)), "A plain game class must be inspectable.");
        }

        [Test]
        public void IsDiInspectableType_RejectsAttributesAndDelegates()
        {
            var method = GetPrivateStatic("IsDiInspectableType");
            Assert.IsFalse(InvokeBool(method, typeof(DiScanAttribute)), "Attribute types are never DI candidates.");
            Assert.IsFalse(InvokeBool(method, typeof(DiScanDelegate)), "Delegate types are never DI candidates.");
        }

        [Test]
        public void IsDiInspectableType_RejectsCompilerGeneratedTypes()
        {
            var method = GetPrivateStatic("IsDiInspectableType");
            Assert.IsFalse(InvokeBool(method, typeof(DiScanGeneratedType)), "Iterator/closure/state-machine types are never DI candidates.");
        }

        [Test]
        public void IsDiInspectableType_RejectsFrameworkNamespaces()
        {
            var method = GetPrivateStatic("IsDiInspectableType");
            Assert.IsFalse(InvokeBool(method, typeof(string)), "BCL types under System.* are not inspectable.");
            Assert.IsFalse(InvokeBool(method, typeof(UnityEngine.Object)), "Engine types under UnityEngine.* are not inspectable.");
        }

        [Test]
        public void HasInjectionMarkers_DetectsEveryMarkerShape()
        {
            var method = GetPrivateStatic("HasInjectionMarkers");
            Assert.IsFalse(InvokeBool(method, typeof(DiScanMarkerFree)), "Unmarked type must not count as a DI candidate.");
            Assert.IsTrue(InvokeBool(method, typeof(DiScanFieldInjected)), "[Inject] field is a marker.");
            Assert.IsTrue(InvokeBool(method, typeof(DiScanPropertyInjected)), "[Inject] property is a marker.");
            Assert.IsTrue(InvokeBool(method, typeof(DiScanCtorInjected)), "[Inject] constructor is a marker.");
            Assert.IsTrue(InvokeBool(method, typeof(DiScanOptionalField)), "[OptionalInject] field is a marker.");
            Assert.IsTrue(InvokeBool(method, typeof(DiScanMethodParamInjected)), "[OptionalInject] method parameter is a marker.");
        }

        [Test]
        public void IsTypeAvailable_UnwrapsArraysAndGenerics()
        {
            var method = GetPrivateStatic("IsTypeAvailable");
            var set = new HashSet<string> { typeof(DiScanPlainType).FullName };

            // Value-type array elements are always resolvable.
            Assert.IsTrue(InvokeBool(method, typeof(int[]), new HashSet<string>()), "int[] must resolve (value element).");
            // Array of a scanned type resolves against the element's FullName.
            Assert.IsTrue(InvokeBool(method, typeof(DiScanPlainType[]), set), "Array of a scanned type must resolve.");
            // Array of a missing type must NOT resolve. This relies on the editor-test
            // assembly name not matching a framework/third-party prefix (it does not:
            // "Nexus.Tests.Editor" is not System*/Unity*/Bee/GLTFast/etc.), so the element
            // falls through to the assembly check and reports unavailable.
            Assert.IsFalse(InvokeBool(method, typeof(DiScanMissingAssemblyType[]), new HashSet<string>()), "Array of a missing type must not resolve.");
            // Closed generics check their type arguments.
            Assert.IsTrue(InvokeBool(method, typeof(List<DiScanPlainType>), set), "List<scanned> must resolve.");
            Assert.IsFalse(InvokeBool(method, typeof(List<DiScanMissingAssemblyType>), new HashSet<string>()), "List<missing> must not resolve.");
            // Plain lookups still work.
            Assert.IsTrue(InvokeBool(method, typeof(DiScanPlainType), set), "Plain scanned type must resolve.");
        }

        // #305: a batch build runs with an empty scene open, so a scene of the build settings has to be opened to be checked.
        [Test]
        public void ABuildSceneThatIsNotLoaded_IsCheckedForRootCycles_AndLeftAsItWas()
        {
            const string folder = "Assets/NexusSceneValidationTest";
            const string scenePath = folder + "/ParentCycle.unity";
            var openBefore = UnityEditor.SceneManagement.EditorSceneManager.GetSceneManagerSetup();
            var buildScenesBefore = UnityEditor.EditorBuildSettings.scenes;
            try
            {
                UnityEditor.AssetDatabase.CreateFolder("Assets", "NexusSceneValidationTest");
                var scene = UnityEditor.SceneManagement.EditorSceneManager.NewScene(UnityEditor.SceneManagement.NewSceneSetup.EmptyScene, UnityEditor.SceneManagement.NewSceneMode.Single);
                var first = new UnityEngine.GameObject("CycleFirst").AddComponent<Root>();
                var second = new UnityEngine.GameObject("CycleSecond").AddComponent<Root>();
                SetParentRoot(first, second);
                SetParentRoot(second, first);
                Assert.IsTrue(UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene, scenePath));
                // an empty untitled scene is all that is open, as in a batch build
                UnityEditor.SceneManagement.EditorSceneManager.NewScene(UnityEditor.SceneManagement.NewSceneSetup.EmptyScene, UnityEditor.SceneManagement.NewSceneMode.Single);
                UnityEditor.EditorBuildSettings.scenes = new[] { new UnityEditor.EditorBuildSettingsScene(scenePath, true) };

                var errors = new List<string>();
                BuildValidation.ErrorLogger = errors.Add;
                int scenesOpen = UnityEngine.SceneManagement.SceneManager.sceneCount;

                GetPrivateStatic("ValidateSceneHierarchy").Invoke(null, new object[] { 0, 0 });

                Assert.IsTrue(errors.Exists(e => e.Contains("Circular Context Violation") && e.Contains("CycleFirst")),
                    "the parentRoot cycle of the closed build scene must be reported: " + string.Join(" | ", errors));
                Assert.AreEqual(scenesOpen, UnityEngine.SceneManagement.SceneManager.sceneCount, "the scene opened for the check is closed again");
                Assert.IsFalse(UnityEngine.SceneManagement.SceneManager.GetSceneByPath(scenePath).IsValid(), "a scene that was not in the hierarchy is removed again");

                // The same scene in the user's hierarchy but unloaded: loaded for the check, then unloaded again, never removed from it.
                var unloaded = UnityEditor.SceneManagement.EditorSceneManager.OpenScene(scenePath, UnityEditor.SceneManagement.OpenSceneMode.Additive);
                UnityEditor.SceneManagement.EditorSceneManager.CloseScene(unloaded, false);
                errors.Clear();
                GetPrivateStatic("ValidateSceneHierarchy").Invoke(null, new object[] { 0, 0 });

                Assert.IsTrue(errors.Exists(e => e.Contains("Circular Context Violation") && e.Contains("CycleFirst")), string.Join(" | ", errors));
                var after = UnityEngine.SceneManagement.SceneManager.GetSceneByPath(scenePath);
                Assert.IsTrue(after.IsValid() && !after.isLoaded, "an unloaded scene of the hierarchy stays in it, unloaded");
            }
            finally
            {
                UnityEditor.EditorBuildSettings.scenes = buildScenesBefore;
                UnityEditor.AssetDatabase.DeleteAsset(folder);
                if (openBefore.Length > 0) UnityEditor.SceneManagement.EditorSceneManager.RestoreSceneManagerSetup(openBefore);
            }
        }

        private static void SetParentRoot(Root root, Root parent)
        {
            var serialized = new UnityEditor.SerializedObject(root);
            serialized.FindProperty("parentRoot").objectReferenceValue = parent;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        // #306: the Tracer tab's Enable button writes NEXUS_DEBUG into the player settings of the build target.
        [Test]
        public void ANonDevelopmentBuildThatCarriesNexusDebug_Warns_ButADevelopmentBuildAndALookalikeSymbolDoNot()
        {
            StringAssert.Contains("NEXUS_DEBUG", NexusBuildPreProcessor.TracingDefineWarning("UNITY_X;NEXUS_DEBUG;OTHER", developmentBuild: false));
            Assert.IsNull(NexusBuildPreProcessor.TracingDefineWarning("UNITY_X;NEXUS_DEBUG;OTHER", developmentBuild: true));
            Assert.IsNull(NexusBuildPreProcessor.TracingDefineWarning("NEXUS_DEBUG_LOG;MY_NEXUS_DEBUG", developmentBuild: false));
            Assert.IsNull(NexusBuildPreProcessor.TracingDefineWarning("", developmentBuild: false));
        }

        [Test]
        public void TracerDefine_EnableAddsNexusDebugOnce_AndDisableRemovesOnlyThatSymbol()
        {
            Assert.AreEqual("A;B;NEXUS_DEBUG", TracerPlugin.WithDebugDefine("A;B", enabled: true));
            Assert.AreEqual("A;NEXUS_DEBUG;B", TracerPlugin.WithDebugDefine("A;NEXUS_DEBUG;B", enabled: true), "enabling it twice changes nothing");
            Assert.AreEqual("A;B", TracerPlugin.WithDebugDefine("A;NEXUS_DEBUG;B", enabled: false));
            Assert.AreEqual("NEXUS_DEBUG_LOG;A", TracerPlugin.WithDebugDefine("NEXUS_DEBUG_LOG;A", enabled: false), "a symbol that only starts with it stays");
            Assert.AreEqual("", TracerPlugin.WithDebugDefine("", enabled: false));
            // a padded entry carries the symbol too (the pre-build warning trims it): Disable removes it, Enable does not add a second
            Assert.AreEqual("A", TracerPlugin.WithDebugDefine("A; NEXUS_DEBUG ", enabled: false));
            Assert.AreEqual("A; NEXUS_DEBUG", TracerPlugin.WithDebugDefine("A; NEXUS_DEBUG", enabled: true));
        }

        public sealed class OwnedScoreModel { }

        // #243: only " Name ", "Name;", "Name:" and "Name," counted, so every generic binding failed the build with a
        // false leak error, and "IOwnedScoreModel," counted for OwnedScoreModel.
        [Test]
        public void AModelBoundThroughAGenericCallIsNamedByItsLifecycleAndAnInterfaceNameIsNot()
        {
            Assert.IsTrue(BuildValidation.NamesType("builder.BindReactiveModel<OwnedScoreModel>();", typeof(OwnedScoreModel)));
            Assert.IsTrue(BuildValidation.NamesType("builder.Bind<IScore, OwnedScoreModel>();", typeof(OwnedScoreModel)));
            Assert.IsTrue(BuildValidation.NamesType("builder.BindInstance(new OwnedScoreModel());", typeof(OwnedScoreModel)));
            Assert.IsFalse(BuildValidation.NamesType("builder.BindReactiveModel<IOwnedScoreModel, OtherModel>();", typeof(OwnedScoreModel)));
            Assert.IsFalse(BuildValidation.NamesType("builder.Bind<OwnedScoreModelView>();", typeof(OwnedScoreModel)));
        }
    }
}

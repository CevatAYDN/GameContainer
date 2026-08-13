using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Nexus.Core;
using Nexus.Editor;

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
            var method = typeof(BuildValidation).GetMethod("ValidateHandlers", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method, "ValidateHandlers method not found via reflection.");

            object[] args = new object[] { 0, 0 };
            method.Invoke(null, args);

            int errorCount = (int)args[0];
            int warningCount = (int)args[1];

            // Expecting:
            // 1. Mixed mode error on MixedSignal (Sequential and Concurrent)
            // 2. Exclusive mode error on ExclusiveSignal (2 handlers registered as Exclusive)
            // 3. Priority tie error on EqualPrioritySignal (Priority 5 on both)
            // 4. Concurrent write error on ConcurrentWriteCommand (injects writable ISomeModel)
            Assert.GreaterOrEqual(errorCount, 4, $"Expected at least 4 errors from handler validation, got {errorCount}.");
        }

        [Test]
        public void ValidateCommandStateLeak_DetectsLeakRisks()
        {
            var method = typeof(BuildValidation).GetMethod("ValidateCommandStateLeak", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method, "ValidateCommandStateLeak method not found via reflection.");

            object[] args = new object[] { 0, 0 };
            method.Invoke(null, args);

            int errorCount = (int)args[0];
            int warningCount = (int)args[1];

            // Expecting warning for StateLeakCommand because it has private string _mutableState but does not implement IResettable
            Assert.GreaterOrEqual(warningCount, 1, $"Expected at least 1 warning for command state leak, got {warningCount}.");
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
    }
}

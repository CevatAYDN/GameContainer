using NUnit.Framework;
using System;
using System.Reflection;
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
        public class MixedCommandSeq : ICommand
        {
            public void Execute() {}
        }

        [SignalHandler(typeof(MixedSignal), Mode = ExecutionMode.Concurrent)]
        public class MixedCommandConc : ICommand
        {
            public void Execute() {}
        }

        // --- Dummy types for Exclusive-Mode validation ---
        public struct ExclusiveSignal { }

        [SignalHandler(typeof(ExclusiveSignal), Mode = ExecutionMode.Exclusive)]
        public class ExclusiveCommandA : ICommand
        {
            public void Execute() {}
        }

        [SignalHandler(typeof(ExclusiveSignal), Mode = ExecutionMode.Exclusive)]
        public class ExclusiveCommandB : ICommand
        {
            public void Execute() {}
        }

        // --- Dummy types for Equal Priority validation ---
        public struct EqualPrioritySignal { }

        [SignalHandler(typeof(EqualPrioritySignal), Mode = ExecutionMode.Sequential, Priority = 5)]
        public class PriorityCommandA : ICommand
        {
            public void Execute() {}
        }

        [SignalHandler(typeof(EqualPrioritySignal), Mode = ExecutionMode.Sequential, Priority = 5)]
        public class PriorityCommandB : ICommand
        {
            public void Execute() {}
        }

        // --- Dummy types for Concurrent Model Write validation ---
        public interface ISomeModel
        {
            int Value { get; set; } // Settable property indicates writable model
        }

        public struct ConcurrentWriteSignal { }

        [SignalHandler(typeof(ConcurrentWriteSignal), Mode = ExecutionMode.Concurrent)]
        public class ConcurrentWriteCommand : ICommand
        {
            [Inject]
            private ISomeModel _model; // Injects writable model

            public void Execute() {}
        }

        // --- Dummy types for Command State Leak validation ---
        public class StateLeakCommand : ICommand
        {
            private string _mutableState; // Mutable, non-injected field (not readonly)

            public void Execute() {}
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
    }
}

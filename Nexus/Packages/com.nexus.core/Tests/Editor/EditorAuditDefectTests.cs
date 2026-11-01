using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using NUnit.Framework;
using Nexus.Core;
using Nexus.Core.Components;
using Nexus.Core.Services;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace Nexus.Editor.Tests
{
    public interface IInspectorAuditDep { }

    public class InspectorAuditComponentBase : MonoBehaviour
    {
        [Inject] private IInspectorAuditDep _fromBase = null;
        public IInspectorAuditDep FromBase => _fromBase;
        [Inject] public void Construct(IInspectorAuditDep dep) { }
    }

    public class InspectorAuditComponent : InspectorAuditComponentBase { }

    public class InspectorAuditBehaviourBase : NexusBehaviour
    {
        [Inject] private IInspectorAuditDep _fromBase = null;
        public IInspectorAuditDep FromBase => _fromBase;
        [Inject] public void Construct(IInspectorAuditDep dep) { }
    }

    public class InspectorAuditBehaviour : InspectorAuditBehaviourBase { }

    /// <summary>Editor defects the audit #206 found (#252-#260); each test fails on the code before its fix.</summary>
    public class EditorAuditDefectTests
    {
        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
        private const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;

        private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, Instance).GetValue(owner);

        [Test]
        public void ErrorDashboard_WithAFullBuffer_StillListsANewError()
        {
            // The dashboard rebuilt when the buffer's count changed, and a full buffer keeps its count (#252).
            int max = ErrorCollection.MaxErrors;
            bool enabled = ErrorCollection.Enabled;
            bool suspended = ErrorCollection.Suspended;
            var dashboard = new ErrorDashboardPlugin();
            try
            {
                ErrorCollection.Enabled = true;
                ErrorCollection.Suspended = false; // a test run suspends collection (#193)
                ErrorCollection.Clear();
                ErrorCollection.MaxErrors = 100;
                for (int i = 0; i < 100; i++)
                    ErrorCollection.Collect(ErrorCollection.ErrorSeverity.Error, ErrorCollection.ErrorCategory.General, "dashboard252 old " + i, logToConsole: false);
                var view = dashboard.CreateView();

                ErrorCollection.Collect(ErrorCollection.ErrorSeverity.Error, ErrorCollection.ErrorCategory.General, "dashboard252 newest", logToConsole: false);
                dashboard.OnUpdate();

                Assert.IsTrue(view.Query<Label>().ToList().Any(label => label.text != null && label.text.Contains("dashboard252 newest")),
                    "the new error is listed");
            }
            finally
            {
                dashboard.OnDisable();
                ErrorCollection.MaxErrors = max;
                ErrorCollection.Clear();
                ErrorCollection.Enabled = enabled;
                ErrorCollection.Suspended = suspended;
            }
        }

        [Test]
        public void Inspectors_ListABaseClassPrivateInjectField_AndAnInjectMethodsParameter()
        {
            // both inspectors read the leaf type's fields and properties: a base class's private member and [Inject]
            // methods were missing, so a component showed no dependency while Nexus injected it (#254)
            var go = new GameObject("InspectorAudit254");
            try
            {
                var binding = go.AddComponent<NexusBinding>();
                binding.Scope = InjectionScope.Self;
                go.AddComponent<InspectorAuditComponent>();
                var behaviour = go.AddComponent<InspectorAuditBehaviour>();

                var component = Inspector.NexusBindingEditor.DiscoverInjectables(binding).FirstOrDefault(item => item.Component is InspectorAuditComponent);
                Assert.IsNotNull(component.Component, "the binding inspector lists the component");
                CollectionAssert.AreEquivalent(new[] { "_fromBase", "Construct(dep)" }, component.InjectedMembers.Select(m => m.Name).ToArray());

                var dependencies = (IList)typeof(Inspector.NexusBehaviourEditor).GetMethod("GetInjectedDependencies", Static).Invoke(null, new object[] { behaviour });
                var names = dependencies.Cast<object>().Select(entry => (string)entry.GetType().GetField("Item1").GetValue(entry)).ToArray();
                CollectionAssert.IsSubsetOf(new[] { "_fromBase", "Construct(dep)" }, names, "the behaviour inspector lists both");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

#if NEXUS_DEBUG
        private static System.Action TracerAction(TracerPlugin tracer, string key)
            => tracer.GetContextActions().First(action => action.Label == NexusLang.Get(key)).Action;

        [Test]
        public void TracerClearBuffer_StaysClearedWhenTheTabIsBuiltAgain()
        {
            // Clear Buffer emptied the tab's list only; the next CreateView read the trace buffer and the events came back (#256)
            var tracer = new TracerPlugin();
            try
            {
                NexusTrace.EndEvent(NexusTrace.BeginEvent(TraceEventType.Signal, "TracerAudit256"));
                tracer.CreateView();
                TracerAction(tracer, "tr_ctx_clear_buffer")();
                tracer.OnDisable();

                tracer.CreateView();

                Assert.AreEqual(0, Field<System.Collections.Generic.List<TraceEvent>>(tracer, "_allEvents").Count);
            }
            finally { tracer.OnDisable(); }
        }

        [Test]
        public void TracerPauseAction_ShowsOnThePauseToggle()
        {
            // the action bar's Pause set the flag and left the toggle off: the tab looked live, and unpausing took two clicks (#256)
            var tracer = new TracerPlugin();
            try
            {
                tracer.CreateView();
                TracerAction(tracer, "tr_ctx_pause")();

                Assert.IsTrue(Field<bool>(tracer, "_isPaused"));
                Assert.IsTrue(Field<Toggle>(tracer, "_pauseToggle").value, "the toggle shows the pause");
            }
            finally { tracer.OnDisable(); }
        }

        [Test]
        public void TracerWhilePaused_KeepsOnlyTheNewest5000Events()
        {
            // while paused every event was queued and nothing was dropped: editor memory grew with the game's traffic (#256)
            var tracer = new TracerPlugin();
            try
            {
                tracer.CreateView();
                TracerAction(tracer, "tr_ctx_pause")();
                var traceEvent = default(TraceEvent);
                for (int i = 0; i < 20000; i++) tracer.Write(in traceEvent);

                Assert.LessOrEqual(Field<ConcurrentQueue<TraceEvent>>(tracer, "_incomingEvents").Count, 5000);
            }
            finally { tracer.OnDisable(); }
        }
#endif

        [Test]
        public void SceneOverlay_AfterARootIsReplacedByAnIdenticalOne_DrawsTheNewRoot()
        {
            // the change signature held scope tags, bounds, link counts and parents but not the Roots, so a reloaded
            // scene with the same layout kept the destroyed Roots and drew nothing (#257)
            var refresh = typeof(NexusSceneOverlay).GetMethod("RefreshData", Static);
            var nodes = (System.Collections.Generic.List<NexusSceneOverlayContextNode>)typeof(NexusSceneOverlay).GetField("s_nodes", Static).GetValue(null);
            var signature = typeof(NexusSceneOverlay).GetField("s_lastSignature", Static);
            var first = new GameObject("OverlayAudit257");
            GameObject second = null;
            try
            {
                first.AddComponent<Root>();
                refresh.Invoke(null, null);
                Object.DestroyImmediate(first);
                second = new GameObject("OverlayAudit257");
                second.AddComponent<Root>();

                refresh.Invoke(null, null);

                Assert.IsTrue(nodes.Any(node => node.GameObject == second), "the overlay holds the new Root");
            }
            finally
            {
                if (first != null) Object.DestroyImmediate(first);
                if (second != null) Object.DestroyImmediate(second);
                nodes.Clear();
                signature.SetValue(null, int.MinValue);
            }
        }

        public struct HandlerCountSignal { }

        public sealed class HandlerCountCommand : ICommand<HandlerCountSignal>
        {
            public void Execute(HandlerCountSignal signal) { }
        }

        [Test]
        public async Task HandlerCount_WhileContextsRun_IsWhatTheirBusesHold_NotThatPlusTheAttributeScan()
        {
            // the count added the static [SignalHandler] scan to the handlers the running contexts registered, and a
            // Root's attribute scan registers the same commands, so each one was counted twice (#258)
            var provider = typeof(NexusEditorDataProvider);
            var context = await ContextFactory.StartAsync("handler-count-258", builder => builder.BindCommand<HandlerCountSignal, HandlerCountCommand>());
            try
            {
                NexusEditorDataProvider.GetHandlerCount(); // builds the static scan
                provider.GetField("s_cachedHandlerCount", Static).SetValue(null, 1); // as if it had found the bound command
                int registered = 0;
                foreach (var running in NexusRuntime.ActiveContexts)
                {
                    var handlers = running?.SignalBus?.RegisteredHandlers;
                    if (handlers != null) foreach (var entry in handlers) registered += entry.Value.Count;
                }

                Assert.AreEqual(registered, NexusEditorDataProvider.GetHandlerCount());
            }
            finally
            {
                provider.GetField("s_cacheValid", Static).SetValue(null, false);
                await context.DisposeAsync();
            }
        }

        [Test]
        public async Task CasualServicesEarn_UsesTheContextThatHoldsTheEconomy_NotWhicheverRootComesFirst()
        {
            // the actions took the first Root FindObjectsByType returned (unordered) and did nothing, with no line, when
            // that Root's context lacked the service, as a Global parent does when the Game child binds the economy (#259)
            var prefs = new InMemoryPlayerPrefsService();
            var economy = new EconomyService { PlayerPrefsService = prefs };
            var parent = await ContextFactory.StartAsync("casual-parent-259", _ => { });
            Context child = null;
            var plugin = new global::Nexus.Editor.Plugins.CasualServicesPlugin();
            try
            {
                child = await ContextFactory.StartAsync("casual-child-259", builder =>
                {
                    builder.BindInstance<IPlayerPrefsService>(prefs);
                    builder.BindInstance<IEconomyService>(economy);
                }, parent);
                var type = plugin.GetType();
                type.GetField("_currencyNameField", Instance).SetValue(plugin, new TextField { value = "casual259" });
                type.GetField("_currencyAmountField", Instance).SetValue(plugin, new LongField { value = 7 });

                type.GetMethod("OnAddCurrency", Instance).Invoke(plugin, null);

                Assert.AreEqual(7, economy.GetBalance("casual259"));
            }
            finally
            {
                if (child != null) await child.DisposeAsync();
                await parent.DisposeAsync();
            }
        }
    }
}

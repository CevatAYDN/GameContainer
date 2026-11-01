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
    }
}

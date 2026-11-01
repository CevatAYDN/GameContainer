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
    }
}

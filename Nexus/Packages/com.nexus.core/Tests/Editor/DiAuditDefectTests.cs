using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Core;
using Nexus.Core.Lifecycle;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Nexus.Editor.Tests
{
    /// <summary>
    /// The DI and lifecycle defects the audit #206 found: a cycle through a binding shared by two keys froze Resolve for
    /// 10 s (#209), and AsImplementedInterfaces dropped a game interface under "Systems." (#210).
    /// </summary>
    public sealed class DiAuditDefectTests
    {
        public interface IFirst { }
        public interface ISecond { }
        public sealed class SharedCycle : IFirst, ISecond
        {
            public SharedCycle(ISecond self) { }
        }

        [Test]
        public void ACycleThroughABindingSharedByTwoKeys_FailsAtOnceAsACycle()
        {
            using var container = new NexusDI();
            container.BindMultiple<IFirst, ISecond, SharedCycle>(Lifetime.Scoped);
            var clock = Stopwatch.StartNew();

            var error = Assert.Throws<InvalidOperationException>(() => container.Resolve<IFirst>());

            StringAssert.Contains("Circular dependency", error.Message);
            Assert.Less(clock.Elapsed.TotalSeconds, 5, "it waited on its own construction");
        }

        [Test]
        public void AsImplementedInterfaces_BindsAGameInterfaceUnderANamespaceThatStartsWithSystem()
        {
            using var container = new NexusDI();
            container.BindFluent<Systems.Audit210.Score>().AsScoped().AsImplementedInterfaces();

            Assert.IsTrue(container.IsRegistered(typeof(Systems.Audit210.IScore)));
            Assert.IsFalse(container.IsRegistered(typeof(IEquatable<Systems.Audit210.Score>)), "an interface under System. stays out");
        }
    }
}

namespace Systems.Audit210
{
    public interface IScore { }
    public sealed class Score : IScore, IEquatable<Score>
    {
        public bool Equals(Score other) => ReferenceEquals(this, other);
    }
}

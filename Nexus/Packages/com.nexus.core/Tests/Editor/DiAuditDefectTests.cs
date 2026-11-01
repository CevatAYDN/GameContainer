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
    /// The DI and lifecycle defects the audit #206 found: AsImplementedInterfaces dropped a game interface under
    /// "Systems." (#210).
    /// </summary>
    public sealed class DiAuditDefectTests
    {
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

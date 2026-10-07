using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Core;
using Nexus.Core.FSM;
using Nexus.Editor;
using NUnit.Framework;
using UnityEngine.UIElements;

namespace Nexus.Tests.Editor
{
    [TestFixture]
    public class FSMHardeningTests
    {
        private sealed class State : IGameState
        {
            public ValueTask OnEnterAsync(object args, CancellationToken ct) => default;
            public ValueTask OnExitAsync(CancellationToken ct) => default;
            public void OnTick(float deltaTime) { }
        }

        [Test]
        public void UnchangedCard_IsRetained_AndConfigurationChangeRebuildsOnlyItsCard()
        {
            using var context = new Context(); var machine = new GameStateMachine();
            context.Container.BindInstance<IGameStateMachine>(machine);
            var plugin = new FSMPlugin();
            try
            {
                Assert.IsNotNull(plugin.CreateView()); plugin.OnEnable();
                var cards = (Dictionary<IGameStateMachine, VisualElement>)typeof(FSMPlugin)
                    .GetField("_cards", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(plugin);
                var render = typeof(FSMPlugin).GetMethod("Render", BindingFlags.Instance | BindingFlags.NonPublic);
                var original = cards[machine];
                render.Invoke(plugin, null); Assert.AreSame(original, cards[machine]);
                machine.RegisterState(new State()); render.Invoke(plugin, null);
                Assert.AreNotSame(original, cards[machine]);
                var updated = cards[machine]; render.Invoke(plugin, null); Assert.AreSame(updated, cards[machine]);
                plugin.OnUpdate(); plugin.OnDisable(); Assert.AreEqual(0, cards.Count);
                var live = (HashSet<IGameStateMachine>)typeof(FSMPlugin).GetField("_live", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(plugin);
                Assert.AreEqual(0, live.Count);
                Assert.IsNotNull(plugin.CreateView()); plugin.OnEnable(); render.Invoke(plugin, null);
                Assert.IsTrue(cards.ContainsKey(machine));
                var content = (ScrollView)typeof(FSMPlugin).GetField("_content", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(plugin);
                Assert.AreEqual(cards.Count, content.contentContainer.childCount);
            }
            finally { plugin.OnDisable(); }
        }
    }
}

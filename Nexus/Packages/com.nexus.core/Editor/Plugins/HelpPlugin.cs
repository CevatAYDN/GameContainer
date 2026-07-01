using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Nexus.Core;

namespace Nexus.Editor
{
    /// <summary>
    /// In-editor Nexus documentation browser.
    /// Provides quick-start guides, API reference, and best practices.
    /// </summary>
    public class HelpPlugin : NexusEditorPlugin
    {
        public override string Id => "Help";
        public override string DisplayName => "Help & Docs";
        public override int Order => 10;

        private VisualElement _view;
        private ScrollView _scrollView;

        public override VisualElement CreateView()
        {
            _view = new VisualElement { style = { flexGrow = 1 } };

            var toolbar = NexusEditorStyles.CreateToolbar("NEXUS HELP & DOCUMENTATION");
            _view.Add(toolbar);

            _scrollView = new ScrollView { style = { flexGrow = 1, paddingLeft = 20, paddingRight = 20, paddingTop = 15, paddingBottom = 15 } };
            _view.Add(_scrollView);

            RenderQuickStart();
            RenderAPISummary();
            RenderVersionInfo();
            RenderSamples();

            return _view;
        }

        private void RenderQuickStart()
        {
            AddSection("QUICK START", NexusEditorStyles.AccentBlue);

            AddStep("1. Create a Root",
                "GameObject → Nexus → Create Root (or use the Wizard tab).\n" +
                "This creates a Root GameObject + ContextData ScriptableObject in the scene.");

            AddStep("2. Define a Signal & Model",
                "Create a signal struct and a model interface/class pair.\n" +
                "Models can implement IReactiveModel for auto-notification via ObservableProperty<T>.");

            AddStep("3. Write a Lifecycle",
                "Create a class named {ScopeTag}Lifecycle implementing IContextLifecycle.\n" +
                "Nexus auto-discovers it. Use OnConfigure() to bind models and commands.");

            AddStep("4. Create Commands",
                "Implement ICommand<TSignal> or IAsyncCommand<TSignal>.\n" +
                "Bind in lifecycle: builder.BindSignal<MySignal>().To<MyCommand>();");

            AddStep("5. Wire Views & Mediators",
                "Extend View, add [Mediator(typeof(MyMediator))], create Mediator<MyView>.\n" +
                "Use Subscribe<TSignal>() in OnBind() to react to signals.");

            AddStep("6. Fire Signals",
                "SignalBus.Fire(new MySignal(data)) — from mediators, commands, or any [Inject]ed class.");
        }

        private void RenderAPISummary()
        {
            AddSection("CORE API", NexusEditorStyles.AccentPurple);
            AddCard("SignalBus",
                "Fire<T>(T signal) — synchronous dispatch\n" +
                "FireAsync<T>(T signal) — awaitable async dispatch\n" +
                "FireAsyncWithTimeout<T>(T, ms) — with timeout\n" +
                "FireAsyncAndForget<T>(T, onError?) — fire-and-forget\n" +
                "FireThreadSafe<T>(T) — from any thread\n" +
                "FireNextFrame<T>(T) — deferred to next frame\n" +
                "Subscribe<T>(Action<T>) / SubscribeAsync<T>(Func<T,CT,ValueTask>)");

            AddCard("ContextBuilder",
                "BindModel<T,I>() / BindReactiveModel<T,I>() — singleton models\n" +
                "BindService<T,I>() — managed services with lifecycle\n" +
                "BindSignal<T>().To<TCmd>() — fluent command binding\n" +
                "BindCommand<T,TCmd>(mode, priority) — imperative binding\n" +
                "BindAsyncCommand<T,TCmd>(mode, priority) — async binding");

            AddCard("Execution Modes",
                "Sequential — default, priority-ordered, one at a time\n" +
                "Concurrent — parallel async execution\n" +
                "Exclusive — single-handler guarantee\n" +
                "Composite — fan-in: waits for multiple signals");

            AddCard("Attributes",
                "[SignalHandler(typeof(T))] — auto-register command\n" +
                "[CompositeSignalHandler(T1, T2)] — fan-in trigger\n" +
                "[CrossContext(ScopeTag?)] — cross-context signal\n" +
                "[Inject] — DI injection point\n" +
                "[Mediator(typeof(T))] — view-mediator binding\n" +
                "[LiveReload] — Play Mode asset sync\n" +
                "[CommandTimeout(ms)] — async command timeout");

            AddCard("Recovery",
                "IRecoveryStrategy.OnCommandFailed(ctx) → RecoveryDecision\n" +
                "RecoveryDecision.Skip() — skip and continue\n" +
                "RecoveryDecision.Retry(max:3) — retry up to N times\n" +
                "RecoveryDecision.Abort() — stop the chain\n" +
                "RecoveryDecision.Fallback<T>() — run alternative command");
        }

        private void RenderVersionInfo()
        {
            AddSection("VERSION", NexusEditorStyles.AccentGreen);

            var card = new VisualElement
            {
                style =
                {
                    backgroundColor = new StyleColor(NexusEditorStyles.CardBg),
                    marginTop = 5,
                    marginBottom = 10,
                    paddingLeft = 12,
                    paddingRight = 12,
                    paddingTop = 8,
                    paddingBottom = 8,
                    borderTopLeftRadius = 6,
                    borderTopRightRadius = 6,
                    borderBottomLeftRadius = 6,
                    borderBottomRightRadius = 6,
                }
            };

            card.Add(new Label("com.nexus.core v0.3.0")
            {
                style = { fontSize = 13, unityFontStyleAndWeight = FontStyle.Bold, color = new StyleColor(NexusEditorStyles.AccentBlue) }
            });

            card.Add(new Label("Unity 6 (6000.x) | C# 9+ | .NET Standard 2.1 | UI Toolkit")
            {
                style = { fontSize = 10, color = new StyleColor(NexusEditorStyles.TextSecondary), marginTop = 4 }
            });

            card.Add(new Label("New in v0.3.0: Auto-AOT generation, Thread-safe DI locking,\n" +
                               "Runtime performance metrics, ContextData validation,\n" +
                               "Enhanced Contexts inspector, Fluent API command detection,\n" +
                               "Recovery strategy integration, IReactiveModel+INexusService lifecycle.")
            {
                style = { fontSize = 9, color = new StyleColor(NexusEditorStyles.DimText), marginTop = 4, whiteSpace = WhiteSpace.Normal }
            });

            _scrollView.Add(card);
        }

        private void RenderSamples()
        {
            AddSection("SAMPLES", NexusEditorStyles.AccentOrange);

            var importBtn = NexusEditorStyles.CreateButton("Import Counter Sample", () =>
            {
                EditorApplication.ExecuteMenuItem("Window/Package Manager");
                Debug.Log("[Nexus] Open Package Manager → Nexus Observable Architecture → Samples to import the Counter example.");
            }, NexusEditorStyles.BtnBlue);
            _scrollView.Add(importBtn);

            var hint = NexusEditorStyles.CreateHint("The Counter example demonstrates a complete MVCS cycle: Model → Command → Signal → Mediator → View.");
            hint.style.marginTop = 4;
            _scrollView.Add(hint);
        }

        // ─── Helpers ───────────────────────────────────────────
        private void AddSection(string title, Color accent)
        {
            var header = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginTop = 12, marginBottom = 6 } };
            header.Add(NexusEditorStyles.CreateStatusDot(accent, 8));
            header.Add(new Label(title)
            {
                style = { fontSize = 12, unityFontStyleAndWeight = FontStyle.Bold, color = new StyleColor(NexusEditorStyles.TextPrimary), marginLeft = 6 }
            });
            _scrollView.Add(header);
        }

        private void AddStep(string title, string description)
        {
            var row = new VisualElement { style = { flexDirection = FlexDirection.Row, marginTop = 3, marginLeft = 15 } };
            row.Add(NexusEditorStyles.CreateStatusDot(NexusEditorStyles.AccentBlue, 5));
            var text = new Label($"<b>{title}</b>\n{description}")
            {
                style = { fontSize = 10, color = new StyleColor(NexusEditorStyles.TextPrimary), marginLeft = 5, whiteSpace = WhiteSpace.Normal, flexShrink = 1 }
            };
            row.Add(text);
            _scrollView.Add(row);
        }

        private void AddCard(string title, string content)
        {
            var card = new VisualElement
            {
                style =
                {
                    backgroundColor = new StyleColor(NexusEditorStyles.CardBg),
                    marginTop = 4,
                    marginBottom = 4,
                    marginLeft = 15,
                    paddingLeft = 10,
                    paddingRight = 10,
                    paddingTop = 6,
                    paddingBottom = 6,
                    borderTopLeftRadius = 4,
                    borderTopRightRadius = 4,
                    borderBottomLeftRadius = 4,
                    borderBottomRightRadius = 4,
                }
            };

            var titleLabel = new Label(title)
            {
                style = { fontSize = 10, unityFontStyleAndWeight = FontStyle.Bold, color = new StyleColor(NexusEditorStyles.AccentYellow), marginBottom = 3 }
            };
            card.Add(titleLabel);

            var contentLabel = new Label(content)
            {
                style = { fontSize = 9, color = new StyleColor(NexusEditorStyles.TextSecondary), whiteSpace = WhiteSpace.Normal }
            };
            card.Add(contentLabel);

            _scrollView.Add(card);
        }
    }
}

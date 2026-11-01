using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Core;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Kind = Nexus.Editor.ArchitectureMap.Kind;

namespace Nexus.Editor
{
    /// <summary>
    /// The Architecture tab (#193): one map of the game's signals, commands, services, models, views and mediators,
    /// with what handles each signal, what Nexus injects into each type and what receives it, a graph of the signals
    /// and their commands, and the form that fires a signal in a running context. It replaces the Explorer, Graph and
    /// Type Analyzer tabs and the Game Manager's lists of types.
    /// </summary>
    public class ArchitecturePlugin : NexusEditorPlugin
    {
        private static readonly Kind[] Kinds = { Kind.Signal, Kind.Command, Kind.Service, Kind.Model, Kind.View, Kind.Mediator };

        public override string Id => "Architecture";
        public override string DisplayName => NexusLang.Get("action_architecture_title");
        public override int Order => 1;
        public override string Category => "cat_architecture";
        public override Color IconColor => NexusEditorStyles.AccentPurple;

        // The filter and the selection survive a rebuilt view (a play-mode change, a language switch).
        private string _search = string.Empty;
        private Kind? _kind;
        private string _assembly;
        private bool _graphMode;
        private int _maxNodes = 80;
        private Type _selected;

        private VisualElement _body;
        private ScrollView _list;
        private ScrollView _details;
        private ArchitectureGraph _graph;
        private int _contextCount = -1;

        public override VisualElement CreateView()
        {
            var map = ArchitectureMap.Current;
            var view = new VisualElement { style = { flexGrow = 1 } };
            view.Add(NexusEditorStyles.CreateToolbar(DisplayName));
            view.Add(BuildSummary(map));
            view.Add(BuildFilters(map));

            var rule = NexusEditorStyles.CreateHint(NexusLang.Get("arch_rule"));
            rule.style.marginLeft = 10;
            rule.style.marginRight = 10;
            rule.style.marginBottom = 6;
            view.Add(rule);

            _body = new VisualElement { style = { flexGrow = 1 } };
            view.Add(_body);
            _contextCount = NexusRuntime.ActiveContexts.Count;
            Render();
            return view;
        }

        public override void OnUpdate()
        {
            _graph?.DrainHighlights();
            // A context that registers after the view was built brings its bindings with it.
            int count = NexusRuntime.ActiveContexts.Count;
            if (count == _contextCount || _body == null) return;
            _contextCount = count;
            Render();
        }

        public override void OnDisable()
        {
            _graph?.Detach();
            _graph = null;
            _body = null;
            _list = null;
            _details = null;
            base.OnDisable();
        }

        public override IReadOnlyList<(string Label, Action Action, Color Color)> GetContextActions()
            => new List<(string, Action, Color)> { (NexusLang.Get("arch_rescan"), Rescan, NexusEditorStyles.BtnGray) };

        private void Rescan()
        {
            ArchitectureMap.Invalidate();
            Window?.RefreshActivePlugin();
        }

        // ── Header ────────────────────────────────────────────────

        private VisualElement BuildSummary(ArchitectureMap map)
        {
            var row = new VisualElement { style = { flexDirection = FlexDirection.Row, flexWrap = Wrap.Wrap, paddingLeft = 10, paddingRight = 10, paddingTop = 6 } };
            foreach (var kind in Kinds)
            {
                var tile = NexusEditorStyles.CreateStatTile(KindName(kind), map.Count(kind).ToString(), KindColor(kind));
                tile.tooltip = NexusLang.Get("arch_summary_tooltip");
                var captured = kind;
                tile.RegisterCallback<ClickEvent>(_ => { _kind = _kind == captured ? null : captured; Window?.RefreshActivePlugin(); });
                row.Add(tile);
            }
            return row;
        }

        private VisualElement BuildFilters(ArchitectureMap map)
        {
            var row = new VisualElement { style = { flexDirection = FlexDirection.Row, flexWrap = Wrap.Wrap, alignItems = Align.Center, paddingLeft = 10, paddingRight = 10, paddingTop = 6, paddingBottom = 6 } };

            var search = new TextField(NexusLang.Get("arch_search")) { value = _search, style = { minWidth = 220, marginRight = 8 } };
            search.AddToClassList(NexusEditorStyles.ClassSearchField);
            search.RegisterValueChangedCallback(evt => { _search = evt.newValue ?? string.Empty; Render(); });
            row.Add(search);

            var kindChoices = new List<string> { NexusLang.Get("arch_kind_all") };
            foreach (var kind in Kinds) kindChoices.Add(KindName(kind));
            var kindField = new DropdownField(kindChoices, _kind.HasValue ? Array.IndexOf(Kinds, _kind.Value) + 1 : 0) { style = { minWidth = 120, marginRight = 8 } };
            kindField.RegisterValueChangedCallback(_ => { _kind = kindField.index <= 0 ? null : Kinds[kindField.index - 1]; Render(); });
            row.Add(kindField);

            var assemblyChoices = new List<string> { NexusLang.Get("arch_all_assemblies") };
            assemblyChoices.AddRange(map.Assemblies);
            if (_assembly != null && !map.Assemblies.Contains(_assembly)) _assembly = null;
            var assemblyField = new DropdownField(assemblyChoices, _assembly == null ? 0 : assemblyChoices.IndexOf(_assembly)) { style = { minWidth = 160, marginRight = 8 } };
            assemblyField.RegisterValueChangedCallback(_ => { _assembly = assemblyField.index <= 0 ? null : assemblyField.value; Render(); });
            row.Add(assemblyField);

            var modeButton = NexusEditorStyles.CreateButton(NexusLang.Get(_graphMode ? "arch_show_map" : "arch_show_graph"), null, NexusEditorStyles.BtnPurple);
            modeButton.clicked += () =>
            {
                _graphMode = !_graphMode;
                modeButton.text = NexusLang.Get(_graphMode ? "arch_show_map" : "arch_show_graph");
                Render();
            };
            row.Add(modeButton);
            return row;
        }

        // ── Body ──────────────────────────────────────────────────

        private void Render()
        {
            if (_body == null) return;
            _graph?.Detach();
            _graph = null;
            _body.Clear();

            var nodes = Filtered();
            if (_graphMode) RenderGraph(nodes);
            else RenderMap(nodes);
        }

        // The map's nodes the filter keeps, and while playing the signals the running contexts bound that no code names.
        private List<ArchitectureMap.Node> Filtered()
        {
            var map = ArchitectureMap.Current;
            var result = new List<ArchitectureMap.Node>();
            foreach (var node in map.Nodes)
                if (Matches(node.Type, node.Kind, node.Assembly)) result.Add(node);

            foreach (var signal in map.SignalsBoundOnlyAtRuntime(NexusRuntime.ActiveContexts))
            {
                var assembly = AssemblyCatalog.GetSimpleName(signal.Assembly);
                if (!Matches(signal, Kind.Signal, assembly)) continue;
                int index = result.FindIndex(n => n.Kind != Kind.Signal);
                result.Insert(index < 0 ? result.Count : index, new ArchitectureMap.Node(signal, Kind.Signal));
            }
            return result;
        }

        private bool Matches(Type type, Kind kind, string assembly)
        {
            if (_kind.HasValue && _kind.Value != kind) return false;
            if (_assembly != null && assembly != _assembly) return false;
            if (_search.Length == 0) return true;
            return type.Name.IndexOf(_search, StringComparison.OrdinalIgnoreCase) >= 0
                || (type.FullName?.IndexOf(_search, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0;
        }

        private void RenderMap(List<ArchitectureMap.Node> nodes)
        {
            var split = new VisualElement { style = { flexDirection = FlexDirection.Row, flexGrow = 1 } };
            _list = new ScrollView { style = { width = new Length(42, LengthUnit.Percent), borderRightWidth = 1, borderRightColor = new StyleColor(NexusEditorStyles.BorderColor) } };
            _details = new ScrollView { style = { flexGrow = 1, paddingLeft = 12, paddingRight = 12, paddingTop = 8 } };
            split.Add(_list);
            split.Add(_details);
            _body.Add(split);

            if (nodes.Count == 0)
            {
                _list.Add(NexusEditorStyles.CreateEmptyState(NexusLang.Get(ArchitectureMap.Current.Nodes.Count == 0 ? "arch_empty" : "arch_no_match")));
            }
            Kind? group = null;
            foreach (var node in nodes)
            {
                if (group != node.Kind)
                {
                    group = node.Kind;
                    var header = NexusEditorStyles.CreateSectionTitle($"{KindName(node.Kind)} ({nodes.Count(n => n.Kind == node.Kind)})");
                    header.style.marginLeft = 8;
                    _list.Add(header);
                }
                _list.Add(BuildRow(node));
            }

            var selected = nodes.FirstOrDefault(n => n.Type == _selected);
            if (selected != null) RenderDetails(selected);
            else _details.Add(NexusEditorStyles.CreateEmptyState(NexusLang.Get("arch_select")));
        }

        private VisualElement BuildRow(ArchitectureMap.Node node)
        {
            var row = new VisualElement { tooltip = node.Type.FullName };
            NexusEditorStyles.MakeRow(row, node.Type == _selected ? NexusEditorStyles.HighlightBg : Color.clear, clickable: true);
            row.style.marginLeft = 4;
            row.style.marginRight = 4;
            row.Add(NexusEditorStyles.CreateStatusDot(KindColor(node.Kind), 7));

            var text = new VisualElement { style = { marginLeft = 6, flexGrow = 1, flexShrink = 1 } };
            text.Add(new Label(node.Type.Name) { style = { color = new StyleColor(NexusEditorStyles.TextPrimary), unityFontStyleAndWeight = FontStyle.Bold } });
            var meta = RowMeta(node);
            if (meta.Length > 0)
                text.Add(new Label(meta) { style = { color = new StyleColor(NexusEditorStyles.TextSecondary), fontSize = 10, whiteSpace = WhiteSpace.Normal } });
            row.Add(text);
            row.RegisterCallback<ClickEvent>(_ => Select(node.Type));
            return row;
        }

        private static string RowMeta(ArchitectureMap.Node node)
        {
            switch (node.Kind)
            {
                case Kind.Signal:
                    return node.Handlers.Count == 0 ? NexusLang.Get("arch_runtime_only") : string.Join(", ", node.Handlers.Select(h => h.Command.Name).Distinct());
                case Kind.Command:
                    return node.Handlers.Count == 0 ? NexusLang.Get("arch_bound_in_code") : string.Join(", ", node.Handlers.Select(h => h.Signal.Name).Distinct());
                default:
                    return string.Join(", ", node.Related.Select(t => t.Name));
            }
        }

        private void Select(Type type)
        {
            _selected = type;
            if (_graphMode)
            {
                _graphMode = false;
                Window?.RefreshActivePlugin();
                return;
            }
            // A type the filter hides is shown with the filter cleared.
            if (!Filtered().Any(n => n.Type == type))
            {
                _search = string.Empty;
                _kind = null;
                _assembly = null;
                Window?.RefreshActivePlugin();
                return;
            }
            Render();
        }

        // ── Details ───────────────────────────────────────────────

        private void RenderDetails(ArchitectureMap.Node node)
        {
            var header = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, flexWrap = Wrap.Wrap } };
            header.Add(new Label(node.Type.Name) { style = { fontSize = 14, unityFontStyleAndWeight = FontStyle.Bold, color = new StyleColor(NexusEditorStyles.TextPrimary), marginRight = 8 } });
            header.Add(NexusEditorStyles.CreatePill(KindName(node.Kind), NexusEditorStyles.CardBgAlt, KindColor(node.Kind)));
            _details.Add(header);
            _details.Add(new Label($"{node.Type.FullName}  ·  {node.Assembly}") { style = { color = new StyleColor(NexusEditorStyles.TextSecondary), fontSize = 10, marginBottom = 4, whiteSpace = WhiteSpace.Normal } });

            var script = FindScript(node.Type);
            var open = NexusEditorStyles.CreateButton(NexusLang.Get("arch_open_script"), () => AssetDatabase.OpenAsset(script), NexusEditorStyles.BtnBlue);
            open.SetEnabled(script != null);
            if (script == null) open.tooltip = NexusLang.Get("arch_no_script");
            open.style.alignSelf = Align.FlexStart;
            _details.Add(open);

            switch (node.Kind)
            {
                case Kind.Signal: RenderSignal(node); return;
                case Kind.Command: RenderCommand(node); break;
                case Kind.Service: RenderRelated(node, "arch_implements"); break;
                case Kind.View: RenderRelated(node, "arch_mediators"); break;
                case Kind.Mediator: RenderRelated(node, "arch_view"); break;
            }
            RenderDependencies(node);
            RenderDependents(node);
        }

        private void RenderSignal(ArchitectureMap.Node node)
        {
            AddTitle(NexusLang.Get("arch_handled_by"));
            if (node.Handlers.Count == 0) AddHint(NexusLang.Get("arch_no_handler"));
            foreach (var handler in node.Handlers)
                _details.Add(HandlerRow(handler.Command, SourceText(handler), handler));

            if (Application.isPlaying)
            {
                AddTitle(NexusLang.Get("arch_bound"));
                var bound = ArchitectureMap.Bound(node.Type, NexusRuntime.ActiveContexts);
                if (bound.Count == 0) AddHint(NexusLang.Get("arch_bound_none"));
                foreach (var (scope, info) in bound)
                {
                    string text = string.Format(NexusLang.Get("arch_bound_row"), string.IsNullOrEmpty(scope) ? NexusLang.Get("fire_untagged_context") : scope, info.Mode, info.Priority);
                    _details.Add(HandlerRow(info.CommandType, text, null, info.IsAsync, info.IsOneShot));
                }
            }

            AddTitle(NexusLang.Get("arch_fire"));
            _details.Add(SignalFireForm.Create(node.Type));
        }

        private void RenderCommand(ArchitectureMap.Node node)
        {
            AddTitle(NexusLang.Get("arch_handles"));
            if (node.Handlers.Count == 0) AddHint(NexusLang.Get("arch_no_signal"));
            foreach (var handler in node.Handlers)
                _details.Add(HandlerRow(handler.Signal, SourceText(handler), handler));
        }

        private VisualElement HandlerRow(Type target, string text, ArchitectureMap.Handler? handler, bool isAsync = false, bool oneShot = false)
        {
            if (handler.HasValue)
            {
                isAsync = handler.Value.IsAsync;
                oneShot = handler.Value.OneShot;
            }
            var row = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, flexWrap = Wrap.Wrap, marginBottom = 3 } };
            row.Add(TypeLink(target));
            row.Add(new Label(text) { style = { color = new StyleColor(NexusEditorStyles.TextSecondary), fontSize = 10, marginLeft = 6, whiteSpace = WhiteSpace.Normal } });
            if (isAsync) row.Add(Tag(NexusLang.Get("arch_tag_async")));
            if (oneShot) row.Add(Tag(NexusLang.Get("arch_tag_oneshot")));
            return row;
        }

        private static string SourceText(ArchitectureMap.Handler handler)
        {
            switch (handler.Source)
            {
                case ArchitectureMap.Source.Attribute:
                    return string.Format(NexusLang.Get("arch_source_attribute"), handler.Mode, handler.Priority);
                case ArchitectureMap.Source.Composite:
                    return string.Format(NexusLang.Get("arch_source_composite"), handler.Priority);
                default:
                    return string.Format(NexusLang.Get("arch_source_implements"), (handler.IsAsync ? "IAsyncCommand<" : "ICommand<") + handler.Signal.Name + ">");
            }
        }

        private void RenderRelated(ArchitectureMap.Node node, string titleKey)
        {
            if (node.Related.Count == 0) return;
            AddTitle(NexusLang.Get(titleKey));
            var row = new VisualElement { style = { flexDirection = FlexDirection.Row, flexWrap = Wrap.Wrap } };
            foreach (var type in node.Related) row.Add(TypeLink(type));
            _details.Add(row);
        }

        private void RenderDependencies(ArchitectureMap.Node node)
        {
            AddTitle(NexusLang.Get("arch_dependencies"));
            if (node.Dependencies.Count == 0) AddHint(NexusLang.Get("arch_no_dependencies"));
            foreach (var dependency in node.Dependencies)
            {
                var row = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, flexWrap = Wrap.Wrap, marginBottom = 3 } };
                row.Add(new Label(dependency.Member) { style = { color = new StyleColor(NexusEditorStyles.TextSecondary), fontSize = 10, minWidth = 160 } });
                row.Add(TypeLink(dependency.Target));
                if (dependency.Optional) row.Add(Tag(NexusLang.Get("arch_optional")));
                _details.Add(row);
            }
        }

        private void RenderDependents(ArchitectureMap.Node node)
        {
            AddTitle(NexusLang.Get("arch_dependents"));
            if (node.Dependents.Count == 0) AddHint(NexusLang.Get("arch_no_dependents"));
            foreach (var dependent in node.Dependents)
            {
                var row = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, flexWrap = Wrap.Wrap, marginBottom = 3 } };
                row.Add(TypeLink(dependent.Owner));
                row.Add(new Label($"{dependent.Member} : {dependent.Target.Name}") { style = { color = new StyleColor(NexusEditorStyles.TextSecondary), fontSize = 10, marginLeft = 6 } });
                _details.Add(row);
            }
        }

        // A type the map lists opens in the map; any other type is plain text.
        private VisualElement TypeLink(Type type)
        {
            if (!ArchitectureMap.Current.TryGet(type, out _))
                return new Label(type.Name) { tooltip = type.FullName, style = { color = new StyleColor(NexusEditorStyles.TextPrimary), marginRight = 6 } };
            var link = new Button(() => Select(type)) { text = type.Name, tooltip = type.FullName };
            link.AddToClassList(NexusEditorStyles.ClassBtn);
            link.style.backgroundColor = new StyleColor(NexusEditorStyles.CardBgAlt);
            link.style.color = new StyleColor(NexusEditorStyles.AccentBlueText);
            return link;
        }

        private static VisualElement Tag(string text) => NexusEditorStyles.CreateTag(text, NexusEditorStyles.CardBgAlt, NexusEditorStyles.AccentOrange);

        private void AddTitle(string text)
        {
            var title = NexusEditorStyles.CreateSectionTitle(text);
            title.style.marginTop = 12;
            _details.Add(title);
        }

        private void AddHint(string text) => _details.Add(NexusEditorStyles.CreateHint(text));

        private static MonoScript FindScript(Type type)
        {
            foreach (var guid in AssetDatabase.FindAssets($"{type.Name} t:MonoScript"))
            {
                var script = AssetDatabase.LoadAssetAtPath<MonoScript>(AssetDatabase.GUIDToAssetPath(guid));
                if (script != null && script.GetClass() == type) return script;
            }
            return null;
        }

        // ── Graph ─────────────────────────────────────────────────

        private void RenderGraph(List<ArchitectureMap.Node> nodes)
        {
            var controls = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, paddingLeft = 10, paddingRight = 10, paddingBottom = 4 } };
            var limit = new IntegerField(NexusLang.Get("arch_graph_max_nodes")) { value = _maxNodes, isDelayed = true, style = { width = 170 } };
            limit.RegisterValueChangedCallback(evt => { _maxNodes = Mathf.Clamp(evt.newValue, 10, 2000); Render(); });
            controls.Add(limit);
            var status = new Label { style = { marginLeft = 10, color = new StyleColor(NexusEditorStyles.TextSecondary), fontSize = 10, flexGrow = 1, whiteSpace = WhiteSpace.Normal } };
            controls.Add(status);
            _body.Add(controls);

            // static edges, and while playing the commands the running contexts bound
            var contexts = NexusRuntime.ActiveContexts;
            var edges = new List<(Type, IReadOnlyList<Type>)>();
            foreach (var node in nodes)
            {
                if (node.Kind != Kind.Signal) continue;
                var commands = new List<Type>();
                foreach (var handler in node.Handlers)
                    if (!commands.Contains(handler.Command)) commands.Add(handler.Command);
                foreach (var (_, info) in ArchitectureMap.Bound(node.Type, contexts))
                    if (info.CommandType != null && !commands.Contains(info.CommandType)) commands.Add(info.CommandType);
                edges.Add((node.Type, commands));
            }

            if (edges.Count == 0)
            {
                _body.Add(NexusEditorStyles.CreateEmptyState(NexusLang.Get("arch_graph_empty")));
                return;
            }

            _graph = new ArchitectureGraph(Select);
            int leftOut = _graph.Draw(edges, _maxNodes);
            status.text = leftOut > 0
                ? string.Format(NexusLang.Get("arch_graph_left_out"), leftOut)
                : string.Format(NexusLang.Get("arch_graph_stats"), edges.Count, edges.Sum(e => e.Item2.Count));
            _body.Add(_graph.Element);
            _graph.Attach();
        }

        // ── Kinds ─────────────────────────────────────────────────

        private static string KindName(Kind kind) => NexusLang.Get("arch_kind_" + kind.ToString().ToLowerInvariant());

        private static Color KindColor(Kind kind)
        {
            switch (kind)
            {
                case Kind.Signal: return NexusEditorStyles.AccentPurple;
                case Kind.Command: return NexusEditorStyles.AccentOrange;
                case Kind.Service: return NexusEditorStyles.AccentGreen;
                case Kind.Model: return NexusEditorStyles.AccentYellow;
                case Kind.View: return NexusEditorStyles.AccentBlue;
                default: return NexusEditorStyles.AccentRed;
            }
        }
    }
}

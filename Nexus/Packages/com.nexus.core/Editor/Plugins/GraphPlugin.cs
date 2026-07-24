using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using UnityEditor;
using UnityEditor.Experimental.GraphView;
using UnityEngine;
using UnityEngine.UIElements;
using Nexus.Core;

namespace Nexus.Editor
{
    public class GraphPlugin : NexusEditorPlugin, INexusTraceSink
    {
        public override string Id => "Graph";
        public override string DisplayName => NexusLang.Get("action_graph_title");
        public override int Order => 5;

        private int _maxNodes = 50;

        private VisualElement _view;
        private SignalGraphView _graphView;
        private Label _statusLabel;

        private Dictionary<string, Node> _signalNodes = new();
        private Dictionary<string, Node> _handlerNodes = new();
        private int _totalEdgeCount;

        // Trace sink Write() may be called from any thread; marshal highlights to the
        // main (UI) thread via a lock-free queue drained on the view schedule.
        private readonly ConcurrentQueue<(bool isSignal, string typeName)> _highlightQueue = new();
        private IVisualElementScheduledItem _drainSchedule;

        public override VisualElement CreateView()
        {
            _view = new VisualElement { style = { flexGrow = 1 } };

            var toolbar = NexusEditorStyles.CreateToolbar(NexusLang.Get("graph_title"));
            _view.Add(toolbar);

            // Status bar below toolbar
            _statusLabel = new Label(NexusLang.Get("graph_ready"))
            {
                style = { fontSize = 10, color = new StyleColor(NexusEditorStyles.TextSecondary), paddingLeft = 10, paddingTop = 4, paddingBottom = 4,
                    borderBottomWidth = 1, borderBottomColor = new StyleColor(NexusEditorStyles.BorderColor) }
            };
            _view.Add(_statusLabel);

            _graphView = new SignalGraphView();
            _graphView.style.flexGrow = 1;
            _view.Add(_graphView);

            var refreshBtn = NexusEditorStyles.CreateButton(NexusLang.Get("graph_refresh"), BuildGraph, NexusEditorStyles.BtnBlue);
            refreshBtn.style.position = Position.Absolute;
            refreshBtn.style.top = 58;
            refreshBtn.style.right = 10;
            _view.Add(refreshBtn);

            // Adjustable node budget — replaces the former hard 50-node cap.
            var limitField = new IntegerField("Max Nodes") { value = _maxNodes };
            limitField.style.position = Position.Absolute;
            limitField.style.top = 58;
            limitField.style.right = 92;
            limitField.style.width = 130;
            limitField.RegisterValueChangedCallback(evt =>
            {
                _maxNodes = Mathf.Clamp(evt.newValue, 10, 2000);
                BuildGraph();
            });
            _view.Add(limitField);

            BuildGraph();
            NexusTrace.AddSink(this);
            _drainSchedule = _view.schedule.Execute(DrainHighlights).Every(100);

            return _view;
        }

        public override void OnDisable()
        {
            _drainSchedule?.Pause();
            NexusTrace.RemoveSink(this);
            _signalNodes.Clear();
            _handlerNodes.Clear();
            while (_highlightQueue.TryDequeue(out _)) { }
        }

        private void BuildGraph()
        {
            _graphView.ClearGraph();
            _signalNodes.Clear();
            _handlerNodes.Clear();
            _totalEdgeCount = 0;

            var runtimeMappings = CollectRuntimeMappings();
            if (runtimeMappings != null)
            {
                DrawGraph(runtimeMappings);
                return;
            }

            var attributeMappings = CollectAttributeMappings();
            DrawGraph(attributeMappings);
        }

        private static Dictionary<Type, List<Type>> CollectRuntimeMappings()
        {
            var contexts = NexusRuntime.ActiveContexts;
            if (contexts == null || contexts.Count == 0) return null;

            var mappings = new Dictionary<Type, List<Type>>();
            foreach (var ctx in contexts)
            {
                if (ctx.SignalBus == null) continue;
                var handlers = ctx.SignalBus.RegisteredHandlers;
                if (handlers == null || handlers.Count == 0) continue;
                foreach (var kvp in handlers)
                {
                    if (!mappings.ContainsKey(kvp.Key))
                        mappings[kvp.Key] = new List<Type>();
                    foreach (var info in kvp.Value)
                    {
                        if (info.CommandType != null && !mappings[kvp.Key].Contains(info.CommandType))
                            mappings[kvp.Key].Add(info.CommandType);
                    }
                }
            }
            return mappings;
        }

        private static Dictionary<Type, List<Type>> CollectAttributeMappings()
        {
            var mappings = new Dictionary<Type, List<Type>>();
            foreach (var assembly in UnityEngine.Assemblies.CurrentAssemblies.GetLoadedAssemblies())
            {
                var name = assembly.GetName().Name;
                if (name.StartsWith("System") || name.StartsWith("mscorlib") || name.StartsWith("Mono") ||
                    name.StartsWith("UnityEngine") || (name.StartsWith("UnityEditor") && !name.Contains("com.nexus")))
                    continue;

                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (System.Reflection.ReflectionTypeLoadException ex) { types = ex.Types; }

                foreach (var type in types)
                {
                    if (type != null && type.IsClass && !type.IsAbstract)
                    {
                        var attrs = type.GetCustomAttributes(typeof(SignalHandlerAttribute), false);
                        foreach (SignalHandlerAttribute attr in attrs)
                        {
                            if (!mappings.ContainsKey(attr.SignalType))
                                mappings[attr.SignalType] = new List<Type>();
                            mappings[attr.SignalType].Add(type);
                        }
                    }
                }
            }
            return mappings;
        }

        private void DrawGraph(Dictionary<Type, List<Type>> mappings)
        {
            // Count total signals + commands
            int signalCount = mappings.Count;
            int cmdCount = 0;
            foreach (var kvp in mappings) cmdCount += kvp.Value.Count;
            int totalNodes = signalCount + cmdCount;

            if (mappings == null || mappings.Count == 0)
            {
                _statusLabel.text = NexusLang.Get("graph_no_mappings");
                _statusLabel.style.color = new StyleColor(NexusEditorStyles.TextSecondary);
                return;
            }

            if (totalNodes > _maxNodes)
            {
                var warnLabel = new Label(
                    string.Format(NexusLang.Get("graph_overflow_desc"), totalNodes, _maxNodes))
                {
                    style = { color = new StyleColor(NexusEditorStyles.AccentOrange), fontSize = 11,
                        alignSelf = Align.Center, marginTop = 8, whiteSpace = WhiteSpace.Normal }
                };
                _graphView.Add(warnLabel);
                // Fall through and render a partial graph up to the node budget.
            }
            else
            {
                _statusLabel.text = string.Format(NexusLang.Get("graph_stats"), signalCount, cmdCount, totalNodes, _totalEdgeCount);
                _statusLabel.style.color = new StyleColor(NexusEditorStyles.TextSecondary);
            }

            int yOffset = 0;
            int edgeCount = 0;
            int nodesCreated = 0;
            bool truncated = false;
            foreach (var kvp in mappings)
            {
                if (nodesCreated >= _maxNodes) { truncated = true; break; }
                var signalType = kvp.Key;
                var handlerTypes = kvp.Value;

                int handlerCount = handlerTypes.Count;
                string mode = "Sequential";

                // Try to get mode from runtime registrations
                var contexts = NexusRuntime.ActiveContexts;
                if (contexts != null && contexts.Count > 0 && contexts[0].SignalBus != null)
                {
                    var handlers = contexts[0].SignalBus.RegisteredHandlers;
                    if (handlers != null && handlers.TryGetValue(signalType, out var infos) && infos.Count > 0)
                    {
                        mode = infos[0].Mode.ToString();
                        handlerCount = infos.Count;
                    }
                }

                var signalNode = _graphView.CreateSignalNode(signalType.Name, new Vector2(100, yOffset));
                signalNode.tooltip = $"{signalType.FullName}\nHandlers: {handlerCount}\nMode: {mode}";
                _graphView.AddElement(signalNode);
                _signalNodes[signalType.Name] = signalNode;
                nodesCreated++;

                int handlerYOffset = yOffset;
                foreach (var handlerType in handlerTypes)
                {
                    if (nodesCreated >= _maxNodes) { truncated = true; break; }
                    var handlerNode = _graphView.CreateHandlerNode(handlerType.Name, new Vector2(400, handlerYOffset));
                    handlerNode.tooltip = $"{handlerType.FullName}";
                    _graphView.AddElement(handlerNode);
                    _handlerNodes[handlerType.Name] = handlerNode;
                    nodesCreated++;

                    var edge = _graphView.ConnectNodes(
                        signalNode.outputContainer.Q<Port>(),
                        handlerNode.inputContainer.Q<Port>());
                    if (edge != null)
                    {
                        _graphView.AddElement(edge);
                        edgeCount++;
                    }

                    handlerYOffset += 100;
                }

                yOffset = Math.Max(yOffset + 150, handlerYOffset + 50);
            }
            _totalEdgeCount = edgeCount;
            if (truncated)
            {
                _statusLabel.text = string.Format(NexusLang.Get("graph_overflow"), totalNodes, _maxNodes);
                _statusLabel.style.color = new StyleColor(NexusEditorStyles.AccentOrange);
            }
            else
            {
                _statusLabel.text = string.Format(NexusLang.Get("graph_stats"), signalCount, cmdCount, totalNodes, edgeCount);
            }
        }

        public void Write(in TraceEvent traceEvent)
        {
            // Called from arbitrary threads under NexusTrace's lock — do no UI work here.
            if (traceEvent.Type == TraceEventType.Signal)
                _highlightQueue.Enqueue((true, traceEvent.TypeName));
            else if (traceEvent.Type == TraceEventType.Command)
                _highlightQueue.Enqueue((false, traceEvent.TypeName));
        }

        private void DrainHighlights()
        {
            while (_highlightQueue.TryDequeue(out var item))
            {
                if (item.isSignal)
                {
                    if (_signalNodes.TryGetValue(item.typeName, out var node))
                        HighlightNode(node, new Color(0.2f, 0.8f, 0.2f, 0.8f));
                }
                else
                {
                    if (_handlerNodes.TryGetValue(item.typeName, out var node))
                        HighlightNode(node, new Color(0.2f, 0.8f, 0.8f, 0.8f));
                }
            }
        }

        private void HighlightNode(Node node, Color flashColor)
        {
            var origColor = node.mainContainer.style.backgroundColor;
            node.mainContainer.style.backgroundColor = new StyleColor(flashColor);
            node.schedule.Execute(() => {
                node.mainContainer.style.backgroundColor = origColor;
            }).StartingIn(500);
        }
    }

    public class SignalGraphView : GraphView
    {
        public SignalGraphView()
        {
            this.AddManipulator(new ContentDragger());
            this.AddManipulator(new SelectionDragger());
            this.AddManipulator(new RectangleSelector());
            this.AddManipulator(new ContentZoomer());

            // Performance: disable pixel cache regen above 30 elements
            zoomerMaxElementCountWithPixelCacheRegen = 30;

            var grid = new GridBackground();
            Insert(0, grid);
            grid.StretchToParentSize();
        }

        public void ClearGraph()
        {
            DeleteElements(graphElements.ToList());
        }

        public Node CreateSignalNode(string signalName, Vector2 position)
        {
            var node = new Node { title = signalName, style = { width = 150 } };
            node.SetPosition(new Rect(position, Vector2.zero));
            node.mainContainer.style.backgroundColor = new StyleColor(new Color(0.2f, 0.4f, 0.2f, 0.8f));

            var outputPort = node.InstantiatePort(Orientation.Horizontal, Direction.Output, Port.Capacity.Multi, typeof(bool));
            outputPort.portName = "▶";
            node.outputContainer.Add(outputPort);
            node.RefreshPorts();

            return node;
        }

        public Node CreateHandlerNode(string handlerName, Vector2 position)
        {
            var node = new Node { title = handlerName, style = { width = 150 } };
            node.SetPosition(new Rect(position, Vector2.zero));
            node.mainContainer.style.backgroundColor = new StyleColor(new Color(0.2f, 0.3f, 0.5f, 0.8f));

            var inputPort = node.InstantiatePort(Orientation.Horizontal, Direction.Input, Port.Capacity.Multi, typeof(bool));
            inputPort.portName = "◀";
            node.inputContainer.Add(inputPort);
            node.RefreshPorts();

            return node;
        }

        public Edge ConnectNodes(Port outputPort, Port inputPort)
        {
            if (outputPort == null || inputPort == null) return null;
            var edge = new Edge { output = outputPort, input = inputPort };
            edge.input.Connect(edge);
            edge.output.Connect(edge);
            return edge;
        }
    }
}

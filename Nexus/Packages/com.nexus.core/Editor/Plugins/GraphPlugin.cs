using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
        public override string DisplayName => "Signal Graph";
        public override int Order => 5;

        private VisualElement _view;
        private SignalGraphView _graphView;
        
        // Cache to colorize nodes during trace
        private Dictionary<string, Node> _signalNodes = new();
        private Dictionary<string, Node> _handlerNodes = new();

        public override VisualElement CreateView()
        {
            _view = new VisualElement { style = { flexGrow = 1 } };
            
            var toolbar = NexusEditorStyles.CreateToolbar("LIVE SIGNAL GRAPH MAP");
            _view.Add(toolbar);

            _graphView = new SignalGraphView();
            _graphView.style.flexGrow = 1;
            _view.Add(_graphView);

            var refreshBtn = NexusEditorStyles.CreateButton("Refresh Graph", BuildGraph, NexusEditorStyles.BtnBlue);
            refreshBtn.style.position = Position.Absolute;
            refreshBtn.style.top = 35;
            refreshBtn.style.right = 10;
            _view.Add(refreshBtn);

            BuildGraph();

            // Hook into NexusTrace for live node animations
            NexusTrace.AddSink(this);

            return _view;
        }

        public override void OnDisable()
        {
            NexusTrace.RemoveSink(this);
            _signalNodes.Clear();
            _handlerNodes.Clear();
        }

        private void BuildGraph()
        {
            _graphView.ClearGraph();
            _signalNodes.Clear();
            _handlerNodes.Clear();

            // Scan assemblies for Handlers and map them
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            var mappings = new Dictionary<Type, List<Type>>(); // SignalType -> List of HandlerTypes

            foreach (var assembly in assemblies)
            {
                var assemblyName = assembly.GetName().Name;
                if (assemblyName.StartsWith("System") || assemblyName.StartsWith("mscorlib") || assemblyName.StartsWith("Mono") || 
                    assemblyName.StartsWith("UnityEngine") || 
                    (assemblyName.StartsWith("UnityEditor") && !assemblyName.Contains("com.nexus")))
                {
                    continue;
                }

                foreach (var type in assembly.GetTypes())
                {
                    if (type.IsClass && !type.IsAbstract)
                    {
                        var attrs = type.GetCustomAttributes<SignalHandlerAttribute>();
                        foreach (var attr in attrs)
                        {
                            if (!mappings.ContainsKey(attr.SignalType))
                                mappings[attr.SignalType] = new List<Type>();
                            
                            mappings[attr.SignalType].Add(type);
                        }
                    }
                }
            }

            // Draw Nodes
            int yOffset = 0;
            foreach (var kvp in mappings)
            {
                var signalType = kvp.Key;
                var handlerTypes = kvp.Value;

                var signalNode = _graphView.CreateSignalNode(signalType.Name, new Vector2(100, yOffset));
                _graphView.AddElement(signalNode);
                _signalNodes[signalType.Name] = signalNode;

                int handlerYOffset = yOffset;
                foreach (var handlerType in handlerTypes)
                {
                    var handlerNode = _graphView.CreateHandlerNode(handlerType.Name, new Vector2(400, handlerYOffset));
                    _graphView.AddElement(handlerNode);
                    _handlerNodes[handlerType.Name] = handlerNode;

                    var edge = _graphView.ConnectNodes(signalNode.outputContainer.Q<Port>(), handlerNode.inputContainer.Q<Port>());
                    if (edge != null)
                        _graphView.AddElement(edge);

                    handlerYOffset += 100;
                }

                yOffset = Math.Max(yOffset + 150, handlerYOffset + 50);
            }
        }

        public void Write(in TraceEvent traceEvent)
        {
            if (traceEvent.Type == TraceEventType.Signal)
            {
                if (_signalNodes.TryGetValue(traceEvent.TypeName, out var node))
                {
                    // Basic animation/highlight effect
                    HighlightNode(node, new Color(0.2f, 0.8f, 0.2f, 0.8f));
                }
            }
            else if (traceEvent.Type == TraceEventType.Command)
            {
                if (_handlerNodes.TryGetValue(traceEvent.TypeName, out var node))
                {
                    HighlightNode(node, new Color(0.2f, 0.8f, 0.8f, 0.8f));
                }
            }
        }

        private void HighlightNode(Node node, Color flashColor)
        {
            var origColor = node.mainContainer.style.backgroundColor;
            node.mainContainer.style.backgroundColor = new StyleColor(flashColor);
            
            // Revert after 500ms using UI Toolkit schedule
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
            var node = new Node
            {
                title = signalName,
                style = { width = 150 }
            };
            node.SetPosition(new Rect(position, Vector2.zero));
            
            node.mainContainer.style.backgroundColor = new StyleColor(new Color(0.2f, 0.4f, 0.2f, 0.8f));

            var outputPort = node.InstantiatePort(Orientation.Horizontal, Direction.Output, Port.Capacity.Multi, typeof(bool));
            outputPort.portName = "Fires";
            node.outputContainer.Add(outputPort);

            return node;
        }

        public Node CreateHandlerNode(string handlerName, Vector2 position)
        {
            var node = new Node
            {
                title = handlerName,
                style = { width = 150 }
            };
            node.SetPosition(new Rect(position, Vector2.zero));

            node.mainContainer.style.backgroundColor = new StyleColor(new Color(0.2f, 0.3f, 0.5f, 0.8f));

            var inputPort = node.InstantiatePort(Orientation.Horizontal, Direction.Input, Port.Capacity.Multi, typeof(bool));
            inputPort.portName = "Listens";
            node.inputContainer.Add(inputPort);

            return node;
        }

        public Edge ConnectNodes(Port outputPort, Port inputPort)
        {
            if (outputPort == null || inputPort == null) return null;
            var edge = new Edge
            {
                output = outputPort,
                input = inputPort
            };
            edge.input.Connect(edge);
            edge.output.Connect(edge);
            return edge;
        }
    }
}

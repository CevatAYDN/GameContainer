using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Nexus.Core;
using UnityEditor.Experimental.GraphView;
using UnityEngine;
using UnityEngine.UIElements;

namespace Nexus.Editor
{
    /// <summary>
    /// The Architecture tab's graph (#193): each signal on the left, one node per command on the right, an edge for every
    /// signal a command handles. A node flashes when the trace reports its signal or command; a double-click opens the
    /// type in the map.
    /// </summary>
    internal sealed class ArchitectureGraph : INexusTraceSink
    {
        private static readonly Color SignalColor = new(0.2f, 0.4f, 0.2f, 0.8f);
        private static readonly Color CommandColor = new(0.2f, 0.3f, 0.5f, 0.8f);

        private readonly GraphCanvas _canvas = new();
        private readonly Action<Type> _open;
        private readonly Dictionary<string, Node> _signalsByName = new();
        private readonly Dictionary<string, Node> _commandsByName = new();
        // Write() runs on any thread under the trace lock; the highlights are drawn on the editor thread.
        private readonly ConcurrentQueue<(bool IsSignal, string Name)> _pending = new();
        private readonly List<IVisualElementScheduledItem> _flashes = new();

        internal ArchitectureGraph(Action<Type> open)
        {
            _open = open;
            _canvas.style.flexGrow = 1;
        }

        internal VisualElement Element => _canvas;

        /// <summary>Draws <paramref name="edges"/> up to <paramref name="maxNodes"/> nodes; returns how many nodes it left out.</summary>
        internal int Draw(IReadOnlyList<(Type Signal, IReadOnlyList<Type> Commands)> edges, int maxNodes)
        {
            _canvas.DeleteElements(_canvas.graphElements.ToList());
            _signalsByName.Clear();
            _commandsByName.Clear();

            var commandNodes = new Dictionary<Type, Node>();
            int drawn = 0, total = 0, signalY = 0, commandY = 0;
            foreach (var (signal, commands) in edges)
            {
                total += 1;
                foreach (var command in commands) if (!commandNodes.ContainsKey(command)) total++;
                if (drawn >= maxNodes) continue;

                var signalNode = CreateNode(signal, SignalColor, new Vector2(80, signalY), Direction.Output);
                _signalsByName[signal.Name] = signalNode;
                drawn++;
                foreach (var command in commands)
                {
                    if (!commandNodes.TryGetValue(command, out var commandNode))
                    {
                        if (drawn >= maxNodes) break;
                        commandNode = CreateNode(command, CommandColor, new Vector2(420, Math.Max(commandY, signalY)), Direction.Input);
                        commandNodes[command] = commandNode;
                        _commandsByName[command.Name] = commandNode;
                        commandY = Math.Max(commandY, signalY) + 90;
                        drawn++;
                    }
                    Connect(signalNode, commandNode);
                }
                signalY = Math.Max(signalY + 90, commandY);
            }
            return total - drawn;
        }

        private Node CreateNode(Type type, Color color, Vector2 position, Direction direction)
        {
            var node = new Node { title = type.Name, tooltip = type.FullName, userData = type, style = { width = 180 } };
            node.SetPosition(new Rect(position, Vector2.zero));
            node.mainContainer.style.backgroundColor = new StyleColor(color);
            var port = node.InstantiatePort(Orientation.Horizontal, direction, Port.Capacity.Multi, typeof(bool));
            port.portName = string.Empty;
            (direction == Direction.Output ? node.outputContainer : node.inputContainer).Add(port);
            node.RefreshPorts();
            node.RegisterCallback<MouseDownEvent>(evt =>
            {
                if (evt.clickCount == 2) _open?.Invoke(type);
            });
            _canvas.AddElement(node);
            return node;
        }

        private void Connect(Node signal, Node command)
        {
            var output = signal.outputContainer.Q<Port>();
            var input = command.inputContainer.Q<Port>();
            var edge = new Edge { output = output, input = input };
            output.Connect(edge);
            input.Connect(edge);
            _canvas.AddElement(edge);
        }

        internal void Attach() => NexusTrace.AddSink(this);

        internal void Detach()
        {
            NexusTrace.RemoveSink(this);
            foreach (var flash in _flashes) flash.Pause();
            _flashes.Clear();
            while (_pending.TryDequeue(out _)) { }
        }

        public void Write(in TraceEvent traceEvent)
        {
            if (traceEvent.Type == TraceEventType.Signal) _pending.Enqueue((true, traceEvent.TypeName));
            else if (traceEvent.Type == TraceEventType.Command) _pending.Enqueue((false, traceEvent.TypeName));
        }

        /// <summary>Flashes the nodes the trace reported since the last call; call it on the editor thread.</summary>
        internal void DrainHighlights()
        {
            while (_pending.TryDequeue(out var item))
            {
                var nodes = item.IsSignal ? _signalsByName : _commandsByName;
                if (item.Name != null && nodes.TryGetValue(item.Name, out var node)) Flash(node, item.IsSignal);
            }
        }

        private void Flash(Node node, bool isSignal)
        {
            var original = node.mainContainer.style.backgroundColor;
            node.mainContainer.style.backgroundColor = new StyleColor(isSignal ? new Color(0.2f, 0.8f, 0.2f, 0.8f) : new Color(0.2f, 0.8f, 0.8f, 0.8f));
            IVisualElementScheduledItem flash = null;
            flash = node.schedule.Execute(() =>
            {
                node.mainContainer.style.backgroundColor = original;
                _flashes.Remove(flash);
            }).StartingIn(500);
            _flashes.Add(flash);
        }

        private sealed class GraphCanvas : GraphView
        {
            internal GraphCanvas()
            {
                this.AddManipulator(new ContentDragger());
                this.AddManipulator(new SelectionDragger());
                this.AddManipulator(new RectangleSelector());
                this.AddManipulator(new ContentZoomer());
                // no pixel-cache regeneration while zooming a large graph
                zoomerMaxElementCountWithPixelCacheRegen = 30;
                var grid = new GridBackground();
                Insert(0, grid);
                grid.StretchToParentSize();
            }
        }
    }
}

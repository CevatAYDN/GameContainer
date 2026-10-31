using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace Nexus.Core.Services
{
    /// <summary>Pads a UI Toolkit element by the device's safe area (ADR-0003).</summary>
    public static class SafeArea
    {
        /// <summary>
        /// Pads the element by the safe-area insets of <see cref="Screen.safeArea"/> in panel units and keeps them current
        /// on every geometry change of the element (rotation, cutout, panel resize). Dispose to stop and clear the padding.
        /// </summary>
        public static IDisposable Pad(VisualElement element)
        {
            if (element == null) throw new ArgumentNullException(nameof(element));
            var padding = new Padding(element);
            element.RegisterCallback<GeometryChangedEvent>(padding.OnGeometryChanged);
            padding.Apply();
            return padding;
        }

        /// <summary>
        /// The insets left, top, right, bottom in panel units: the screen's safe area (pixels, origin bottom left) scaled
        /// by the panel's size over the screen's.
        /// </summary>
        internal static Vector4 PanelInsets(Rect safeArea, Vector2 screen, Vector2 panel)
        {
            if (screen.x <= 0 || screen.y <= 0) return Vector4.zero;
            float x = panel.x / screen.x, y = panel.y / screen.y;
            return new Vector4(
                Mathf.Max(0, safeArea.xMin) * x,
                Mathf.Max(0, screen.y - safeArea.yMax) * y,
                Mathf.Max(0, screen.x - safeArea.xMax) * x,
                Mathf.Max(0, safeArea.yMin) * y);
        }

        private sealed class Padding : IDisposable
        {
            private VisualElement _element;
            private Vector4 _applied = new Vector4(-1, -1, -1, -1);

            public Padding(VisualElement element) => _element = element;

            public void OnGeometryChanged(GeometryChangedEvent evt) => Apply();

            public void Apply()
            {
                var root = _element?.panel?.visualTree;
                if (root == null) return;
                var insets = PanelInsets(Screen.safeArea, new Vector2(Screen.width, Screen.height), root.layout.size);
                if (insets == _applied) return;
                _applied = insets;
                _element.style.paddingLeft = insets.x;
                _element.style.paddingTop = insets.y;
                _element.style.paddingRight = insets.z;
                _element.style.paddingBottom = insets.w;
            }

            public void Dispose()
            {
                if (_element == null) return;
                _element.UnregisterCallback<GeometryChangedEvent>(OnGeometryChanged);
                _element.style.paddingLeft = StyleKeyword.Null;
                _element.style.paddingTop = StyleKeyword.Null;
                _element.style.paddingRight = StyleKeyword.Null;
                _element.style.paddingBottom = StyleKeyword.Null;
                _element = null;
            }
        }
    }
}

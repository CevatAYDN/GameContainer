// Minimal compile-time stubs so the pure-C# Nexus runtime compiles and RUNS outside Unity.
// Beyond compiling, the Object/GameObject/Transform surface below is FUNCTIONAL: it keeps
// a registry of live objects and a small scene graph (parent/children, components,
// SetActive) so the harness can exercise real Root hierarchy, ViewBinder/Mediator and
// ObjectPoolService logic end-to-end instead of timing an empty path.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;

namespace UnityEngine
{
    /// <summary>Unity log severity — a no-op enum outside Unity.</summary>
    public enum LogType
    {
        Error = 0,
        Assert = 1,
        Warning = 2,
        Log = 3,
        Exception = 4
    }

    /// <summary>Unity log callback signature.</summary>
    public delegate void LogCallback(string condition, string stackTrace, LogType type);

    /// <summary>Unity application surface used by the runtime outside Unity.</summary>
    public static class Application
    {
        public static bool isPlaying => false;
        /// <summary>Mirrors UnityEngine.Application.isEditor; false in the benchmark harness.</summary>
        public static bool isEditor => false;
#pragma warning disable 0067 // events mirror the Unity surface; nothing raises them in the harness
        public static event LogCallback logMessageReceivedThreaded;
        public static event Action<bool> focusChanged;
        public static event Action quitting;
#pragma warning restore 0067

        public static string identifier => "com.nexus.benchmark";
        public static string version => "9.9.9";
        // Distinct from persistentDataPath so tests that need data-vs-save path
        // separation don't collide (also lets the editor codegen stub compile).
        public static string dataPath => Path.Combine(Path.GetTempPath(), "NexusBenchmarkData");
        public static string persistentDataPath
        {
            get
            {
                var dir = Path.Combine(Path.GetTempPath(), "NexusBenchmark");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                return dir;
            }
        }
    }

    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public class DisallowMultipleComponentAttribute : Attribute {}

    /// <summary>Unity device info — a fixed identity outside Unity.</summary>
    public static class SystemInfo
    {
        public static string deviceUniqueIdentifier => "NexusBenchmarkDevice";
    }

    /// <summary>Unity PlayerPrefs — in-memory store outside Unity.</summary>
    public static class PlayerPrefs
    {
        private static readonly Dictionary<string, string> s_store = new();
        private static readonly object s_lock = new();

        public static string GetString(string key, string defaultValue = "")
        {
            lock (s_lock) { return s_store.TryGetValue(key, out var v) ? v : defaultValue; }
        }
        public static void SetString(string key, string value)
        {
            lock (s_lock) { s_store[key] = value; }
        }
        public static int GetInt(string key, int defaultValue = 0)
        {
            string v = GetString(key, null);
            return v != null && int.TryParse(v, out int r) ? r : defaultValue;
        }
        public static void SetInt(string key, int value) => SetString(key, value.ToString(CultureInfo.InvariantCulture));
        public static float GetFloat(string key, float defaultValue = 0f)
        {
            string v = GetString(key, null);
            return v != null && float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out float r) ? r : defaultValue;
        }
        public static void SetFloat(string key, float value) => SetString(key, value.ToString(CultureInfo.InvariantCulture));
        public static bool HasKey(string key)
        {
            lock (s_lock) { return s_store.ContainsKey(key); }
        }
        public static void DeleteKey(string key)
        {
            lock (s_lock) { s_store.Remove(key); }
        }
        public static void Save() { }

        internal static void ClearAll()
        {
            lock (s_lock) { s_store.Clear(); }
        }
    }

    /// <summary>Unity runtime init load type — no-op outside Unity.</summary>
    public enum RuntimeInitializeLoadType
    {
        AfterSceneLoad = 0,
        BeforeSceneLoad = 1,
        AfterAssembliesLoaded = 2,
        BeforeSplashScreen = 3,
        SubsystemRegistration = 4
    }

    /// <summary>Unity runtime-init marker — a no-op outside Unity.</summary>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class RuntimeInitializeOnLoadMethodAttribute : Attribute
    {
        public RuntimeInitializeOnLoadMethodAttribute() { }
        public RuntimeInitializeOnLoadMethodAttribute(RuntimeInitializeLoadType loadType) { }
    }

    /// <summary>Unity Debug — a no-op outside Unity.</summary>
    public static class Debug
    {
        public static void Log(object message) { }
        public static void LogWarning(object message) { }
        public static void LogError(object message) { }
        public static void LogException(Exception exception) { }
        /// <summary>Mirrors UnityEngine.Debug.isDebugBuild; false in the benchmark harness.</summary>
        public static bool isDebugBuild => false;
    }

    /// <summary>Unity Time — a no-op outside Unity.</summary>
    public static class Time
    {
        public static float time => 0f;
        public static float deltaTime => 0.0166f;
        public static float unscaledDeltaTime => 0.0166f;
        public static float fixedDeltaTime => 0.02f;
        public static float timeScale { get; set; } = 1f;
        public static double realtimeSinceStartupAsDouble => 0d;
        /// <summary>Monotonic runtime clock — settable so suites can advance time (AdService cooldowns).</summary>
        public static float realtimeSinceStartup { get; set; } = 0f;
        public static int frameCount => 0;
    }

    /// <summary>Unity math helpers — a no-op outside Unity.</summary>
    public static class Mathf
    {
        public static float Max(float a, float b) => a > b ? a : b;
        public static float Clamp01(float value) => value < 0f ? 0f : (value > 1f ? 1f : value);
    }

    /// <summary>Unity stack-trace helper used by ErrorCollection — a no-op outside Unity.</summary>
    public static class StackTraceUtility
    {
        public static string ExtractStackTrace() => string.Empty;
        public static string ExtractStringFromException(Exception exception) => exception?.ToString() ?? string.Empty;
    }

    /// <summary>Unity serialization helper — minimal reflection-based JSON for the harness.</summary>
    public static class JsonUtility
    {
        public static string ToJson(object obj)
        {
            if (obj == null) return "{}";
            var sb = new StringBuilder();
            sb.Append('{');
            bool first = true;
            foreach (var field in obj.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append('"').Append(Escape(field.Name)).Append("\":");
                var value = field.GetValue(obj);
                sb.Append(EncodeValue(value));
            }
            sb.Append('}');
            return sb.ToString();
        }

        private static string EncodeValue(object value)
        {
            if (value == null) return "null";
            if (value is byte[] bytes) return '"' + Escape(Convert.ToBase64String(bytes)) + '"';
            if (value is string str) return '"' + Escape(str) + '"';
            if (value is bool b) return b ? "true" : "false";
            if (value is double d) return d.ToString("R", CultureInfo.InvariantCulture);
            if (value is float f) return f.ToString("R", CultureInfo.InvariantCulture);
            if (value is long l) return l.ToString(CultureInfo.InvariantCulture);
            if (value is int i) return i.ToString(CultureInfo.InvariantCulture);
            return '"' + Escape(value.ToString()) + '"';
        }

        private static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        public static T FromJson<T>(string json)
        {
            var obj = Activator.CreateInstance<T>();
            if (string.IsNullOrEmpty(json)) return obj;
            foreach (var field in typeof(T).GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                string key = '"' + Escape(field.Name) + '"';
                int keyIdx = json.IndexOf(key, StringComparison.Ordinal);
                if (keyIdx < 0) continue;
                int colon = json.IndexOf(':', keyIdx + key.Length);
                if (colon < 0) continue;
                int start = colon + 1;
                while (start < json.Length && (json[start] == ' ' || json[start] == '\t')) start++;
                string token = ReadToken(json, ref start);
                SetField(field, obj, token);
            }
            return obj;
        }

        private static string ReadToken(string json, ref int start)
        {
            if (start >= json.Length) return "";
            char c = json[start];
            if (c == '"')
            {
                var sb = new StringBuilder();
                start++;
                while (start < json.Length && json[start] != '"')
                {
                    if (json[start] == '\\' && start + 1 < json.Length)
                    {
                        start++;
                        sb.Append(json[start]);
                    }
                    else
                    {
                        sb.Append(json[start]);
                    }
                    start++;
                }
                start++;
                return sb.ToString();
            }
            int end = start;
            while (end < json.Length && json[end] != ',' && json[end] != '}')
            {
                if (json[end] == '"') break;
                end++;
            }
            string token = json.Substring(start, end - start).Trim();
            start = end;
            return token;
        }

        private static void SetField(FieldInfo field, object obj, string token)
        {
            try
            {
                if (field.FieldType == typeof(string)) { field.SetValue(obj, token); return; }
                if (field.FieldType == typeof(byte[])) { field.SetValue(obj, Convert.FromBase64String(token)); return; }
                if (field.FieldType == typeof(bool)) { field.SetValue(obj, token == "true"); return; }
                if (field.FieldType == typeof(int)) { field.SetValue(obj, int.Parse(token, CultureInfo.InvariantCulture)); return; }
                if (field.FieldType == typeof(long)) { field.SetValue(obj, long.Parse(token, CultureInfo.InvariantCulture)); return; }
                if (field.FieldType == typeof(double)) { field.SetValue(obj, double.Parse(token, CultureInfo.InvariantCulture)); return; }
                if (field.FieldType == typeof(float)) { field.SetValue(obj, float.Parse(token, CultureInfo.InvariantCulture)); return; }
            }
            catch
            {
                // Keep default on parse failure.
            }
        }
    }

    /// <summary>Unity inspector attribute markers — no-ops outside Unity.</summary>
    public sealed class HeaderAttribute : Attribute { public HeaderAttribute(string header) { } }
    public sealed class SerializeFieldAttribute : Attribute { }
    public sealed class TooltipAttribute : Attribute { public TooltipAttribute(string tooltip) { } }
    public sealed class HideInInspectorAttribute : Attribute { }
    public sealed class CreateAssetMenuAttribute : Attribute
    {
        public string fileName;
        public string menuName;
    }
    public sealed class DefaultExecutionOrderAttribute : Attribute
    {
        public int order;
        public DefaultExecutionOrderAttribute(int order) { this.order = order; }
    }

    /// <summary>Unity FindObjectsInactive filter — no-op outside Unity.</summary>
    public enum FindObjectsInactive
    {
        Exclude = 0,
        Include = 1
    }

    /// <summary>Unity 2D/3D math structs — minimal surface.</summary>
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public static Vector3 zero => default;
        public static Vector3 operator +(Vector3 a, Vector3 b) => new Vector3(a.x + b.x, a.y + b.y, a.z + b.z);
        public static Vector3 operator -(Vector3 a, Vector3 b) => new Vector3(a.x - b.x, a.y - b.y, a.z - b.z);
        public static bool operator ==(Vector3 a, Vector3 b) => a.x == b.x && a.y == b.y && a.z == b.z;
        public static bool operator !=(Vector3 a, Vector3 b) => !(a == b);
        public override bool Equals(object obj) => obj is Vector3 v && this == v;
        public override int GetHashCode() => x.GetHashCode() ^ y.GetHashCode() ^ z.GetHashCode();
    }

    public struct Quaternion
    {
        public float x, y, z, w;
        public Quaternion(float x, float y, float z, float w) { this.x = x; this.y = y; this.z = z; this.w = w; }
        public static Quaternion identity => new Quaternion(0f, 0f, 0f, 1f);
        public static Quaternion operator *(Quaternion a, Quaternion b) => a; // simplified compose for harness
        public static bool operator ==(Quaternion a, Quaternion b) => a.x == b.x && a.y == b.y && a.z == b.z && a.w == b.w;
        public static bool operator !=(Quaternion a, Quaternion b) => !(a == b);
        public override bool Equals(object obj) => obj is Quaternion q && this == q;
        public override int GetHashCode() => x.GetHashCode() ^ y.GetHashCode() ^ z.GetHashCode() ^ w.GetHashCode();
    }

    /// <summary>Unity Transform — functional scene-graph node (parent/children/position).</summary>
    public class Transform : Component
    {
        private Transform _parent;
        private readonly List<Transform> _children = new();
        public Vector3 localPosition;
        public Quaternion localRotation = Quaternion.identity;

        public Transform parent
        {
            get => _parent;
            set => SetParent(value);
        }

        public Vector3 position
        {
            get => _parent == null ? localPosition : _parent.position + localPosition;
            set => localPosition = _parent == null ? value : value - _parent.position;
        }

        public void SetParent(Transform parent) => SetParent(parent, false);

        public void SetParent(Transform parent, bool worldPositionStays)
        {
            var old = _parent;
            _parent = parent;
            old?._children.Remove(this);
            if (parent != null) parent._children.Add(this);
        }

        public void SetPositionAndRotation(Vector3 position, Quaternion rotation)
        {
            this.position = position;
            this.rotation = rotation;
        }

        public Quaternion rotation
        {
            get => _parent == null ? localRotation : _parent.rotation * localRotation;
            set => localRotation = _parent == null ? value : Quaternion.identity; // simplified inverse for harness
        }

        public int childCount => _children.Count;
        internal List<Transform> ChildrenSnapshot() => new(_children);

        public Transform Find(string name)
        {
            for (int i = 0; i < _children.Count; i++)
            {
                if (_children[i].gameObject.name == name) return _children[i];
            }
            return null;
        }

        public void SetAsLastSibling()
        {
            if (_parent == null) return;
            _parent._children.Remove(this);
            _parent._children.Add(this);
        }
    }

    /// <summary>Unity Coroutine/WaitForSeconds — minimal no-ops for StartCoroutine call sites.</summary>
    public class Coroutine { }
    public class WaitForSeconds
    {
        public readonly float seconds;
        public WaitForSeconds(float seconds) { this.seconds = seconds; }
    }

    /// <summary>Base class for Unity objects — a functional registry outside Unity.</summary>
    public class Object
    {
        public string name;
        public int GetInstanceID() => GetHashCode();
        public int GetEntityId() => GetHashCode();

        private static readonly List<Object> s_all = new();
        private static readonly object s_lock = new();
        private bool _destroyed;

        public Object()
        {
            lock (s_lock) { s_all.Add(this); }
        }

        internal bool IsDestroyed => _destroyed;

        public static void DontDestroyOnLoad(Object target) { }
        public static void DestroyImmediate(Object target) => Destroy(target);

        /// <summary>Unity-style truthiness: a destroyed object is falsy.</summary>
        public static implicit operator bool(Object o) => o != null && !o._destroyed;

        internal static Object[] LiveSnapshot()
        {
            lock (s_lock) { return s_all.ToArray(); }
        }

        public static void Destroy(Object target)
        {
            if (target == null) return;
            lock (s_lock)
            {
                if (target is GameObject go)
                {
                    DestroyTree(go);
                    return;
                }
                s_all.Remove(target);
                target._destroyed = true;
            }
        }

        private static void DestroyTree(GameObject go)
        {
            foreach (var comp in go.GetComponentsSnapshot())
            {
                s_all.Remove(comp);
                comp._destroyed = true;
            }
            var tx = go.transform;
            if (tx != null)
            {
                s_all.Remove(tx);
                tx._destroyed = true;
                foreach (var child in tx.ChildrenSnapshot())
                {
                    if (child.gameObject != null) DestroyTree(child.gameObject);
                }
            }
            s_all.Remove(go);
            go._destroyed = true;
        }

        /// <summary>Finds live objects of the given type (mirrors UnityEngine.Object.FindObjectsByType).</summary>
        public static T[] FindObjectsByType<T>(FindObjectsInactive findObjectsInactive) where T : Object
        {
            lock (s_lock)
            {
                var result = new List<T>();
                for (int i = 0; i < s_all.Count; i++)
                {
                    if (s_all[i] is T typed && !s_all[i]._destroyed) result.Add(typed);
                }
                return result.ToArray();
            }
        }

        /// <summary>Clones a GameObject prefab including its components (mirrors Unity Instantiate).</summary>
        public static T Instantiate<T>(T original, Transform parent) where T : Object
        {
            if (original == null) return null;
            T clone = (T)Activator.CreateInstance(original.GetType());
            clone.name = original.name;

            if (clone is GameObject cloneGo && original is GameObject originalGo)
            {
                foreach (var comp in originalGo.GetComponentsSnapshot())
                {
                    var copy = (Component)Activator.CreateInstance(comp.GetType());
                    cloneGo.AttachComponent(copy);
                }
                cloneGo.SetActive(originalGo.IsActive);
                if (parent != null) cloneGo.transform.SetParent(parent);
            }
            else if (parent != null && clone is Component compClone && compClone.gameObject != null)
            {
                compClone.gameObject.transform.SetParent(parent);
            }
            return clone;
        }
    }

    /// <summary>Unity Component base — linked to its owning GameObject.</summary>
    public class Component : Object
    {
        internal GameObject _gameObject;

        public GameObject gameObject => _gameObject ?? (_gameObject = new GameObject(GetType().Name + "_detached"));
        public Transform transform => gameObject.transform;
        public T GetComponent<T>() => gameObject.GetComponent<T>();
        public T[] GetComponents<T>() => gameObject.GetComponents<T>();
        public T[] GetComponentsInChildren<T>(bool includeInactive = false) => gameObject.GetComponentsInChildren<T>(includeInactive);
        public T GetComponentInParent<T>() => gameObject.GetComponentInParent<T>();
    }

    /// <summary>Unity GameObject — a functional component container with hierarchy.</summary>
    public class GameObject : Object
    {
        private readonly List<Component> _components = new();
        private Transform _transform;
        private bool _active = true;

        public GameObject() { }
        public GameObject(string name) { this.name = name; }

        public Transform transform
        {
            get
            {
                if (_transform == null)
                {
                    _transform = new Transform();
                    _transform._gameObject = this;
                }
                return _transform;
            }
        }

        public bool activeInHierarchy => IsActive && (transform.parent == null || transform.parent.gameObject.activeInHierarchy);
        internal bool IsActive => _active;
        public void SetActive(bool active) { _active = active; }

        public T AddComponent<T>() where T : Component, new()
        {
            var comp = new T();
            AttachComponent(comp);
            return comp;
        }

        public T GetComponent<T>()
        {
            var list = GetComponentsInternal();
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] is T typed) return typed;
            }
            return default;
        }

        /// <summary>Mirrors UnityEngine.GameObject.TryGetComponent (Root's support-component guard).</summary>
        public bool TryGetComponent<T>(out T component) where T : Component
        {
            component = GetComponent<T>();
            return component != null;
        }

        public T[] GetComponents<T>()
        {
            var list = GetComponentsInternal();
            var result = new List<T>();
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] is T typed) result.Add(typed);
            }
            return result.ToArray();
        }

        public void GetComponents<T>(List<T> results)
        {
            if (results == null) return;
            results.Clear();
            var list = GetComponentsInternal();
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] is T typed) results.Add(typed);
            }
        }

        public T[] GetComponentsInChildren<T>(bool includeInactive = false)
        {
            var list = new List<T>();
            CollectComponentsInChildren(this, list, includeInactive);
            return list.ToArray();
        }

        private static void CollectComponentsInChildren<T>(GameObject go, List<T> result, bool includeInactive)
        {
            if (go == null) return;
            if (!includeInactive && !go.IsActive) return;
            var comps = go.GetComponents<T>();
            for (int i = 0; i < comps.Length; i++) result.Add(comps[i]);
            var tx = go.transform;
            if (tx != null)
            {
                foreach (var child in tx.ChildrenSnapshot())
                {
                    if (child.gameObject != null) CollectComponentsInChildren(child.gameObject, result, includeInactive);
                }
            }
        }

        public T GetComponentInParent<T>()
        {
            var node = this;
            while (node != null)
            {
                var comp = node.GetComponent<T>();
                if (comp != null) return comp;
                node = node.transform.parent?.gameObject;
            }
            return default;
        }

        internal void AttachComponent(Component component)
        {
            if (component == null || component._gameObject != null) return;
            component._gameObject = this;
            _components.Add(component);
        }

        /// <summary>Finds the first live GameObject with the given name (mirrors GameObject.Find).</summary>
        public static GameObject Find(string name)
        {
            var all = Object.LiveSnapshot();
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] is GameObject go && !go.IsDestroyed && go.name == name) return go;
            }
            return null;
        }

        internal List<Component> GetComponentsSnapshot() => new(_components);
        internal List<Component> GetComponentsInternal() => _components;
    }

    /// <summary>Unity MonoBehaviour base — a functional no-op outside Unity.</summary>
    public class MonoBehaviour : Component
    {
        public bool enabled = true;
        public bool isActiveAndEnabled => enabled && gameObject.activeInHierarchy;

        public Coroutine StartCoroutine(IEnumerator routine) => new Coroutine();
        public void StopCoroutine(Coroutine routine) { }
        public void StopCoroutine(IEnumerator routine) { }
        public void StopAllCoroutines() { }
    }

    /// <summary>Unity ScriptableObject base — creatable outside Unity.</summary>
    public class ScriptableObject : Object
    {
        public static T CreateInstance<T>() where T : ScriptableObject, new() => new T();
        public static ScriptableObject CreateInstance(Type type) => (ScriptableObject)Activator.CreateInstance(type);
    }

    /// <summary>Unity assembly introspection used by Context auto-discovery.</summary>
    public static class Assemblies
    {
        public static class CurrentAssemblies
        {
            public static System.Reflection.Assembly[] GetLoadedAssemblies() => AppDomain.CurrentDomain.GetAssemblies();
        }
    }

    namespace Profiling
    {
        /// <summary>Unity ProfilerMarker — a no-op outside Unity.</summary>
        public struct ProfilerMarker
        {
            public ProfilerMarker(string name) { }
            public void Begin() { }
            public void End() { }
        }

        public static class Profiler
        {
            public static long GetTotalAllocatedMemoryLong() => 0L;
            public static long GetTotalReservedMemoryLong() => 0L;
            public static long GetTotalUnusedReservedMemoryLong() => 0L;
            public static long GetMonoUsedSizeLong() => 0L;
            public static long GetMonoHeapSizeLong() => 0L;
        }
    }

    /// <summary>Unity 2D vector — minimal surface for input/joystick math.</summary>
    public struct Vector2
    {
        public float x, y;
        public Vector2(float x, float y) { this.x = x; this.y = y; }
        public static Vector2 zero => default;
        public static Vector2 one => new Vector2(1f, 1f);
        public float sqrMagnitude => x * x + y * y;
        public float magnitude => (float)Math.Sqrt(x * x + y * y);
        public Vector2 normalized
        {
            get
            {
                float m = magnitude;
                return m > 1E-05f ? new Vector2(x / m, y / m) : default;
            }
        }
        public static Vector2 ClampMagnitude(Vector2 v, float maxLength)
        {
            float m = v.magnitude;
            if (m > maxLength) return new Vector2(v.x * maxLength / m, v.y * maxLength / m);
            return v;
        }
        public static Vector2 operator +(Vector2 a, Vector2 b) => new Vector2(a.x + b.x, a.y + b.y);
        public static Vector2 operator -(Vector2 a, Vector2 b) => new Vector2(a.x - b.x, a.y - b.y);
        public static Vector2 operator *(Vector2 a, float d) => new Vector2(a.x * d, a.y * d);
        public static Vector2 operator *(float d, Vector2 a) => a * d;
        public static bool operator ==(Vector2 a, Vector2 b) => a.x == b.x && a.y == b.y;
        public static bool operator !=(Vector2 a, Vector2 b) => !(a == b);
        public override bool Equals(object obj) => obj is Vector2 v && this == v;
        public override int GetHashCode() => x.GetHashCode() ^ y.GetHashCode();
        public override string ToString() => $"({x}, {y})";
    }

    /// <summary>Unity color — minimal surface for floating text/UI services.</summary>
    public struct Color
    {
        public float r, g, b, a;
        public Color(float r, float g, float b, float a = 1f) { this.r = r; this.g = g; this.b = b; this.a = a; }
        public static Color white => new Color(1f, 1f, 1f, 1f);
        public static Color black => new Color(0f, 0f, 0f, 1f);
        public static Color red => new Color(1f, 0f, 0f, 1f);
        public static Color green => new Color(0f, 1f, 0f, 1f);
        public static Color blue => new Color(0f, 0f, 1f, 1f);
        public static bool operator ==(Color a, Color b) => a.r == b.r && a.g == b.g && a.b == b.b && a.a == b.a;
        public static bool operator !=(Color a, Color b) => !(a == b);
        public override bool Equals(object obj) => obj is Color c && this == c;
        public override int GetHashCode() => r.GetHashCode() ^ g.GetHashCode() ^ b.GetHashCode() ^ a.GetHashCode();
    }

    /// <summary>Unity Random — seeded global; Range mirrors Unity's inclusive floats.</summary>
    public static class Random
    {
        private static readonly System.Random s_rng = new System.Random();
        private static readonly object s_lock = new object();

        public static float value
        {
            get { lock (s_lock) { return (float)s_rng.NextDouble(); } }
        }

        public static float Range(float min, float max)
        {
            lock (s_lock) { return min + (max - min) * (float)s_rng.NextDouble(); }
        }

        public static int Range(int min, int max)
        {
            lock (s_lock) { return s_rng.Next(min, max); }
        }
    }

    /// <summary>Unity audio clip — a named handle; no real audio outside Unity.</summary>
    public class AudioClip : Object
    {
        public AudioClip() { }
        public AudioClip(string name) { this.name = name; }
    }

    /// <summary>Unity AudioSource — functional play/stop state so the SFX pool logic runs.</summary>
    public class AudioSource : Component
    {
        public AudioClip clip;
        public bool loop;
        public bool playOnAwake;
        public float volume = 1f;
        public float pitch = 1f;
        public float spatialBlend;
        public bool isPlaying { get; private set; }

        public void Play() { isPlaying = true; }
        public void Stop() { isPlaying = false; }
        public void PlayOneShot(AudioClip clip) { this.clip = clip; isPlaying = true; }
    }

    /// <summary>Unity canvas render modes — enum surface only.</summary>
    public enum RenderMode
    {
        ScreenSpaceOverlay = 0,
        ScreenSpaceCamera = 1,
        WorldSpace = 2
    }

    /// <summary>Unity Canvas — functional sort-order state for the UIManager layer roots.</summary>
    public class Canvas : Component
    {
        public RenderMode renderMode = RenderMode.ScreenSpaceOverlay;
        public int sortingOrder;
        public bool overrideSorting;
    }

    /// <summary>Unity RectTransform — functional anchor/offset surface.</summary>
    public class RectTransform : Transform
    {
        public Vector2 anchorMin;
        public Vector2 anchorMax;
        public Vector2 offsetMin;
        public Vector2 offsetMax;
    }

    /// <summary>Unity CanvasGroup — functional interactivity state for modal blocking.</summary>
    public class CanvasGroup : Component
    {
        public bool interactable = true;
        public bool blocksRaycasts = true;
        public float alpha = 1f;
    }    /// <summary>Unity async asset request — a no-op null result outside Unity.</summary>
    public class ResourceRequest
    {
        public bool isDone { get; set; }
        public Object asset { get; set; }
        // Mirrors UnityEngine.AsyncOperation.completed (2020.1+) so the runtime's
        // ResourcesUIAssetProvider can bridge the load through a TaskCompletionSource.
        public event Action<ResourceRequest> completed;
        internal void SimulateComplete()
        {
            isDone = true;
            completed?.Invoke(this);
        }
    }

    /// <summary>Unity Resources — always-miss outside Unity (harness injects mock providers).</summary>
    public static class Resources
    {
        public static ResourceRequest LoadAsync<T>(string path) where T : Object => new ResourceRequest { isDone = false, asset = null };
        public static T Load<T>(string path) where T : Object => null;
    }

    // ── Debug HUD support (NexusDebugHUD.cs) — onGUI surface, no-op outside Unity ──
    public enum KeyCode { None = 0, F12 = 123 }
    public enum TextAnchor { UpperLeft, UpperCenter, UpperRight, MiddleLeft, MiddleCenter, MiddleRight, LowerLeft, LowerCenter, LowerRight }
    public enum TextureFormat { RGBA32 = 4 }

    public struct Rect
    {
        public float x, y, width, height;
        public Rect(float x, float y, float width, float height) { this.x = x; this.y = y; this.width = width; this.height = height; }
    }

    public sealed class Texture2D : Object
    {
        public Texture2D(int width, int height, TextureFormat format, bool mipChain) { }
        public void SetPixel(int x, int y, Color color) { }
        public void Apply() { }
    }

    public sealed class GUIStyleState
    {
        public Color textColor;
        public Texture2D background;
    }

    public sealed class GUIStyle
    {
        public int fontSize;
        public TextAnchor alignment;
        public bool wordWrap;
        public bool richText;
        public GUIStyleState normal = new GUIStyleState();
        public GUIStyle() { }
        public GUIStyle(GUIStyle src) { }
    }

    public sealed class GUISkin
    {
        public GUIStyle box { get; } = new GUIStyle();
        public GUIStyle label { get; } = new GUIStyle();
    }

    public static class GUI
    {
        public static GUISkin skin { get; } = new GUISkin();
        public static void Box(Rect rect, string text, GUIStyle style) { }
    }

    public static class GUILayout
    {
        public static void BeginArea(Rect rect, GUIStyle style) { }
        public static void EndArea() { }
        public static void BeginVertical() { }
        public static void EndVertical() { }
        public static void Label(string text, GUIStyle style) { }
        public static void Space(float pixels) { }
    }

    public static class ColorUtility
    {
        public static string ToHtmlStringRGB(Color c) => $"#{((int)(c.r * 255)):X2}{((int)(c.g * 255)):X2}{((int)(c.b * 255)):X2}";
    }

    public static class Input
    {
        public static bool GetKeyDown(KeyCode key) => false;
        public static float GetAxisRaw(string axisName) => 0f;
        public static float GetAxis(string axisName) => 0f;
    }

    public sealed class AddComponentMenuAttribute : Attribute { public AddComponentMenuAttribute(string menuName) { } }
}

namespace Unity.Profiling
{
    // SignalBus.cs imports this namespace; only the (guarded) ProfilerMarker usage
    // needs the type, which lives under UnityEngine.Profiling above.
}

namespace UnityEngine.Assertions
{
    /// <summary>Unity assertion exception — thrown by NexusTestContext on failed asserts.</summary>
    public class AssertionException : Exception
    {
        public AssertionException(string message, string detail) : base(message) { }
    }
}

namespace UnityEngine.UI
{
    /// <summary>Unity UI scaler — enum surface only for the UIManager canvas setup.</summary>
    public class CanvasScaler : Component
    {
        public enum ScaleMode
        {
            ConstantPixelSize = 0,
            ScaleWithScreenSize = 1,
            ConstantPhysicalSize = 2
        }

        public ScaleMode uiScaleMode;
        public Vector2 referenceResolution;
        public float matchWidthOrHeight;
    }

    /// <summary>Unity UI raycast helper — a no-op outside Unity.</summary>
    public class GraphicRaycaster : Component { }
}

namespace UnityEngine.SceneManagement
{
    /// <summary>Unity scene load mode.</summary>
    public enum LoadSceneMode
    {
        Single = 0,
        Additive = 1
    }

    /// <summary>Unity async scene operation — functional pending/done state driven by SceneManager.</summary>
    public class AsyncOperation
    {
        public bool isDone { get; set; }
        public bool allowSceneActivation { get; set; } = true;
        internal string _sceneName;
        internal bool _isUnload;
    }

    /// <summary>Unity scene handle — minimal validity/loaded state.</summary>
    public struct Scene
    {
        public string name;
        public bool isLoaded;
        public bool IsValid() => !string.IsNullOrEmpty(name);
    }

    /// <summary>
    /// Functional Unity SceneManager stub: tracks known scenes and pending operations.
    /// The harness drives completion through SimulateCompleteAll so SceneLoader's
    /// while(!op.isDone) loop is tested deterministically (a real Unity scene load
    /// would take many frames; here the test controls when the load finishes).
    /// </summary>
    public static class SceneManager
    {
        private static readonly HashSet<string> s_known = new HashSet<string>();
        private static readonly List<AsyncOperation> s_pending = new List<AsyncOperation>();
        private static readonly HashSet<string> s_loaded = new HashSet<string>();
        private static string s_active;

        public static void SimulateReset()
        {
            s_known.Clear();
            s_pending.Clear();
            s_loaded.Clear();
            s_active = null;
        }

        public static void SimulateAddScene(string sceneName, bool loaded = true)
        {
            s_known.Add(sceneName);
            if (loaded)
            {
                s_loaded.Add(sceneName);
                if (s_active == null) s_active = sceneName;
            }
        }

        public static void SimulateCompleteAll()
        {
            foreach (var op in s_pending)
            {
                op.isDone = true;
                if (!op._isUnload && op._sceneName != null) s_loaded.Add(op._sceneName);
            }
            s_pending.Clear();
        }

        public static int SimulatePendingCount => s_pending.Count;
        public static string SimulateActiveScene => s_active;

        public static AsyncOperation LoadSceneAsync(string sceneName, LoadSceneMode mode)
        {
            if (!s_known.Contains(sceneName)) return null;
            var op = new AsyncOperation { _sceneName = sceneName, _isUnload = false };
            s_pending.Add(op);
            return op;
        }

        public static AsyncOperation UnloadSceneAsync(string sceneName)
        {
            if (!s_known.Contains(sceneName) || !s_loaded.Contains(sceneName)) return null;
            s_loaded.Remove(sceneName);
            var op = new AsyncOperation { _sceneName = sceneName, _isUnload = true };
            s_pending.Add(op);
            return op;
        }

        public static Scene GetSceneByName(string sceneName) =>
            new Scene { name = sceneName, isLoaded = s_loaded.Contains(sceneName) };

        public static void SetActiveScene(Scene scene)
        {
            if (scene.IsValid()) s_active = scene.name;
        }
    }
}

namespace UnityEngine.Scripting
{
    /// <summary>Unity's link-time preservation marker — a no-op outside Unity.</summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Method |
                    AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Constructor |
                    AttributeTargets.Interface | AttributeTargets.Enum | AttributeTargets.Delegate |
                    AttributeTargets.Event, Inherited = false)]
    public sealed class PreserveAttribute : Attribute
    {
    }
}

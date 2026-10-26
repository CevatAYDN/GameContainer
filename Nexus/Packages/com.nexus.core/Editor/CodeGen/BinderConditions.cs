using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Nexus.Editor
{
    /// <summary>
    /// The binder is generated from the Editor's assemblies, so a type declared under <c>#if UNITY_EDITOR</c> or
    /// <c>#if DEVELOPMENT_BUILD</c> was named in release players where it does not exist (CS0234, issue #85).
    /// This reads each type's declarations in its assembly's source files and returns the preprocessor condition
    /// the binder repeats around every line that names the type. A symbol defined for the type's assembly but not
    /// for the binder's (an asmdef's versionDefines) is replaced by its Editor value; every other symbol has the
    /// same value in both assemblies of one build, so the player compile evaluates the copied condition itself.
    /// shortcut: declarations only; an [Inject] member under its own #if inside an unconditional type is not seen,
    /// add a member scan when a game needs one.
    /// </summary>
    internal sealed class BinderConditions
    {
        public const string Always = "true";

        private static readonly Regex s_namespace = new Regex(@"^\s*namespace\s+([\w.]+)", RegexOptions.Compiled);
        private static readonly Regex s_declaration = new Regex(
            @"\b(?:record\s+(?:class|struct)|class|struct|interface|enum|record)\s+@?([A-Za-z_]\w*)", RegexOptions.Compiled);

        private readonly IDictionary<string, (string[] Files, string[] Defines)> _assemblies;
        private readonly HashSet<string> _binderDefines;
        private readonly Func<string, string> _read;
        private readonly Dictionary<string, Dictionary<string, List<string>>> _declarations = new Dictionary<string, Dictionary<string, List<string>>>();
        private readonly Dictionary<Type, string> _byType = new Dictionary<Type, string>();

        /// <param name="assemblies">Source files and Editor defines by assembly name; precompiled assemblies are absent.</param>
        /// <param name="binderAssembly">The assembly that compiles the binder, or null when unknown.</param>
        /// <param name="read">Reads a source file; tests pass text, the Editor reads the disk.</param>
        public BinderConditions(IDictionary<string, (string[] Files, string[] Defines)> assemblies, string binderAssembly, Func<string, string> read = null)
        {
            _assemblies = assemblies;
            _binderDefines = binderAssembly != null && assemblies.TryGetValue(binderAssembly, out var binder)
                ? new HashSet<string>(binder.Defines)
                : null;
            _read = read ?? File.ReadAllText;
        }

        /// <summary>The condition under which every given type exists in a player, or null when it always does.</summary>
        public string For(IEnumerable<Type> types)
        {
            var sb = new StringBuilder();
            var seen = new HashSet<string>();
            foreach (var type in types)
            {
                string c = ForType(type);
                if (c == Always || !seen.Add(c)) continue;
                if (sb.Length > 0) sb.Append(" && ");
                sb.Append('(').Append(c).Append(')');
            }
            if (sb.Length == 0) return null;
            string all = Simplify(sb.ToString(), _ => null);
            return all == Always ? null : all;
        }

        public string For(params Type[] types) => For((IEnumerable<Type>)types);

        private string ForType(Type type)
        {
            if (type == null || type.IsGenericParameter) return Always;
            if (type.HasElementType) return ForType(type.GetElementType());
            if (type.IsGenericType && !type.IsGenericTypeDefinition)
            {
                var parts = new List<Type> { type.GetGenericTypeDefinition() };
                parts.AddRange(type.GetGenericArguments());
                return For(parts) ?? Always;
            }
            if (_byType.TryGetValue(type, out var cached)) return cached;

            string result = Always;
            string assembly = type.Assembly.GetName().Name;
            if (_binderDefines != null && _assemblies.TryGetValue(assembly, out var source))
            {
                var outer = type;
                while (outer.DeclaringType != null) outer = outer.DeclaringType;
                string name = type.Name;
                int tick = name.IndexOf('`');
                if (tick >= 0) name = name.Substring(0, tick);

                if (DeclarationsOf(assembly, source.Files).TryGetValue((outer.Namespace ?? "") + "|" + name, out var raws))
                {
                    var editor = new HashSet<string>(source.Defines);
                    var sb = new StringBuilder();
                    foreach (var raw in raws)
                    {
                        // A declaration the Editor does not compile is not where this type came from.
                        if (Simplify(raw, s => editor.Contains(s)) != Always) continue;
                        string c = Simplify(raw, s => editor.Contains(s) != _binderDefines.Contains(s) ? editor.Contains(s) : (bool?)null);
                        if (c == null || c == Always) { sb.Clear(); break; }
                        if (sb.Length > 0) sb.Append(" || ");
                        sb.Append('(').Append(c).Append(')');
                    }
                    if (sb.Length > 0) result = Simplify(sb.ToString(), _ => null) ?? Always;
                }
            }
            _byType[type] = result;
            return result;
        }

        private Dictionary<string, List<string>> DeclarationsOf(string assembly, string[] files)
        {
            if (_declarations.TryGetValue(assembly, out var index)) return index;
            index = new Dictionary<string, List<string>>();
            foreach (var file in files)
            {
                string text;
                try { text = _read(file); }
                catch (Exception) { continue; }
                foreach (var (ns, name, condition) in Declarations(text))
                {
                    string key = ns + "|" + name;
                    if (!index.TryGetValue(key, out var list)) index[key] = list = new List<string>();
                    list.Add(condition);
                }
            }
            _declarations[assembly] = index;
            return index;
        }

        /// <summary>Every type declaration in a source file with the raw condition of the directives around it.</summary>
        internal static IEnumerable<(string Namespace, string Name, string Condition)> Declarations(string source)
        {
            var frames = new List<List<string>>();
            string ns = "";
            foreach (var rawLine in source.Split('\n'))
            {
                string line = rawLine.Trim();
                if (line.StartsWith("#"))
                {
                    string directive = StripComment(line.Substring(1)).Trim();
                    if (directive.StartsWith("if ") || directive.StartsWith("if(")) frames.Add(new List<string> { directive.Substring(2).Trim() });
                    else if ((directive.StartsWith("elif ") || directive.StartsWith("elif(")) && frames.Count > 0) frames[frames.Count - 1].Add(directive.Substring(4).Trim());
                    else if (directive == "else" && frames.Count > 0) frames[frames.Count - 1].Add(Always);
                    else if (directive == "endif" && frames.Count > 0) frames.RemoveAt(frames.Count - 1);
                    continue;
                }
                line = StripComment(line);
                var nsMatch = s_namespace.Match(line);
                if (nsMatch.Success) ns = nsMatch.Groups[1].Value;
                foreach (Match m in s_declaration.Matches(line))
                    yield return (ns, m.Groups[1].Value, Condition(frames));
            }
        }

        private static string StripComment(string line)
        {
            int i = line.IndexOf("//", StringComparison.Ordinal);
            return i >= 0 ? line.Substring(0, i) : line;
        }

        // The branch taken is the current one and none of the earlier ones in the same #if chain.
        private static string Condition(List<List<string>> frames)
        {
            var sb = new StringBuilder();
            foreach (var branches in frames)
            {
                for (int b = 0; b < branches.Count; b++)
                {
                    if (sb.Length > 0) sb.Append(" && ");
                    sb.Append(b < branches.Count - 1 ? "!(" : "(").Append(branches[b]).Append(')');
                }
            }
            return sb.Length == 0 ? Always : sb.ToString();
        }

        /// <summary>
        /// Folds a preprocessor expression: <paramref name="symbol"/> gives a symbol's value, or null to keep it.
        /// Returns "true", "false" or the reduced expression; null when the expression does not parse.
        /// </summary>
        internal static string Simplify(string expression, Func<string, bool?> symbol)
        {
            try
            {
                var parser = new Parser(expression, symbol);
                var result = parser.Or();
                parser.End();
                return result.Value.HasValue ? (result.Value.Value ? Always : "false") : result.Text;
            }
            catch (FormatException)
            {
                return null;
            }
        }

        private struct Part
        {
            public bool? Value;
            public string Text;
            public int Precedence; // 1 ||, 2 &&, 3 == !=, 4 operand or !

            public static Part Of(bool value) => new Part { Value = value, Precedence = 4 };
            public string In(int precedence) => Precedence < precedence ? "(" + Text + ")" : Text;
        }

        private sealed class Parser
        {
            private readonly string _s;
            private readonly Func<string, bool?> _symbol;
            private int _i;

            public Parser(string s, Func<string, bool?> symbol) { _s = s; _symbol = symbol; }

            public Part Or()
            {
                var left = And();
                while (Take("||"))
                {
                    var right = And();
                    if (left.Value == true || right.Value == true) left = Part.Of(true);
                    else if (left.Value == false) left = right;
                    else if (right.Value != false) left = new Part { Text = left.In(1) + " || " + right.In(1), Precedence = 1 };
                }
                return left;
            }

            private Part And()
            {
                var left = Equality();
                while (Take("&&"))
                {
                    var right = Equality();
                    if (left.Value == false || right.Value == false) left = Part.Of(false);
                    else if (left.Value == true) left = right;
                    else if (right.Value != true) left = new Part { Text = left.In(2) + " && " + right.In(2), Precedence = 2 };
                }
                return left;
            }

            private Part Equality()
            {
                var left = Unary();
                while (true)
                {
                    bool equal;
                    if (Take("==")) equal = true;
                    else if (Take("!=")) equal = false;
                    else return left;
                    var right = Unary();
                    if (left.Value.HasValue && right.Value.HasValue) left = Part.Of((left.Value == right.Value) == equal);
                    else if (left.Value.HasValue || right.Value.HasValue)
                    {
                        var known = left.Value.HasValue ? left : right;
                        var open = left.Value.HasValue ? right : left;
                        left = known.Value == equal ? open : Not(open);
                    }
                    else left = new Part { Text = left.In(3) + (equal ? " == " : " != ") + right.In(4), Precedence = 3 };
                }
            }

            private Part Unary()
            {
                Skip();
                if (_i < _s.Length && _s[_i] == '!' && (_i + 1 >= _s.Length || _s[_i + 1] != '='))
                {
                    _i++;
                    return Not(Unary());
                }
                if (Take("("))
                {
                    var inner = Or();
                    if (!Take(")")) throw new FormatException();
                    return inner;
                }
                Skip();
                int start = _i;
                while (_i < _s.Length && (char.IsLetterOrDigit(_s[_i]) || _s[_i] == '_')) _i++;
                if (_i == start) throw new FormatException();
                string name = _s.Substring(start, _i - start);
                if (name == "true") return Part.Of(true);
                if (name == "false") return Part.Of(false);
                var value = _symbol(name);
                return value.HasValue ? Part.Of(value.Value) : new Part { Text = name, Precedence = 4 };
            }

            private static Part Not(Part p) => p.Value.HasValue ? Part.Of(!p.Value.Value) : new Part { Text = "!" + p.In(4), Precedence = 4 };

            public void End()
            {
                Skip();
                if (_i != _s.Length) throw new FormatException();
            }

            private bool Take(string token)
            {
                Skip();
                if (string.CompareOrdinal(_s, _i, token, 0, token.Length) != 0) return false;
                _i += token.Length;
                return true;
            }

            private void Skip()
            {
                while (_i < _s.Length && char.IsWhiteSpace(_s[_i])) _i++;
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    /// A type whose condition cannot be known (an unreadable source file, an #if this parser does not read) gets
    /// <see cref="Never"/>: the binder leaves out every line that names it and the reflection path serves it (#191).
    /// <see cref="MemberProblem"/> finds an [Inject] member, or an [Inject] constructor, under an #if of its own:
    /// the binder would name a member the player may not have, so the type keeps the reflection path.
    /// </summary>
    internal sealed class BinderConditions
    {
        public const string Always = "true";
        public const string Never = "false";

        private static readonly Regex s_namespace = new Regex(@"^\s*namespace\s+([\w.]+)", RegexOptions.Compiled);
        private static readonly Regex s_declaration = new Regex(
            @"\b(?:record\s+(?:class|struct)|class|struct|interface|enum|record)\s+@?([A-Za-z_]\w*)", RegexOptions.Compiled);
        private static readonly Regex s_partial = new Regex(@"\bpartial\b", RegexOptions.Compiled);
        private static readonly Regex s_inject = new Regex(
            @"(?:\[\s*(?:\w+\s*:\s*)?|,)\s*(?:global::)?(?:Nexus\.Core\.)?(?:Optional)?Inject(?:Attribute)?\s*(?:\]|\(|,)", RegexOptions.Compiled);

        private readonly IDictionary<string, (string[] Files, string[] Defines)> _assemblies;
        private readonly HashSet<string> _binderDefines;
        private readonly Func<string, string> _read;
        private readonly Dictionary<string, SourceIndex> _indexes = new Dictionary<string, SourceIndex>();
        private readonly Dictionary<Type, string> _byType = new Dictionary<Type, string>();
        private readonly Dictionary<Type, string> _problems = new Dictionary<Type, string>();
        private readonly SortedSet<string> _warnings = new SortedSet<string>(StringComparer.Ordinal);

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

        /// <summary>Types whose condition could not be known, with the reason; the binder names none of them.</summary>
        public IReadOnlyDictionary<Type, string> Problems => _problems;

        /// <summary>Source files that could not be read and #if expressions this parser does not read, once each.</summary>
        public IEnumerable<string> Warnings => _warnings;

        /// <summary>
        /// The condition under which every given type exists in a player: null when they always do,
        /// <see cref="Never"/> when one of them cannot be known.
        /// </summary>
        public string For(IEnumerable<Type> types)
        {
            var sb = new StringBuilder();
            var seen = new HashSet<string>();
            foreach (var type in types)
            {
                string c = ForType(type);
                if (c == Never) return Never;
                if (c == Always || !seen.Add(c)) continue;
                if (sb.Length > 0) sb.Append(" && ");
                sb.Append('(').Append(c).Append(')');
            }
            if (sb.Length == 0) return null;
            string all = Simplify(sb.ToString(), _ => null);
            return all == Always ? null : all;
        }

        public string For(params Type[] types) => For((IEnumerable<Type>)types);

        /// <summary>
        /// Why the binder must not name the type's own members or constructor, or null: an [Inject] line of the type or
        /// of a base type sits under an #if its declaration does not, its #if could not be parsed, or the type is partial
        /// and a source file of its assembly could not be read. A type with no declaration found is <see cref="Never"/>
        /// already, so every binder line naming it is left out.
        /// </summary>
        public string MemberProblem(Type type)
        {
            for (var level = type; level != null && level != typeof(object); level = level.BaseType)
            {
                var t = level.IsGenericType ? level.GetGenericTypeDefinition() : level;
                string assembly = t.Assembly.GetName().Name;
                if (!_assemblies.TryGetValue(assembly, out var source)) continue;
                var index = Index(assembly, source.Files);
                if (!index.Declarations.TryGetValue(Key(t), out var declarations)) continue;
                if (index.Unreadable.Count > 0 && declarations.Any(d => d.Partial)) return "could not read " + index.Unreadable[0];
                var editor = new HashSet<string>(source.Defines);
                foreach (var declaration in declarations)
                {
                    foreach (var (line, raw) in index.Files[declaration.File].ConditionalInjects)
                    {
                        // shortcut: the type is the one declared above the line; a file with several types
                        // sends all of them to the reflection path, which serves every type correctly.
                        if (line <= declaration.Line) continue;
                        string c = Simplify(raw, OwnSymbol(editor));
                        if (c == null) return Unparsable(raw, declaration.File);
                        if (c != Always) return $"an [Inject] member in {declaration.File} (line {line}) is declared under #if {c}";
                    }
                }
            }
            return null;
        }

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
                var index = Index(assembly, source.Files);
                var editor = new HashSet<string>(source.Defines);
                var sb = new StringBuilder();
                bool compiled = false, always = false;
                if (index.Declarations.TryGetValue(Key(type), out var declarations))
                {
                    foreach (var declaration in declarations)
                    {
                        string inEditor = Simplify(declaration.Condition, s => editor.Contains(s));
                        if (inEditor == null)
                        {
                            result = Problem(type, Unparsable(declaration.Condition, declaration.File));
                            break;
                        }
                        // A declaration the Editor does not compile is not where this type came from.
                        if (inEditor != Always) continue;
                        compiled = true;
                        string c = Simplify(declaration.Condition, OwnSymbol(editor));
                        if (c == Always) { always = true; break; }
                        if (sb.Length > 0) sb.Append(" || ");
                        sb.Append('(').Append(c).Append(')');
                    }
                }
                // No declaration the Editor compiles: generated source keeps "always", but an unreadable file may hold
                // the declaration under an #if of its own.
                if (result == Always && !compiled && index.Unreadable.Count > 0)
                    result = Problem(type, "could not read " + index.Unreadable[0]);
                else if (result == Always && !always && sb.Length > 0)
                    result = Simplify(sb.ToString(), _ => null) ?? Always;
            }
            _byType[type] = result;
            return result;
        }

        private string Problem(Type type, string reason)
        {
            _problems[type] = reason;
            return Never;
        }

        private string Unparsable(string condition, string file)
        {
            string reason = $"could not parse '#if {condition}' in {file}";
            _warnings.Add(reason);
            return reason;
        }

        // A symbol the type's assembly and the binder's assembly disagree on is a per-assembly define (versionDefines,
        // asmdef defines): a player compiles it as the Editor does. Every other symbol stays for the player to decide.
        private Func<string, bool?> OwnSymbol(HashSet<string> editor)
            => s => _binderDefines != null && editor.Contains(s) != _binderDefines.Contains(s) ? editor.Contains(s) : (bool?)null;

        private static string Key(Type type)
        {
            var outer = type;
            while (outer.DeclaringType != null) outer = outer.DeclaringType;
            string name = type.Name;
            int tick = name.IndexOf('`');
            if (tick >= 0) name = name.Substring(0, tick);
            return (outer.Namespace ?? "") + "|" + name;
        }

        private SourceIndex Index(string assembly, string[] files)
        {
            if (_indexes.TryGetValue(assembly, out var index)) return index;
            index = new SourceIndex();
            foreach (var file in files)
            {
                string text;
                try { text = _read(file); }
                catch (Exception error)
                {
                    index.Unreadable.Add(file);
                    _warnings.Add($"could not read {file} ({error.Message})");
                    continue;
                }
                var scan = Scan(text);
                index.Files[file] = scan;
                foreach (var (ns, name, condition, line, partial) in scan.Declarations)
                {
                    string key = ns + "|" + name;
                    if (!index.Declarations.TryGetValue(key, out var list)) index.Declarations[key] = list = new List<Declaration>();
                    list.Add(new Declaration { Condition = condition, File = file, Line = line, Partial = partial });
                }
            }
            _indexes[assembly] = index;
            return index;
        }

        private sealed class SourceIndex
        {
            public readonly Dictionary<string, List<Declaration>> Declarations = new Dictionary<string, List<Declaration>>();
            public readonly Dictionary<string, FileScan> Files = new Dictionary<string, FileScan>();
            public readonly List<string> Unreadable = new List<string>();
        }

        private struct Declaration
        {
            public string Condition;
            public string File;
            public int Line;
            public bool Partial;
        }

        /// <summary>What one source file declares, and its [Inject] lines that sit under an #if of their own.</summary>
        internal sealed class FileScan
        {
            /// <summary>Every type declaration with the raw condition of the directives around it, its 1-based line, and whether it is partial.</summary>
            public readonly List<(string Namespace, string Name, string Condition, int Line, bool Partial)> Declarations = new List<(string, string, string, int, bool)>();

            /// <summary>
            /// [Inject]/[OptionalInject] lines inside an #if branch that declares no type, so the branch is inside the
            /// member's type: the condition is the member's own. A branch around a whole type declares that type.
            /// </summary>
            public readonly List<(int Line, string Condition)> ConditionalInjects = new List<(int, string)>();
        }

        private sealed class Frame
        {
            public readonly List<string> Branches = new List<string>();
            public readonly List<bool> Declares = new List<bool>();

            public void Open(string condition)
            {
                Branches.Add(condition);
                Declares.Add(false);
            }
        }

        internal static FileScan Scan(string source)
        {
            var scan = new FileScan();
            var frames = new List<Frame>();
            var injects = new List<(int Line, (Frame Frame, int Branch)[] Branches)>();
            string ns = "";
            var lines = source.Split('\n');
            for (int n = 0; n < lines.Length; n++)
            {
                string line = lines[n].Trim();
                if (line.StartsWith("#"))
                {
                    string directive = StripComment(line.Substring(1)).Trim();
                    if (directive.StartsWith("if ") || directive.StartsWith("if("))
                    {
                        var frame = new Frame();
                        frame.Open(directive.Substring(2).Trim());
                        frames.Add(frame);
                    }
                    else if ((directive.StartsWith("elif ") || directive.StartsWith("elif(")) && frames.Count > 0) frames[frames.Count - 1].Open(directive.Substring(4).Trim());
                    else if (directive == "else" && frames.Count > 0) frames[frames.Count - 1].Open(Always);
                    else if (directive == "endif" && frames.Count > 0) frames.RemoveAt(frames.Count - 1);
                    continue;
                }
                line = StripComment(line);
                var nsMatch = s_namespace.Match(line);
                if (nsMatch.Success) ns = nsMatch.Groups[1].Value;
                var current = frames.Select(f => (f, f.Branches.Count - 1)).ToArray();
                bool declared = false;
                foreach (Match m in s_declaration.Matches(line))
                {
                    scan.Declarations.Add((ns, m.Groups[1].Value, Condition(current), n + 1, s_partial.IsMatch(line.Substring(0, m.Index))));
                    declared = true;
                }
                if (declared)
                    foreach (var frame in frames) frame.Declares[frame.Declares.Count - 1] = true;
                if (frames.Count > 0 && s_inject.IsMatch(line)) injects.Add((n + 1, current));
            }
            foreach (var (line, branches) in injects)
            {
                var own = branches.Where(b => !b.Frame.Declares[b.Branch]).ToArray();
                if (own.Length > 0) scan.ConditionalInjects.Add((line, Condition(own)));
            }
            return scan;
        }

        private static string StripComment(string line)
        {
            int i = line.IndexOf("//", StringComparison.Ordinal);
            return i >= 0 ? line.Substring(0, i) : line;
        }

        // The branch taken is the current one and none of the earlier ones in the same #if chain.
        private static string Condition(IEnumerable<(Frame Frame, int Branch)> branches)
        {
            var sb = new StringBuilder();
            foreach (var (frame, branch) in branches)
            {
                for (int b = 0; b <= branch; b++)
                {
                    if (sb.Length > 0) sb.Append(" && ");
                    sb.Append(b < branch ? "!(" : "(").Append(frame.Branches[b]).Append(')');
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
                return result.Value.HasValue ? (result.Value.Value ? Always : Never) : result.Text;
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

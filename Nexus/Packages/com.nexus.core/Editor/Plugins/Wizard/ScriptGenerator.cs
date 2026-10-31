using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CSharp;

namespace Nexus.Editor
{
    /// <summary>
    /// The Context Wizard's script generators: each returns the files it would write, and
    /// <see cref="TryWrite"/> writes all of them or none, never over an existing file (issue #193).
    /// </summary>
    internal static class ScriptGenerator
    {
        internal const string DefaultFolder = "Assets/Scripts/Game";

        private static readonly CSharpCodeProvider s_csharp = new CSharpCodeProvider();

        /// <summary>Null when <paramref name="name"/> can name a C# type, otherwise the message to show.</summary>
        internal static string TypeNameError(string name) =>
            !string.IsNullOrEmpty(name) && s_csharp.IsValidIdentifier(name)
                ? null
                : string.Format(NexusLang.Get("wizard_validation_type_name"), name);

        internal static (string Path, string Content)[] Service(string folder, string name) => new[]
        {
            ($"{folder}/{name}/I{name}.cs",
$@"using Nexus.Core;

public interface I{name} : INexusService {{ }}
"),
            ($"{folder}/{name}/{name}.cs",
$@"using System.Threading;
using System.Threading.Tasks;
using Nexus.Core;

public class {name} : NexusService<I{name}>, I{name}
{{
    public override ValueTask InitializeAsync(CancellationToken ct) => default;
}}
"),
        };

        internal static string ServiceBinding(string name) => $"builder.BindService<I{name}, {name}>();";

        internal static (string Path, string Content)[] ViewMediator(string folder, string name) => new[]
        {
            ($"{folder}/UI/{name}.cs",
$@"using Nexus.Core;

[Mediator(typeof({name}Mediator))]
public class {name} : View
{{
    protected override void OnBind(IContext context) {{ }}
    protected override void OnUnbind() {{ }}
}}
"),
            ($"{folder}/UI/{name}Mediator.cs",
$@"using Nexus.Core;

public class {name}Mediator : Mediator<{name}>
{{
    protected override void OnBind() {{ }}
    protected override void OnUnbind() {{ }}
}}
"),
        };

        internal static (string Path, string Content)[] SignalCommand(string folder, string signal, string command) => new[]
        {
            ($"{folder}/Signals/{signal}.cs",
$@"public readonly struct {signal}
{{
    public readonly int Value;
    public {signal}(int value) => Value = value;
}}
"),
            ($"{folder}/Commands/{command}.cs",
$@"using Nexus.Core;

public class {command} : ICommand<{signal}>
{{
    public void Execute({signal} signal) {{ }}
}}
"),
        };

        internal static string CommandBinding(string signal, string command) => $"builder.BindCommand<{signal}, {command}>();";

        /// <summary>
        /// Writes every file, or none when any of them already exists; <paramref name="existing"/> names those.
        /// </summary>
        internal static bool TryWrite(IReadOnlyList<(string Path, string Content)> files, out List<string> existing)
        {
            existing = files.Select(f => f.Path).Where(File.Exists).ToList();
            if (existing.Count > 0) return false;
            foreach (var (path, content) in files)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                NexusSetupWizard.WriteFileAtomically(path, content);
            }
            return true;
        }
    }
}

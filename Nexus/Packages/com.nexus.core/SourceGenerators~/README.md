# Nexus.SourceGenerator (shipped analyzer DLL)

`Nexus.SourceGenerator.dll` is the compiled **Roslyn Source Generator** (AOT binder,
see `../SourceGenerator~/NexusBinderGenerator.cs`). Unity loads it as a source
generator via a `RoslynAnalyzer` asmdef reference or a `csc.rsp` `-analyzer:` line,
and it emits `Nexus.Generated.NexusGeneratedBinder` into the compilation at build time.
`Assets/csc.rsp` reaches every assembly without a `csc.rsp` of its own, package assemblies included; the
generator emits a binder only into a game's runtime assembly, never into a Nexus package, editor or test
assembly (#264).

## Rebuild

```
dotnet build Nexus/Packages/com.nexus.core/SourceGenerator~/Nexus.SourceGenerator.csproj -c Release
cp Nexus/Packages/com.nexus.core/SourceGenerator~/bin/Release/netstandard2.0/Nexus.SourceGenerator.dll \
   Nexus/Packages/com.nexus.core/SourceGenerators~/
```

Pinned to **Microsoft.CodeAnalysis.CSharp 4.10** (Unity 6000.6's Roslyn, the same as 6000.5's) as a
netstandard2.0 assembly — do not bump, or Unity will silently refuse to load it.

Unity does not treat an analyzer named in a `csc.rsp` as a compile input: in a project that runs the copied DLL,
an assembly keeps the binder the old DLL wrote until something else recompiles it, such as a change to its sources,
its references or the `csc.rsp` (`tools/unity-verify/README.md`, Mode B).

## One-file rule

The editor-time generator (menu **Nexus > Generate AOT Binder**) writes
`Assets/Scripts/Nexus/NexusGeneratedBinder.g.cs` as a real file; the source
generator produces a class with the same name into the compilation. Only one may
exist per assembly at a time — delete the editor output file when the source
generator is active (see `tools/unity-verify/README.md`, Mode B). While the `csc.rsp`
that governs the binder's assembly names the generator, the menu and the player build's
pre-build step write only `link.xml`, and an editor binder left beside the generator
stops them with a message that names CS0101 (#242).

The generator's logic is proven outside Unity by `tools/nexus-benchmark`
(CodeGenSuite CG2): the same source is compiled into the harness and driven through
`CSharpGeneratorDriver`; its emitted binder is compiled and booted end to end.

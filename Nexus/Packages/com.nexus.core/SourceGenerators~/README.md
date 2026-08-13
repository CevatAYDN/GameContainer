# Nexus.SourceGenerator (shipped analyzer DLL)

`Nexus.SourceGenerator.dll` is the compiled **Roslyn Source Generator** (AOT binder,
see `../SourceGenerator~/NexusBinderGenerator.cs`). Unity loads it as a source
generator via a `RoslynAnalyzer` asmdef reference or a `csc.rsp` `-analyzer:` line,
and it emits `Nexus.Generated.NexusGeneratedBinder` into the compilation at build time.

## Rebuild

```
dotnet build Nexus/Packages/com.nexus.core/SourceGenerator~/Nexus.SourceGenerator.csproj -c Release
cp Nexus/Packages/com.nexus.core/SourceGenerator~/bin/Release/netstandard2.0/Nexus.SourceGenerator.dll \
   Nexus/Packages/com.nexus.core/SourceGenerators~/
```

Pinned to **Microsoft.CodeAnalysis.CSharp 4.10** (Unity 6000.5's Roslyn) as a
netstandard2.0 assembly — do not bump, or Unity will silently refuse to load it.

## One-file rule

The editor-time generator (menu **Nexus > Generate AOT Binder**) writes
`Assets/Scripts/Nexus/NexusGeneratedBinder.g.cs` as a real file; the source
generator produces a class with the same name into the compilation. Only one may
exist per assembly at a time — delete the editor output file when the source
generator is active (see `tools/unity-verify/README.md`, Mode B).

The generator's logic is proven outside Unity by `tools/nexus-benchmark`
(CodeGenSuite CG2): the same source is compiled into the harness and driven through
`CSharpGeneratorDriver`; its emitted binder is compiled and booted end to end.

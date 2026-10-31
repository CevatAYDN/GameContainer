# Troubleshooting Guide

This guide helps you diagnose and resolve common issues with Nexus Core.

## Table of Contents

- [Common Issues](#common-issues)
- [Signal Execution Order](#signal-execution-order)
- [Build & Compilation Issues](#build--compilation-issues)
- [Runtime Issues](#runtime-issues)
- [Performance Issues](#performance-issues)
- [Editor Issues](#editor-issues)
- [AOT/IL2CPP Issues](#aotil2cpp-issues)
- [Testing Issues](#testing-issues)
- [Getting Help](#getting-help)

---

## Signal Execution Order

### Execution Order Guarantee (v0.4.0+)

When `signalBus.Fire(signal)` is called, the dispatch order is:

1. **Signal Filters** (`ISignalFilter<T>`, since 0.5.0; in registration order; may cancel dispatch)
2. **Plugin Interceptors** (may cancel dispatch)
3. **Cross-Context Broadcast** (if `[CrossContext]` attribute present)
4. **Commands** (mutate model state, execute in priority order)
5. **Subscriptions** (observe final state)
6. **Composite Triggers** (fan-in commands whose signal set is now complete)

This means **mediator/view subscription handlers always read post-command state**.

### Common Pattern

```csharp
// ✅ CORRECT: Subscribe to original signal; model is already updated
signalBus.Subscribe<UndoSignal>(_ =>
{
    // model.MovesCount.Value is already decremented by UndoCommand
    RebuildBoard();
});

// ❌ WRONG: Creating a separate "CompletedSignal" is unnecessary
// Do NOT create signals like "UndoCompletedSignal" — the command has
// already finished before your subscription handler runs.
```

### Reentrancy Protection

Nexus limits nested dispatch to **10** synchronous levels (`Fire`) and **32** async levels (`FireAsync`); the two counters are separate. If a subscription handler fires another signal, which in turn fires another, Nexus throws `NexusReentrancyException` beyond that depth, in every build.

---

## Common Issues

### Signals Not Firing

**Symptoms:**
- Commands not executing
- Subscribers not receiving signals
- No errors in console

**Possible Causes:**
1. Signal not registered in context
2. Command not bound to signal
3. Context not configured
4. Wrong SignalBus instance

**Solutions:**

**Check Signal Registration:**
```csharp
// In your IContextLifecycle
public void OnConfigure(IContextBuilder builder)
{
    // Ensure signal is bound
    builder.BindCommand<MySignal, MyCommand>();
}
```

**Verify Context Configuration:**
```csharp
// Check context is configured
var context = NexusRuntime.CurrentContext;
if (context == null)
{
    Debug.LogError("No active context. Ensure Root GameObject is in scene.");
}
```

**Check SignalBus Instance:**
```csharp
// Use context's SignalBus, not create new one
var signalBus = context.SignalBus;
signalBus.Fire(new MySignal());
```

**Debug Signal Registration:**
```csharp
// Check registered handlers
var handlers = signalBus.RegisteredHandlers;
foreach (var kvp in handlers)
{
    Debug.Log($"Signal: {kvp.Key.Name}, Handlers: {kvp.Value.Count}");
}
```

---

### Dependency Injection Not Working

**Symptoms:**
- Injected fields are null
- Constructor injection fails
- Property injection not working

**Possible Causes:**
1. Type not bound in container
2. Wrong dependency lifetime
3. Circular dependency
4. Missing [Inject] attribute

**Solutions:**

**Check Type Binding:**
```csharp
// In IContextLifecycle
public void OnConfigure(IContextBuilder builder)
{
    // Bind the type
    builder.Bind<IMyService, MyService>();
    
    // Or bind as singleton
    builder.BindService<MyService>();
}
```

**Verify [Inject] Attribute:**
```csharp
public class MyClass
{
    [Inject] private IMyService _service; // Ensure attribute is present
    
    // Constructor injection
    public MyClass(IMyService service)
    {
        _service = service;
    }
}
```

**Check for Circular Dependencies:**
```csharp
// This will throw InvalidOperationException
public class ServiceA
{
    [Inject] private ServiceB _b;
}

public class ServiceB
{
    [Inject] private ServiceA _a; // Circular!
}
```

**Solution:**
- Use constructor injection for one direction
- Introduce an interface to break the cycle
- Use lazy initialization

---

### ObservableProperty Not Notifying

**Symptoms:**
- Subscribers not called
- UI not updating
- No errors in console

**Possible Causes:**
1. Value not actually changing
2. Subscribed to wrong property
3. Subscription disposed prematurely

**Solutions:**

**Check Value Change:**
```csharp
var property = new ObservableProperty<int>(0);
property.Value = 0; // Won't fire - same value
property.Value = 1; // Will fire - different value
```

**Verify Subscription:**
```csharp
property.OnChanged((oldValue, newValue) =>
{
    Debug.Log($"Changed from {oldValue} to {newValue}");
});
```

**Check Subscription Lifetime:**
```csharp
// OnChanged returns nothing: keep a named handler so you can remove it later
property.OnChanged(OnValueChanged);
// Remove it only when you no longer need it
property.RemoveOnChanged(OnValueChanged);

// In a Mediator, TrackObservable removes the handler on unbind
TrackObservable(property, OnValueChanged);
```

---

## Build & Compilation Issues

### AOT Code Generation Errors

**Symptoms:**
- Warning `[Nexus] AOT Binder generation: no injectable types discovered. An empty binder was written to clear any previously generated injectors.`
- Error `[Nexus CodeGen Error] Field '<name>' in type '<type>' has [Inject]/[OptionalInject] attribute but is a value type (<type>). ...` (or `Property '<name>'`)
- Missing inject types
- Build fails on IL2CPP

**Possible Causes:**
1. No [Inject] attributes in user code
2. Auto-generation disabled
3. Wrong assembly scanning
4. Value type injection

**Solutions:**

**Enable Auto-Generation:**
```
Menu: Nexus > Auto-Generate AOT on Script Reload
```

**Manual Generation:**
```
Menu: Nexus > Generate AOT Binder
```

**Check for Inject Attributes:**
```csharp
// Ensure you have [Inject] attributes
public class MyClass
{
    [Inject] private IMyService _service;
}
```

**Check Value Type Injection:**
```csharp
// This will cause an error
public class MyClass
{
    [Inject] private int _value; // Value types not supported
}
```

**Solution:**
- Use reference types for injection
- Pass value types through constructor or method parameters

**Check Assembly Scanning:**
```csharp
// In ContextData ScriptableObject
public string[] AssemblyScopes = new string[]
{
    "Assembly-CSharp",
    "MyCustomAssembly"
};
```

---

### Missing Reference Errors

**Symptoms:**
- CS0246: Type or namespace could not be found
- CS0246: The type or namespace name 'X' could not be found

**Possible Causes:**
1. Missing using directive
2. Assembly reference missing
3. Wrong namespace

**Solutions:**

**Add Using Directives:**
```csharp
using Nexus.Core;
using Nexus.Core.Services;
```

**Check Assembly Definitions:**
- Ensure `com.nexus.core.asmdef` references `UnityEngine.UI`
- Check editor assembly references

**Verify Namespace:**
```csharp
// Services are in Nexus.Core.Services
using Nexus.Core.Services;

// Core types are in Nexus.Core
using Nexus.Core;
```

---

### IL2CPP Stripping Errors

**Symptoms:**
- Methods not found on IL2CPP builds
- Runtime errors on iOS/Android
- Missing type errors

**Possible Causes:**
1. link.xml not generated
2. Code stripping too aggressive
3. Missing Preserve attributes

**Solutions:**

**Regenerate link.xml:**
```
Menu: Nexus > Generate AOT Binder
```

**Check link.xml Location:**
```
Assets/Scripts/Nexus/link.xml
(default; Link Xml Output Path in Menu: Nexus > Editor Settings)
```
The generated file lists the project's own types. Each Nexus package ships a `link.xml` that preserves its own types (issue #110). Unity passes the linker only the files under `Assets`; `NexusPackagesLinkXml` hands it the packages' files, and the player build log names the merged file (`[Nexus] N package link.xml files handed to the linker as ...`).

**Verify link.xml Content:**
```xml
<linker>
  <assembly fullname="Assembly-CSharp">
    <type fullname="MyNamespace.MyClass" preserve="all"/>
  </assembly>
</linker>
```

**Manual Preserve (if needed):**
```csharp
using UnityEngine.Scripting;

[Preserve]
public class MyClass
{
    [Preserve]
    public void MyMethod() { }
}
```

**`EntryPointNotFoundException` at Medium or High stripping (issue #113):**
`[Preserve]` on a class keeps the class, not its methods. An interface may declare two methods with the same name and parameters that differ only in their number of type parameters, such as `Bind<T>()` and `Bind<TInterface, TImplementation>()`. When the game calls both through the interface, UnityLinker (6000.5.6f1, Medium and High; Low kept both) kept only the implementation declared first, and the call to the other threw `EntryPointNotFoundException`. Nexus preserves its own such classes. For a game class, preserve it whole in a `link.xml` of your own under `Assets` (`<type fullname="MyNamespace.MyClass" preserve="all"/>`), not in `Assets/Scripts/Nexus/link.xml`: Nexus generates that file, and an edit there stops every player build as out of date (`NexusBinderBuildGuard`) until a regeneration overwrites it. In the Nexus repository, `tools/unity-verify/check-stripped.ps1` finds a missing implementation in a player's stripped assemblies.

---

## Runtime Issues

### Context Not Active

**Symptoms:**
- `NexusRuntime.CurrentContext` returns null
- Signals not firing
- Services not resolving

**Possible Causes:**
1. Root GameObject not in scene
2. Context not initialized
3. Context disposed prematurely

**Solutions:**

**Check Root GameObject:**
```csharp
// Ensure Root component exists in scene
var root = FindAnyObjectByType<Root>();
if (root == null)
{
    Debug.LogError("Root GameObject not found in scene");
}
```

**Verify Context Initialization:**
```csharp
// Root creates its Context in Awake and starts it asynchronously; do not create one yourself
if (root.Context == null)
{
    Debug.LogError("Root has no context: its GameObject is inactive or startup failed (see the Console)");
}
else if (!root.IsInitialized)
{
    Debug.Log("Context is still starting; use services from OnStartAsync or IStartable");
}
```

**Check Context Lifecycle:**
```csharp
// Ensure context is not disposed
var context = NexusRuntime.CurrentContext;
if (context == null)
{
    Debug.LogError("Context is null or disposed");
}
```

---

### Null Reference Exceptions

**Symptoms:**
- NullReferenceException in commands
- NullReferenceException in mediators
- NullReferenceException in services

**Possible Causes:**
1. Injection failed
2. Service not available
3. Race conditions

**Solutions:**

**Check Injection:**
```csharp
public class MyCommand : ICommand<MySignal>
{
    [Inject] private IMyService _service;
    
    public void Execute(MySignal signal)
    {
        if (_service == null)
        {
            Debug.LogError("Service not injected");
            return;
        }
        _service.DoSomething();
    }
}
```

**Use TryResolve:**
```csharp
var service = context.TryResolve<IMyService>();
if (service != null)
{
    service.DoSomething();
}
```

**Check Mediator View Validity:**
```csharp
protected void ExecuteIfViewValid(Action<TView> action)
{
    if (IsViewValid)
    {
        action?.Invoke(View);
    }
}
```

---

### Memory Leaks

**Symptoms:**
- Memory usage increases over time
- GC spikes
- Performance degradation

**Possible Causes:**
1. Subscriptions not disposed
2. Event handlers not removed
3. Static references
4. Object pools not cleared

**Solutions:**

**Dispose Subscriptions:**
```csharp
public class MyMediator : Mediator<MyView>
{
    private ISignalSubscription _subscription;
    
    protected override void OnBind()
    {
        _subscription = SignalBus.Subscribe<MySignal>(OnSignal);
    }
    
    protected override void OnUnbind()
    {
        _subscription?.Dispose(); // Important!
    }
}
```

**Use Weak References (if needed):**
```csharp
private WeakReference<MyView> _viewRef;
```

**Clear Object Pools:**
```csharp
// ObjectPoolService clears its pools when its context disposes it.
// To clear them earlier, call the instance method on the injected IObjectPoolService:
_pool.ClearAllPools();
```

**Check Static References:**
```csharp
// Avoid static references to Unity objects
// Use context-based resolution instead
```

---

## Performance Issues

### High GC Allocation

**Symptoms:**
- Frequent GC collections
- Frame rate drops
- Profiler shows GC.Alloc

**Possible Causes:**
1. Boxing/unboxing
2. Lambda allocations
3. Temporary collections
4. String concatenation

**Solutions:**

**Use Object Pooling:**
```csharp
// Use the built-in pool service (inject IObjectPoolService as _pool)
var enemy = _pool.Spawn(enemyPrefab, position, rotation); // enemyPrefab: a component on a prefab
// Use enemy
_pool.Despawn(enemy.gameObject);
```

**Avoid Boxing:**
```csharp
// Bad - boxes value type
object boxed = 42;

// Good - use generics
T value = default;
```

**Reuse Collections:**
```csharp
// Bad - allocates new list each time
var list = new List<int>();

// Good - reuse list
private readonly List<int> _reusableList = new();
```

**Use StringBuilder:**
```csharp
// Bad - allocates strings
string text = "Hello " + name + "!";

// Good - uses StringBuilder
var sb = new StringBuilder();
sb.Append("Hello ");
sb.Append(name);
sb.Append("!");
string text = sb.ToString();
```

---

### Slow Signal Dispatch

**Symptoms:**
- Signal firing takes long time
- Frame rate drops on signal fire
- Profiler shows time in SignalBus

**Possible Causes:**
1. Too many handlers
2. Heavy command logic
3. Sequential execution
4. Lock contention

**Solutions:**

**Use Concurrent Execution:**
```csharp
// For independent operations
builder.BindCommand<MySignal, MyCommand>(
    ExecutionMode.Concurrent
);
```

**Optimize Command Logic:**
```csharp
// Move heavy work to async
public class MyCommand : IAsyncCommand<MySignal>
{
    public async ValueTask ExecuteAsync(MySignal signal, CancellationToken ct)
    {
        await Task.Run(() => HeavyWork(), ct);
    }
}
```

**Reduce Handler Count:**
```csharp
// Combine related handlers
// Use composite commands
// Filter unnecessary subscriptions
```

**Profile with Profiler:**
```
Window > Analysis > Profiler
Check SignalBus.Fire timing
```

---

## Editor Issues

### Nexus Window Not Opening

**Symptoms:**
- Menu item doesn't open window
- Window opens but shows errors
- Window freezes

**Possible Causes:**
1. Compilation errors
2. Missing assembly references
3. Editor-only code issues

**Solutions:**

**Check for Compilation Errors:**
```
Window > General > Console
Fix all errors before opening Nexus Window
```

**Reimport Package:**
```
Package Manager > com.nexus.core > Reimport
```

**Restart Unity Editor:**
```
File > Restart
```

**Clear Library Cache:**
```
Close Unity
Delete Library folder
Reopen Unity
```

---

### Code Generation Not Working

**Symptoms:**
- Error `[Nexus CodeGen] Overwrite blocked: the file at '<path>' does not contain the <auto-generated> tag. ...`
- Generated files not updating
- No binder file created

**Possible Causes:**
1. Auto-generation disabled
2. Write permissions
3. Path configuration
4. The generated binder or `link.xml` was edited by hand (its `<auto-generated>` tag is gone)

**Solutions:**

**Enable Auto-Generation:**
```
Menu: Nexus > Auto-Generate AOT on Script Reload
```

**Check Output Path:**
```
Menu: Nexus > Editor Settings (selects Assets/NexusEditorSettings.asset)
Binder Output Path and Link Xml Output Path (default Assets/Scripts/Nexus)
Ensure both paths are writable
```

**Manual Generation:**
```
Menu: Nexus > Generate AOT Binder
```

**Check Permissions:**
```
Ensure the output folder (default Assets/Scripts/Nexus/) is writable
Check folder permissions
```

---

### Stale Generated Binder

**Symptoms:**
- Warning: `The generated binder does not inject '<Type>.<Member>' ... Falling back to reflection for this type`
- Build fails with `[Nexus] The generated binder or link.xml is out of date`

**Cause:** an `[Inject]` member (or a new injectable type) was added after the binder was generated. A generated injector never sets members it does not know about.

**What Nexus does:** at runtime it checks each generated injector once per type; a required member left null switches that type to reflection injection (with the warning above). A player build is stopped by `NexusBinderBuildGuard` so IL2CPP never strips with a stale `link.xml`.

**Fix:**
```
Menu: Nexus > Generate AOT Binder
Headless: -executeMethod Nexus.Editor.NexusCodeGenerator.GenerateBinder
```
The guard only runs in projects that have a generated binder; a project on reflection injection is never stopped. Set `NEXUS_SKIP_BINDER_GUARD=1` only to build deliberately without regenerating.

**Removed or renamed type:** the binder then fails to compile itself (`error CS0246` inside `NexusGeneratedBinder.g.cs`). In the Editor, Nexus empties the file and regenerates it after the next successful compilation. In batchmode Unity stops on the first failed compilation ("Scripts have compiler errors", exit 1) unless it is started with `-ignorecompilererrors`; with it, the same healing runs in one command and the binder is written from the current code:
```
Unity.exe -batchmode -nographics -quit -ignorecompilererrors -projectPath <project> -executeMethod Nexus.Editor.NexusCodeGenerator.GenerateBinder
```
(checked on Unity 6000.5.6f1 with a binder that named a deleted type: exit 0, and the binder equal to one generated from a clean project). The flag lets any other compile error through as well, so run the build or the tests next; they fail on it. Emptying the file before a plain run also works.

The binder registers only the project's own types; the Nexus packages' types take the reflection path and each package preserves them in its own `link.xml`, which `NexusPackagesLinkXml` hands to the linker (issue #110). A package update that removes one of its types therefore leaves the binder compiling. A binder generated by an older package version still names package types, so the first update past #110 needs one regeneration. When every type it names still exists (an update from `3271239`), the binder compiles and the first player build stops with the out-of-date message above; a plain **Generate AOT Binder** fixes it. When it names a type the update removed, use the command above.

---

### Build Validation Errors

**Symptoms:**
- Build validation fails
- False positives
- Missing assembly errors

**Possible Causes:**
1. Wrong assembly configuration
2. Missing attributes
3. Custom validators

**Solutions:**

**Check Assembly Scopes:**
```csharp
// In ContextData
public string[] AssemblyScopes = new string[]
{
    "Assembly-CSharp"
};
```

**Bypass Validation (if needed):**
```
Set an environment variable before Unity starts the build:
NEXUS_DISABLE_VALIDATION=1    skip architecture validation (logs a warning)
NEXUS_VALIDATION_WARN_ONLY=1  report validation failures as warnings and continue the build
```

**Review Custom Validators:**
```csharp
// Check if you have custom validators
// Review their logic
```

### Optional Package Code Compiled Out

**Symptoms:**
- Error after a compile: `[Nexus] <assembly> is compiled without <DEFINE>: <DEFINE> needs <package> <expression>, the project has <version>`, or `[Nexus] <assembly> is not compiled although every package its defineConstraints name is installed`
- A player build stops with the same message (`VersionDefineBuildGuard`)

**Cause:** a Nexus package compiles its optional code only while the package it needs is installed at its asmdef's `versionDefines` expression (a bare version means that version or newer). Below it, Unity compiles the code out, or leaves out an assembly the symbol gates, and reports nothing: `com.nexus.ads.levelplay` at expression 9.7.0 with LevelPlay 9.6.0 compiled to an empty `Nexus.Ads.LevelPlay.dll` (4096 bytes) and the game ran no ads (issue #143, `Nexus/artifacts/define-cliff-20261031/`). A package that is not installed at all is not reported: its Nexus code is meant to stay out.

**Fix:** move the named package to a version the expression holds, or pin the Nexus package at a commit whose expression the installed version meets. The ad family's validator reports the same state for the selected provider as `provider.compiled`.

---

## AOT/IL2CPP Issues

### IL2CPP Build Fails

**Symptoms:**
- Build fails on IL2CPP
- Missing method errors
- Type not found errors

**Possible Causes:**
1. Missing link.xml
2. Code stripping
3. Generic instantiation issues

**Solutions:**

**Regenerate link.xml:**
```
Menu: Nexus > Generate AOT Binder
```

**Check link.xml in Build:**
```
Build Settings > Player Settings > Other Settings
Ensure link.xml is included
```

**Add Preserve Attributes:**
```csharp
[Preserve]
public class MyClass
{
    [Preserve]
    public void MyMethod() { }
}
```

**Check Generic Instantiation:**
```csharp
// Ensure generic types are instantiated
// Force instantiation if needed
var _ = new MyClass<int>();
```

---

### WebGL Build Issues

**Symptoms:**
- WebGL build fails
- Runtime errors in browser
- Performance issues

**Possible Causes:**
1. Code size limits
2. Memory limits
3. Threading issues

**Solutions:**

**Check Code Size:**
```
Build Settings > WebGL > Code Optimization
Set to Balanced or Size
```

**Increase Memory:**
```
Build Settings > WebGL > Memory Size
Increase to 256MB or higher
```

**Avoid Threading:**
```csharp
// WebGL has limited threading support
// Use async/await instead of threads
```

---

## Testing Issues

### Tests Not Running

**Symptoms:**
- Tests not appearing in Test Runner
- Tests fail to run
- Tests timeout

**Possible Causes:**
1. Wrong test assembly
2. Missing Test attributes
3. Setup/Teardown issues

**Solutions:**

**Check Test Assembly:**
```
Window > General > Test Runner
EditMode tests: com.nexus.core.editor.tests
PlayMode tests: com.nexus.core.tests
In a project that consumes the package, list com.nexus.core under "testables" in Packages/manifest.json
```

**Verify Test Attributes:**
```csharp
[TestFixture]
public class MyTests
{
    [Test]
    public void MyTest()
    {
        // Test logic
    }
}
```

**Check Setup/Teardown:**
```csharp
[SetUp]
public void Setup()
{
    // Initialize test context
}

[TearDown]
public void TearDown()
{
    // Clean up
}
```

---

### Mock Objects Not Working

**Symptoms:**
- Mock objects throw errors
- Mock methods not called
- Wrong mock behavior

**Possible Causes:**
1. Interface mismatch
2. Missing method implementations
3. Wrong mock setup

**Solutions:**

**Use Centralized MockContext:**
```csharp
using Nexus.Core;

var context = new MockContext();
```

**Implement Full Interface:**
```csharp
public class MockService : IMyService
{
    // Implement ALL interface members
    public void Method1() { }
    public void Method2() { }
    // ... all methods
}
```

**Check Mock Behavior:**
```csharp
// Add logging to verify calls
public class MockService : IMyService
{
    public int CallCount { get; private set; }
    
    public void Method1()
    {
        CallCount++;
        Debug.Log("Method1 called");
    }
}
```

---

## Getting Help

If you can't resolve your issue:

### 1. Check Documentation

- [README.md](README.md) - General documentation
- [MIGRATION.md](MIGRATION.md) - Migration guide
- [CHANGELOG.md](CHANGELOG.md) - Version history

### 2. Search GitHub Issues

- [GitHub Issues](https://github.com/CevatAYDN/GameContainer/issues)
- Search for your error message
- Check if issue is already reported

### 3. Open an Issue

- [New issue](https://github.com/CevatAYDN/GameContainer/issues/new/choose) (bug report or feature request template; the repository has no Discussions)
- Describe your issue clearly
- Provide code examples
- Include error messages

### 4. Create Minimal Reproduction

```csharp
// Create a minimal test case
[Test]
public void MinimalReproduction()
{
    // Only essential code
    var context = new MockContext();
    // ... minimal setup
    // ... reproduce issue
}
```

### 5. Include Debug Information

When reporting issues, include:

- Unity version
- Nexus Core version
- Platform (Windows, Mac, Linux, iOS, Android, WebGL)
- Error messages
- Stack traces
- Minimal reproduction code
- Screenshots (if applicable)

### 6. Enable Debug Logging

Add `NEXUS_DEBUG` to **Edit > Project Settings > Player > Other Settings > Scripting Define Symbols**
for the active build target, or press **Enable Full Causal Tracing & Recompile** in the Dashboard's
Tracer tab. A `#define NEXUS_DEBUG` in your own source file does not reach the package's assemblies.

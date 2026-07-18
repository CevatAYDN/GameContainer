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

1. **Plugin Interceptors** (may cancel dispatch)
2. **Cross-Context Broadcast** (if `[CrossContext]` attribute present)
3. **Commands** (mutate model state, execute in priority order)
4. **Subscriptions** (observe final state)

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

Nexus limits signal stack depth to **10**. If a subscription handler fires another signal, which in turn fires another, Nexus throws `NexusReentrancyException` after 10 nested levels.

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
// Ensure subscription is not disposed
var subscription = property.OnChanged((old, newValue) => { });
// Don't call subscription.Dispose() if you still need it
```

---

## Build & Compilation Issues

### AOT Code Generation Errors

**Symptoms:**
- "Code generation aborted" warning
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
Assets/NexusGenerated/link.xml
```

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
var root = FindObjectOfType<Root>();
if (root == null)
{
    Debug.LogError("Root GameObject not found in scene");
}
```

**Verify Context Initialization:**
```csharp
// Context should be initialized in Awake/Start
public class Root : MonoBehaviour
{
    private void Awake()
    {
        var context = new Context(this, contextData);
        context.Configure();
    }
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
// On context dispose
ObjectPoolService.ClearAll();
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
// Use built-in object pool
var obj = ObjectPoolService.Rent<MyObject>();
// Use obj
ObjectPoolService.Return(obj);
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
- "Code generation aborted" warning
- Generated files not updating
- No binder file created

**Possible Causes:**
1. Auto-generation disabled
2. Write permissions
3. Path configuration

**Solutions:**

**Enable Auto-Generation:**
```
Menu: Nexus > Auto-Generate AOT on Script Reload
```

**Check Output Path:**
```
Edit > Project Settings > Nexus > Binder Output Path
Ensure path is writable
```

**Manual Generation:**
```
Menu: Nexus > Generate AOT Binder
```

**Check Permissions:**
```
Ensure Assets/NexusGenerated/ is writable
Check folder permissions
```

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

**Disable Validation (if needed):**
```csharp
// In NexusEditorSettings
public bool EnableBuildValidation = false;
```

**Review Custom Validators:**
```csharp
// Check if you have custom validators
// Review their logic
```

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
Ensure com.nexus.core.tests is loaded
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

- [GitLab Issues](https://gitlab.com/beehivegame/GameContainer/issues)
- Search for your error message
- Check if issue is already reported

### 3. Ask in Discussions

- [GitLab Discussions](https://gitlab.com/beehivegame/GameContainer/discussions)
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

```csharp
// Enable Nexus debug logging
#define NEXUS_DEBUG

// Or in Player Settings
Add NEXUS_DEBUG to Scripting Define Symbols
```

---

**Last Updated:** 2026-07-07
**Nexus Core Version:** 0.3.1

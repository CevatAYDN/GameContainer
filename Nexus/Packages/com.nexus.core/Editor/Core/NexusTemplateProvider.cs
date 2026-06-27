namespace Nexus.Editor
{
    public static class NexusTemplateProvider
    {
        public static string GetLifecycleTemplateCode(string contextName)
        {
            return $@"using System.Threading;
using System.Threading.Tasks;
using Nexus.Core;
using UnityEngine;

namespace Nexus
{{
    public class {contextName}Lifecycle : IContextLifecycle
    {{
        public void OnConfigure(IContextBuilder builder)
        {{
            Debug.Log(""[{contextName}Lifecycle] Configuring context..."");
        }}

        public ValueTask OnInitializeAsync(CancellationToken ct) => default;
        public ValueTask OnStartAsync(CancellationToken ct) => default;
        public void OnDispose() {{ }}
    }}
}}
";
        }

        public static string GetSampleSignalCode()
        {
            return @"namespace Nexus.Samples
{
    public readonly struct SampleSignal
    {
        public readonly string Message;
        public SampleSignal(string message) => Message = message;
    }
}
";
        }

        public static string GetSampleCommandCode()
        {
            return @"using Nexus.Core;
using UnityEngine;

namespace Nexus.Samples
{
    [SignalHandler(typeof(SampleSignal))]
    public class SampleCommand : ICommand<SampleSignal>
    {
        public void Execute(SampleSignal signal)
        {
            Debug.Log($""[Nexus] SampleCommand executed successfully with message: {signal.Message}"");
        }
    }
}
";
        }

        public static string GetSignalsBoilerplate(string contextName)
        {
            return $@"namespace Nexus
{{
    public readonly struct {contextName}CounterSignal
    {{
        public readonly int Value;
        public {contextName}CounterSignal(int value) => Value = value;
    }}
}}
";
        }

        public static string GetModelInterfaceBoilerplate(string contextName)
        {
            return $@"using Nexus.Core;

namespace Nexus
{{
    public interface I{contextName}Model
    {{
        ObservableProperty<int> Counter {{ get; }}
        void Increment(int amount);
    }}
}}
";
        }

        public static string GetModelImplementationBoilerplate(string contextName)
        {
            return $@"using System.Threading;
using System.Threading.Tasks;
using Nexus.Core;

namespace Nexus
{{
    public class {contextName}Model : I{contextName}Model, IReactiveModel
    {{
        public readonly ObservableProperty<int> Counter = new(0);

        public ValueTask OnBind(CancellationToken ct) => default;

        public void Increment(int amount)
        {{
            Counter.Value += amount;
        }}
    }}
}}
";
        }

        public static string GetCommandBoilerplate(string contextName)
        {
            return $@"using Nexus.Core;
using UnityEngine;

namespace Nexus
{{
    public class {contextName}IncrementCommand : ICommand<{contextName}CounterSignal>
    {{
        [Inject] public I{contextName}Model Model {{ get; set; }}

        public void Execute({contextName}CounterSignal signal)
        {{
            Debug.Log($""[{{nameof({contextName}IncrementCommand)}}] Executing command with signal payload: {{signal.Value}}"");
            Model.Increment(signal.Value);
        }}
    }}
}}
";
        }

        public static string GetViewBoilerplate(string contextName)
        {
            return $@"using Nexus.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Nexus
{{
    [Mediator(typeof({contextName}Mediator))]
    public class {contextName}View : View
    {{
        public event System.Action OnButtonClicked;

        [Header(""UI References"")]
        [SerializeField] private Button incrementButton;
        [SerializeField] private Text counterText;

        protected override void OnBind(IContext context)
        {{
            if (incrementButton != null)
                incrementButton.onClick.AddListener(() => OnButtonClicked?.Invoke());
        }}

        protected override void OnUnbind()
        {{
            if (incrementButton != null)
                incrementButton.onClick.RemoveAllListeners();
        }}

        public void UpdateCounterText(int value)
        {{
            if (counterText != null)
                counterText.text = $""Counter: {{value}}"";
        }}
    }}
}}
";
        }

        public static string GetMediatorBoilerplate(string contextName)
        {
            return $@"using Nexus.Core;
using UnityEngine;

namespace Nexus
{{
    public class {contextName}Mediator : Mediator<{contextName}View>
    {{
        [Inject] public I{contextName}Model Model {{ get; set; }}

        protected override void OnBind()
        {{
            Model.Counter.OnChanged((oldVal, newVal) =>
            {{
                View.UpdateCounterText(newVal);
            }});
            View.UpdateCounterText(Model.Counter);
            View.OnButtonClicked += OnViewButtonClicked;
        }}

        protected override void OnUnbind()
        {{
            Model.Counter.ClearOnChanged();
            if (View != null) View.OnButtonClicked -= OnViewButtonClicked;
        }}

        private void OnViewButtonClicked()
        {{
            SignalBus.Fire(new {contextName}CounterSignal(1));
        }}
    }}
}}
";
        }

        public static string GetServiceBoilerplate(string serviceName, string contextName)
        {
            return $@"using System.Threading;
using System.Threading.Tasks;
using Nexus.Core;

namespace Nexus
{{
    public interface I{serviceName} : INexusService
    {{
    }}

    public class {serviceName} : NexusService<I{serviceName}>, I{serviceName}
    {{
        public override async ValueTask InitializeAsync(CancellationToken ct)
        {{
            // Service initialization logic here
            await Task.CompletedTask;
        }}

        public override void OnDispose()
        {{
            // Cleanup logic here
        }}
    }}
}}
";
        }

        public static string GetLifecycleBoilerplateWithBindings(string contextName)
        {
            return $@"using System.Threading;
using System.Threading.Tasks;
using Nexus.Core;
using UnityEngine;

namespace Nexus
{{
    public class {contextName}Lifecycle : IContextLifecycle
    {{
        public void OnConfigure(IContextBuilder builder)
        {{
            // Reactive model (auto-notifies views on property changes)
            builder.BindReactiveModel<I{contextName}Model, {contextName}Model>();

            // Managed service (initialized after configuration, disposed on shutdown)
            // builder.BindService<IMyService, MyService>();

            // Signal → Command binding
            builder.BindSignal<{contextName}CounterSignal>().To<{contextName}IncrementCommand>();
        }}

        public ValueTask OnInitializeAsync(CancellationToken ct) => default;
        public ValueTask OnStartAsync(CancellationToken ct) => default;
        public void OnDispose() {{ }}
    }}
}}
";
        }

        public static string GetGenericViewBoilerplate(string viewName, string contextName)
        {
            return $@"using Nexus.Core;
using UnityEngine;

namespace Nexus
{{
    [Mediator(typeof({viewName}Mediator))]
    public class {viewName}View : View
    {{
        protected override void OnBind(IContext context)
        {{
            Debug.Log($""[{{nameof({viewName}View)}}] Bound to context {contextName}"");
        }}

        protected override void OnUnbind()
        {{
            Debug.Log($""[{{nameof({viewName}View)}}] Unbound"");
        }}
    }}
}}
";
        }

        public static string GetGenericMediatorBoilerplate(string viewName, string contextName)
        {
            return $@"using Nexus.Core;
using UnityEngine;

namespace Nexus
{{
    public class {viewName}Mediator : Mediator<{viewName}View>
    {{
        protected override void OnBind() {{ }}
        protected override void OnUnbind() {{ }}
    }}
}}
";
        }

        public static string GetGenericSignalBoilerplate(string signalName, string contextName)
        {
            return $@"namespace Nexus
{{
    public readonly struct {signalName}
    {{
        public readonly string Message;
        public {signalName}(string message) => Message = message;
    }}
}}
";
        }

        public static string GetGenericCommandBoilerplate(string commandName, string signalName, string contextName)
        {
            return $@"using Nexus.Core;
using UnityEngine;

namespace Nexus
{{
    [SignalHandler(typeof({signalName}))]
    public class {commandName} : ICommand<{signalName}>
    {{
        public void Execute({signalName} signal)
        {{
            Debug.Log($""[{commandName}] Executed with message: {{signal.Message}}"");
        }}
    }}
}}
";
        }

        public static string GetGenericServiceBoilerplate(string serviceName, string contextName)
        {
            return $@"using System.Threading;
using System.Threading.Tasks;
using Nexus.Core;

namespace Nexus
{{
    public interface I{serviceName} : INexusService
    {{
    }}

    public class {serviceName} : NexusService<I{serviceName}>, I{serviceName}
    {{
        public override async ValueTask InitializeAsync(CancellationToken ct)
        {{
            await Task.CompletedTask;
        }}

        public override void OnDispose()
        {{
        }}
    }}
}}
";
        }
    }
}

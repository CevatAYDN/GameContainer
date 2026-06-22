using System.IO;
using UnityEditor;
using UnityEngine;

namespace Nexus.Editor
{
    public partial class NexusWindow
    {
        // ==========================================
        // ── BOILERPLATE CODE GENERATOR STRINGS
        // ==========================================
        private string GetLifecycleTemplateCode(string contextName)
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

        private string GetSampleSignalCode()
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

        private string GetSampleCommandCode()
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

        private string GetSignalsBoilerplate(string contextName)
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

        private string GetModelInterfaceBoilerplate(string contextName)
        {
            return $@"using System;

namespace Nexus
{{
    public interface I{contextName}Model
    {{
        int Counter {{ get; }}
        event Action<int> OnCounterChanged;
        void Increment(int amount);
    }}
}}
";
        }

        private string GetModelImplementationBoilerplate(string contextName)
        {
            return $@"using System;
using UnityEngine;

namespace Nexus
{{
    public class {contextName}Model : I{contextName}Model
    {{
        public int Counter {{ get; private set; }}
        public event Action<int> OnCounterChanged;

        public void Increment(int amount)
        {{
            Counter += amount;
            Debug.Log($""[{{nameof({contextName}Model)}}] Counter changed to: {{Counter}}"");
            OnCounterChanged?.Invoke(Counter);
        }}
    }}
}}
";
        }

        private string GetCommandBoilerplate(string contextName)
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

        private string GetViewBoilerplate(string contextName)
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

        private string GetMediatorBoilerplate(string contextName)
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
            Model.OnCounterChanged += OnModelCounterChanged;
            View.UpdateCounterText(Model.Counter);
            View.OnButtonClicked += OnViewButtonClicked;
        }}

        protected override void OnUnbind()
        {{
            if (Model != null) Model.OnCounterChanged -= OnModelCounterChanged;
            if (View != null) View.OnButtonClicked -= OnViewButtonClicked;
        }}

        private void OnViewButtonClicked()
        {{
            SignalBus.Fire(new {contextName}CounterSignal(1));
        }}

        private void OnModelCounterChanged(int newValue)
        {{
            View.UpdateCounterText(newValue);
        }}
    }}
}}
";
        }

        private string GetLifecycleBoilerplateWithBindings(string contextName)
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
            builder.BindModel<I{contextName}Model, {contextName}Model>();
            builder.BindSignal<{contextName}CounterSignal>().To<{contextName}IncrementCommand>();
        }}

        public ValueTask OnInitializeAsync(CancellationToken ct) => default;
        public ValueTask OnStartAsync(CancellationToken ct) => default;
        public void OnDispose() {{ }}
    }}
}}
";
        }

        private string GetGenericViewBoilerplate(string viewName, string contextName)
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

        private string GetGenericMediatorBoilerplate(string viewName, string contextName)
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
    }
}

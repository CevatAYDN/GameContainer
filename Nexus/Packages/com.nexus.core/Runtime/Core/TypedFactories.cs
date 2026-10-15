using System;

namespace Nexus.Core
{
    /// <summary>Factory that constructs and injects every instance through its container.</summary>
    internal sealed class ContainerFactory<TValue> : IFactory<TValue> where TValue : class
    {
        private readonly NexusDI _container;
        public ContainerFactory(NexusDI container) => _container = container;
        public TValue Create() => (TValue)_container.Construct(typeof(TValue), null);
    }

    /// <summary>Factory that constructs through its container with one constructor argument supplied per call.</summary>
    internal sealed class ContainerFactory<TParam, TValue> : IFactory<TParam, TValue> where TValue : class
    {
        private readonly NexusDI _container;
        public ContainerFactory(NexusDI container) => _container = container;
        public TValue Create(TParam param)
            => (TValue)_container.Construct(typeof(TValue), new[] { new NexusDI.ParameterOverride(typeof(TParam), param) });
    }

    /// <summary>Factory that constructs through its container with two constructor arguments supplied per call.</summary>
    internal sealed class ContainerFactory<TParam1, TParam2, TValue> : IFactory<TParam1, TParam2, TValue> where TValue : class
    {
        private readonly NexusDI _container;
        public ContainerFactory(NexusDI container) => _container = container;
        public TValue Create(TParam1 param1, TParam2 param2)
            => (TValue)_container.Construct(typeof(TValue), new[]
            {
                new NexusDI.ParameterOverride(typeof(TParam1), param1),
                new NexusDI.ParameterOverride(typeof(TParam2), param2),
            });
    }

    /// <summary>Factory backed by a user delegate; the container neither constructs nor injects the result.</summary>
    internal sealed class DelegateFactory<TValue> : IFactory<TValue>
    {
        private readonly Func<TValue> _create;
        public DelegateFactory(Func<TValue> create) => _create = create ?? throw new ArgumentNullException(nameof(create));
        public TValue Create() => _create();
    }

    internal sealed class DelegateFactory<TParam, TValue> : IFactory<TParam, TValue>
    {
        private readonly Func<TParam, TValue> _create;
        public DelegateFactory(Func<TParam, TValue> create) => _create = create ?? throw new ArgumentNullException(nameof(create));
        public TValue Create(TParam param) => _create(param);
    }

    internal sealed class DelegateFactory<TParam1, TParam2, TValue> : IFactory<TParam1, TParam2, TValue>
    {
        private readonly Func<TParam1, TParam2, TValue> _create;
        public DelegateFactory(Func<TParam1, TParam2, TValue> create) => _create = create ?? throw new ArgumentNullException(nameof(create));
        public TValue Create(TParam1 param1, TParam2 param2) => _create(param1, param2);
    }
}

using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Nexus.Core
{
    [Flags]
    public enum PluginCapabilities
    {
        None              = 0,
        SignalInterceptor = 1 << 0,
        CommandDecorator  = 1 << 1,
        ContextExtender   = 1 << 2,
        ModelSerializer   = 1 << 3,
        TraceProvider     = 1 << 4,
    }

    public class NexusPluginManifest
    {
        public string Name { get; }
        public string Version { get; }
        public PluginCapabilities Capabilities { get; }

        public NexusPluginManifest(string name, string version, PluginCapabilities capabilities)
        {
            Name = name;
            Version = version;
            Capabilities = capabilities;
        }
    }

    public interface INexusPlugin
    {
        NexusPluginManifest Manifest { get; }
        void OnPluginRegistered(IPluginContext context);
        void OnPluginRemoved();
    }

    public interface IPluginContext
    {
        IContext Context { get; }
        void RegisterSignalInterceptor(ISignalInterceptor interceptor);
        void RegisterCommandDecorator(ICommandDecorator decorator);
        void RegisterModelSerializer(IModelSerializer serializer);
        void RegisterTraceSink(INexusTraceSink sink);
    }

    public interface ISignalInterceptor
    {
        /// <summary>
        /// Intercepts a signal before dispatch.
        /// Returns true to continue dispatch, false to abort/block the signal.
        /// Can modify the signal if passed by ref.
        /// </summary>
        bool Intercept(ref object signal);
    }

    public interface ICommandDecorator
    {
        void DecorateExecute(object command, Action next);
        ValueTask DecorateExecuteAsync(object command, Func<ValueTask> next);
    }

    public interface IModelSerializer
    {
        string Serialize(object model);
        object Deserialize(string data, Type modelType);
    }

    public class PluginContext : IPluginContext
    {
        private readonly INexusPlugin _plugin;
        private readonly IContext _context;
        private readonly List<ISignalInterceptor> _interceptors = new();
        private readonly List<ICommandDecorator> _decorators = new();
        private readonly List<IModelSerializer> _serializers = new();
        private readonly List<INexusTraceSink> _traceSinks = new();
        
        // Volatile snapshot fields for Interceptors and Decorators, rebuilt on every mutation.
        // SignalBus dispatch reads Interceptors/Decorators properties during Fire/ExecuteWithDecorators.
        // Without snapshots, a concurrent Add() and iteration race throws InvalidOperationException.
        private volatile IReadOnlyList<ISignalInterceptor> _interceptorsSnapshot = Array.Empty<ISignalInterceptor>();
        private volatile IReadOnlyList<ICommandDecorator> _decoratorsSnapshot = Array.Empty<ICommandDecorator>();
        private readonly object _listLock = new();

        public IContext Context => _context;
        public IReadOnlyList<ISignalInterceptor> Interceptors => _interceptorsSnapshot;
        public IReadOnlyList<ICommandDecorator> Decorators => _decoratorsSnapshot;
        public IReadOnlyList<IModelSerializer> Serializers => _serializers;
        public IReadOnlyList<INexusTraceSink> TraceSinks => _traceSinks;

        public PluginContext(INexusPlugin plugin, IContext context)
        {
            _plugin = plugin;
            _context = context;
        }

        public void RegisterSignalInterceptor(ISignalInterceptor interceptor)
        {
            if ((_plugin.Manifest.Capabilities & PluginCapabilities.SignalInterceptor) == 0)
            {
                throw new UnauthorizedPluginAccessException($"Plugin '{_plugin.Manifest.Name}' is not authorized to register SignalInterceptors. Please declare SignalInterceptor capability in manifest.");
            }
            lock (_listLock)
            {
                _interceptors.Add(interceptor);
                _interceptorsSnapshot = new List<ISignalInterceptor>(_interceptors);
            }
            if (_context is Context ctx)
            {
                ctx.IncrementInterceptorsCount();
            }
        }

        public void RegisterCommandDecorator(ICommandDecorator decorator)
        {
            if ((_plugin.Manifest.Capabilities & PluginCapabilities.CommandDecorator) == 0)
            {
                throw new UnauthorizedPluginAccessException($"Plugin '{_plugin.Manifest.Name}' is not authorized to register CommandDecorators. Please declare CommandDecorator capability in manifest.");
            }
            lock (_listLock)
            {
                _decorators.Add(decorator);
                _decoratorsSnapshot = new List<ICommandDecorator>(_decorators);
            }
        }

        public void RegisterModelSerializer(IModelSerializer serializer)
        {
            if ((_plugin.Manifest.Capabilities & PluginCapabilities.ModelSerializer) == 0)
            {
                throw new UnauthorizedPluginAccessException($"Plugin '{_plugin.Manifest.Name}' is not authorized to register ModelSerializers. Please declare ModelSerializer capability in manifest.");
            }
            lock (_listLock)
            {
                _serializers.Add(serializer);
            }
        }

        public void RegisterTraceSink(INexusTraceSink sink)
        {
            if ((_plugin.Manifest.Capabilities & PluginCapabilities.TraceProvider) == 0)
            {
                throw new UnauthorizedPluginAccessException($"Plugin '{_plugin.Manifest.Name}' is not authorized to register TraceSinks. Please declare TraceProvider capability in manifest.");
            }
            lock (_listLock)
            {
                _traceSinks.Add(sink);
            }
            NexusTrace.AddSink(sink);
        }

        public void Clear()
        {
            List<INexusTraceSink> sinksToClear;
            lock (_listLock)
            {
                sinksToClear = new List<INexusTraceSink>(_traceSinks);
            }

            foreach (var sink in sinksToClear)
            {
                NexusTrace.RemoveSink(sink);
            }
            if (_context is Context ctx)
            {
                int interceptorsToDecrement;
                lock (_listLock) { interceptorsToDecrement = _interceptors.Count; }
                for (int i = 0; i < interceptorsToDecrement; i++)
                {
                    ctx.DecrementInterceptorsCount();
                }
            }
            lock (_listLock)
            {
                _interceptors.Clear();
                _decorators.Clear();
                _serializers.Clear();
                _traceSinks.Clear();
                _interceptorsSnapshot = Array.Empty<ISignalInterceptor>();
                _decoratorsSnapshot = Array.Empty<ICommandDecorator>();
            }
        }
    }
}

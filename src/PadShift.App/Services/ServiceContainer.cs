using PadShift.Core.Host;

namespace PadShift.App.Services;

/// <summary>
/// A deliberately tiny type-keyed container. The client only needs singletons for the host, the
/// ViewModels and the app settings, so dozens of lines beat pulling in a full container.
/// </summary>
public sealed class ServiceContainer
{
    private readonly object _gate = new();
    private readonly Dictionary<Type, Func<ServiceContainer, object>> _factories = new();
    private readonly Dictionary<Type, object> _instances = new();

    /// <summary>Registers an existing object.</summary>
    public void RegisterInstance<T>(T instance) where T : class
    {
        ArgumentNullException.ThrowIfNull(instance);
        lock (_gate)
        {
            _instances[typeof(T)] = instance;
            _factories.Remove(typeof(T));
        }
    }

    /// <summary>Registers a lazily created singleton.</summary>
    public void RegisterSingleton<T>(Func<ServiceContainer, T> factory) where T : class
    {
        ArgumentNullException.ThrowIfNull(factory);
        lock (_gate)
        {
            _factories[typeof(T)] = container => factory(container);
            _instances.Remove(typeof(T));
        }
    }

    public T Resolve<T>() where T : class
    {
        lock (_gate)
        {
            if (_instances.TryGetValue(typeof(T), out object? existing))
            {
                return (T)existing;
            }

            if (!_factories.TryGetValue(typeof(T), out Func<ServiceContainer, object>? factory))
            {
                throw new InvalidOperationException($"服务未注册: {typeof(T).FullName}");
            }

            var created = (T)factory(this);
            _instances[typeof(T)] = created;
            return created;
        }
    }

    public bool TryResolve<T>(out T? service) where T : class
    {
        lock (_gate)
        {
            if (_instances.TryGetValue(typeof(T), out object? existing))
            {
                service = (T)existing;
                return true;
            }

            if (!_factories.ContainsKey(typeof(T)))
            {
                service = null;
                return false;
            }

            service = (T)_factories[typeof(T)](this);
            _instances[typeof(T)] = service;
            return true;
        }
    }
}

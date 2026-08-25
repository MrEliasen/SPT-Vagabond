using System.Collections.Concurrent;

namespace Vagabond.Server.Services;

internal static class ReflectionUtil
{
    private static readonly ConcurrentDictionary<Type, object> ServicesByType = new();

    public static void Register<T>(T service) where T : class
    {
        var type = typeof(T);

        ServicesByType[type] = service;
    }

    public static T? GetService<T>() where T : class
    {
        return ServicesByType.TryGetValue(typeof(T), out var service)
            ? service as T
            : null;
    }
}
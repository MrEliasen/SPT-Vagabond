using System.Collections.Concurrent;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Services.Modding;
using Vagabond.Common.Definitions;

namespace Vagabond.Server.Services;

internal static class StateService
{
    private const string ModKey = "dev.oogabooga.spt-vagabond";
    private static readonly ConcurrentDictionary<MongoId, Lock> StateLocks = new();
    private static readonly ConcurrentDictionary<MongoId, VersionCounter> StateVersions = new();

    private sealed class VersionCounter
    {
        public long Value;
    }

    public static void WithState(MongoId sessionId, Action<VagabondSessionState> action)
    {
        lock (GetStateLock(sessionId))
        {
            action(GetState(sessionId));
        }
    }

    public static T WithState<T>(MongoId sessionId, Func<VagabondSessionState, T> action)
    {
        lock (GetStateLock(sessionId))
        {
            return action(GetState(sessionId));
        }
    }

    public static VagabondSessionState GetState(string sessionId)
    {
        var profileDataService = ReflectionUtil.GetService<ProfileDataService>();
        if (profileDataService == null)
        {
            return new();
        }

        lock (GetStateLock(sessionId))
        {
            return profileDataService.GetProfileDataAsync<VagabondSessionState>(sessionId, ModKey)
                .GetAwaiter().GetResult() ?? new VagabondSessionState();
        }
    }

    public static void SaveState(string sessionId, VagabondSessionState state)
    {
        var profileDataService = ReflectionUtil.GetService<ProfileDataService>();
        if (profileDataService == null)
        {
            return;
        }

        lock (GetStateLock(sessionId))
        {
            Interlocked.Increment(ref GetVersionCounter(sessionId).Value);
            profileDataService.SaveProfileDataAsync(sessionId, ModKey, state).GetAwaiter().GetResult();
        }
    }

    public static long GetStateVersion(MongoId sessionId)
    {
        return Volatile.Read(ref GetVersionCounter(sessionId).Value);
    }

    private static Lock GetStateLock(MongoId sessionId)
    {
        return StateLocks.GetOrAdd(sessionId, _ => new Lock());
    }

    private static VersionCounter GetVersionCounter(MongoId sessionId)
    {
        return StateVersions.GetOrAdd(sessionId, _ => new VersionCounter());
    }
}
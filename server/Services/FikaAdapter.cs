using System.Reflection;
using SPTarkov.Server.Core.Models.Common;

namespace Vagabond.Server.Services;

public static class FikaAdapter
{
    private static volatile bool _initialized;
    private static volatile bool _available;
    private static object? _headlessService;
    private static PropertyInfo? _headlessClientsProp;
    private static MethodInfo? _headlessClientsTryGetValue;
    private static PropertyInfo? _requesterSessionIdProp;
    private static PropertyInfo? _headlessPlayersProp;
    private static object? _matchService;
    private static MethodInfo? _getMatchIdByProfileMethod;
    private static MethodInfo? _getMatchMethod;
    private static PropertyInfo? _matchIsHeadlessProp;

    public static bool Init(IServiceProvider services)
    {
        if (_initialized)
        {
            return _available;
        }

        var fikaAsm = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "FikaServer");

        if (fikaAsm == null)
        {
            _initialized = true;
            return false;
        }

        var headlessServiceType = fikaAsm.GetType("FikaServer.Services.Headless.HeadlessService");
        if (headlessServiceType != null)
        {
            _headlessService = services.GetService(headlessServiceType);
            _headlessClientsProp = headlessServiceType.GetProperty("HeadlessClients");

            var clientsDictType = _headlessClientsProp?.PropertyType;
            _headlessClientsTryGetValue = clientsDictType?.GetMethod("TryGetValue");
            var clientInfoType = _headlessClientsTryGetValue?.GetParameters() is { Length: 2 } tgvParams
                ? tgvParams[1].ParameterType.GetElementType()
                : null;
            _requesterSessionIdProp = clientInfoType?.GetProperty("RequesterSessionID");
            _headlessPlayersProp = clientInfoType?.GetProperty("Players");
        }

        var matchServiceType = fikaAsm.GetType("FikaServer.Services.MatchService");
        if (matchServiceType != null)
        {
            _matchService = services.GetService(matchServiceType);
            _getMatchIdByProfileMethod = matchServiceType.GetMethod("GetMatchIdByProfile", [typeof(MongoId)]);
            _getMatchMethod = matchServiceType.GetMethod("GetMatch", [typeof(MongoId?)]);
            _matchIsHeadlessProp = _getMatchMethod?.ReturnType.GetProperty("IsHeadless");
        }

        _available = _headlessClientsProp != null || (_matchService != null && _getMatchIdByProfileMethod != null);
        _initialized = true;
        return _available;
    }

    public static MongoId GetCanonicalSessionId(MongoId sessionId)
    {
        var matchId = TryGetMatchIdByProfile(sessionId);
        if (matchId.HasValue)
        {
            var match = TryGetMatch(matchId.Value);
            var isHeadless = match != null && ((bool?)_matchIsHeadlessProp?.GetValue(match) ?? false);
            if (isHeadless)
            {
                return GetRaidOwnerSessionId(matchId.Value);
            }

            return matchId.Value;
        }

        return GetRaidOwnerSessionId(sessionId);
    }

    public static MongoId GetMatchHostSessionId(MongoId sessionId)
    {
        return TryGetMatchIdByProfile(sessionId) ?? sessionId;
    }

    public static MongoId GetRaidOwnerSessionId(MongoId sessionId)
    {
        if (_headlessService == null || _headlessClientsProp == null || _headlessClientsTryGetValue == null)
        {
            return sessionId;
        }

        var headlessClients = _headlessClientsProp.GetValue(_headlessService);
        if (headlessClients == null)
        {
            return sessionId;
        }

        var args = new object?[] { sessionId, null };
        var found = (bool)_headlessClientsTryGetValue.Invoke(headlessClients, args)!;
        if (!found || args[1] == null)
        {
            return sessionId;
        }

        var requesterSessionId = _requesterSessionIdProp?.GetValue(args[1]) as string;
        if (string.IsNullOrWhiteSpace(requesterSessionId))
        {
            return sessionId;
        }

        VagabondLogger.Debug($"Raid Owner SessionId: {requesterSessionId}");
        return new MongoId(requesterSessionId);
    }

    public static IReadOnlyList<MongoId>? GetHeadlessMatchMemberSessionIds(MongoId headlessSessionId)
    {
        if (_headlessService == null || _headlessClientsProp == null || _headlessClientsTryGetValue == null
            || _headlessPlayersProp == null)
        {
            return null;
        }

        var headlessClients = _headlessClientsProp.GetValue(_headlessService);
        if (headlessClients == null)
        {
            return null;
        }

        var args = new object?[] { headlessSessionId, null };
        var found = (bool)_headlessClientsTryGetValue.Invoke(headlessClients, args)!;
        if (!found || args[1] == null)
        {
            return null;
        }

        if (_headlessPlayersProp.GetValue(args[1]) is not List<MongoId> players)
        {
            return null;
        }

        try
        {
            return players.ToArray();
        }
        catch
        {
            return null;
        }
    }

    private static MongoId? TryGetMatchIdByProfile(MongoId sessionId)
    {
        if (_matchService == null || _getMatchIdByProfileMethod == null)
        {
            return null;
        }

        var result = _getMatchIdByProfileMethod.Invoke(_matchService, [sessionId]);
        if (result == null)
        {
            return null;
        }

        return (MongoId)result;
    }

    private static object? TryGetMatch(MongoId matchId)
    {
        if (_matchService == null || _getMatchMethod == null)
        {
            return null;
        }

        return _getMatchMethod.Invoke(_matchService, [matchId]);
    }
}
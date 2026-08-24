using System.Reflection;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Servers;
using Vagabond.Server.Services;

namespace Vagabond.Server.Patches;

public sealed class SaveProfilePatch : AbstractPatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(SaveServer).GetMethod(nameof(SaveServer.SaveProfileAsync))!;
    }

    [PatchPrefix]
    internal static bool Prefix(
        MongoId sessionID,
        ref Task<long> __result,
        out VirtualStashService.ProfileSaveScope? __state)
    {
        __state = VirtualStashService.BeginProfileSaveScope(sessionID);
        if (__state is { SaveSkipped: true })
        {
            __state = null;
            __result = Task.FromResult(0L);
            return false;
        }

        return true;
    }

    [PatchPostfix]
    internal static void Postfix(ref Task<long> __result, VirtualStashService.ProfileSaveScope? __state)
    {
        if (__state == null)
        {
            return;
        }

        __result = CompleteAfter(__result, __state);
    }

    [PatchFinalizer]
    internal static Exception? Finalizer(Exception? __exception, VirtualStashService.ProfileSaveScope? __state)
    {
        if (__exception != null)
        {
            __state?.Complete();
        }

        return __exception;
    }

    private static async Task<long> CompleteAfter(Task<long> original, VirtualStashService.ProfileSaveScope scope)
    {
        try
        {
            return await original.ConfigureAwait(false);
        }
        finally
        {
            scope.Complete();
        }
    }
}

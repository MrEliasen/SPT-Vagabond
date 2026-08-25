using SPTarkov.Server.Core.Models.Utils;

namespace Vagabond.Server.Models;

public sealed class SyncStateServerRequest : IRequestData
{
    public bool? InRaid { get; set; }
}
using System.Threading.Tasks;
using Newtonsoft.Json;
using SPT.Common.Http;
using Vagabond.Common.Models;

namespace Vagabond.Client.Networking
{
    internal static class ApiClient
    {
        public static async Task<SyncStateResponse> SyncVagabondState(SyncStateRequest body)
        {
            string payload =
                await RequestHandler.PostJsonAsync("/vagabond/sync/state", JsonConvert.SerializeObject(body));
            return JsonConvert.DeserializeObject<SyncStateResponse>(payload);
        }

        public static async Task<SyncExfilResponse> SyncExfilData(GetExfilDataRequest body)
        {
            string payload =
                await RequestHandler.PostJsonAsync("/vagabond/sync/exfils", JsonConvert.SerializeObject(body));
            return JsonConvert.DeserializeObject<SyncExfilResponse>(payload);
        }

        public static SyncStateResponse SyncVagabondStateBlocking(SyncStateRequest body)
        {
            return JsonConvert.DeserializeObject<SyncStateResponse>(
                RequestHandler.PostJson("/vagabond/sync/state", JsonConvert.SerializeObject(body)));
        }

        public static SyncExfilResponse SyncExfilDataBlocking(GetExfilDataRequest body)
        {
            return JsonConvert.DeserializeObject<SyncExfilResponse>(RequestHandler.PostJson("/vagabond/sync/exfils",
                JsonConvert.SerializeObject(body)));
        }

        public static async Task<PlaceHideoutResponse> EstablishHideoutExtract(PlaceHideoutRequest body)
        {
            string payload =
                await RequestHandler.PostJsonAsync("/vagabond/hideout/establish", JsonConvert.SerializeObject(body));
            return JsonConvert.DeserializeObject<PlaceHideoutResponse>(payload);
        }
    }
}
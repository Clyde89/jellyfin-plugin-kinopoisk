using System.Net.Http;

namespace KinopoiskUnofficialInfo.ApiClient
{
    public partial class Client
    {
        internal KinopoiskApiAuthentication Authentication { get; set; }

        partial void PrepareRequest(HttpClient client, HttpRequestMessage request, string url)
        {
            Authentication?.Apply(request);
        }
    }
}

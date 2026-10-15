using System;
using System.Net.Http;

namespace KinopoiskUnofficialInfo.ApiClient
{
    public sealed class KinopoiskApiAuthentication
    {
        private readonly Func<string> _tokenProvider;

        public KinopoiskApiAuthentication(string apiToken, Func<string> tokenProvider = null)
        {
            _tokenProvider = tokenProvider ?? (() => apiToken);
        }

        public string GetRequiredToken()
        {
            var token = _tokenProvider()?.Trim();
            if (string.IsNullOrEmpty(token))
                throw new InvalidOperationException("API-токен КиноПоиска не настроен.");
            return token;
        }

        public void Apply(HttpRequestMessage request, string token = null)
        {
            // Заголовок добавлен к отдельному запросу без изменения общего HttpClient.
            request.Headers.Add("X-API-KEY", token ?? GetRequiredToken());
        }
    }
}

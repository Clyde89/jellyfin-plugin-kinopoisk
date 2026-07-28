using Newtonsoft.Json;
using Xunit;

namespace KinopoiskUnofficialInfo.ApiClient.Tests
{
    public class FilmSearchTypeConverterTests
    {
        [Theory]
        [InlineData("FILM", FilmSearchResponse_filmsType.FILM)]
        [InlineData("VIDEO", FilmSearchResponse_filmsType.FILM)]
        [InlineData("TV_SHOW", FilmSearchResponse_filmsType.TV_SHOW)]
        [InlineData("MINI_SERIES", FilmSearchResponse_filmsType.TV_SHOW)]
        [InlineData("TV_SERIES", FilmSearchResponse_filmsType.TV_SHOW)]
        [InlineData("UNKNOWN", FilmSearchResponse_filmsType.UNKNOWN)]
        [InlineData("UNSUPPORTED", FilmSearchResponse_filmsType.UNKNOWN)]
        public void ShouldNormalizeSearchType(string sourceType, FilmSearchResponse_filmsType expectedType)
        {
            var settings = new JsonSerializerSettings
            {
                ContractResolver = new DeclarationPatcherContractResolver()
            };

            var result = JsonConvert.DeserializeObject<FilmSearchResponse_films>(
                $"{{\"filmId\":1,\"type\":\"{sourceType}\"}}",
                settings);

            Assert.NotNull(result);
            Assert.Equal(expectedType, result.Type);
        }
    }
}

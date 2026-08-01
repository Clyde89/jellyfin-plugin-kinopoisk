using System.Linq;
using KinopoiskUnofficialInfo.ApiClient;
using Newtonsoft.Json;
using Xunit;

namespace KinopoiskUnofficialInfo.ApiClient.Tests
{
    public class ApiResponseCompatibilityTests
    {
        [Fact]
        public void ShouldDeserializeDistributionWithNullSubtype()
        {
            const string json = """
                {
                  "total": 1,
                  "items": [
                    {
                      "type": "WORLD_PREMIER",
                      "subType": null,
                      "date": "2025-03-07",
                      "reRelease": false,
                      "country": { "country": "США" },
                      "companies": []
                    }
                  ]
                }
                """;

            var response = JsonConvert.DeserializeObject<DistributionResponse>(json);
            var item = Assert.Single(response.Items);

            Assert.Equal(DistributionType.WORLD_PREMIER, item.Type);
            Assert.Equal("2025-03-07", item.Date);
            Assert.False(item.HasSubType);
        }

        [Fact]
        public void ShouldPreserveOriginalOperatorProfessionWithoutDirectorClassification()
        {
            const string json = """
                {
                  "staffId": 123,
                  "nameRu": "Оператор",
                  "nameEn": "Cinematographer",
                  "posterUrl": "https://example.test/operator.jpg",
                  "professionText": "Оператор",
                  "professionKey": "OPERATOR"
                }
                """;

            var response = JsonConvert.DeserializeObject<StaffResponse>(json);

            Assert.Equal(StaffResponseProfessionKey.OPERATOR, response.OriginalProfessionKey);
            Assert.Equal(StaffResponseProfessionKey.UNKNOWN, response.ProfessionKey);
            Assert.Equal("Оператор", response.ProfessionText);
        }
    }
}

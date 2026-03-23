using System.Net;
using System.Text;
using FluentAssertions;
using LocalAgentTravelPlanner.Tools;

namespace LocalAgentTravelPlanner.Tests.Tools
{
    public class ResearchToolsTests
    {
        #region Helpers

        private static HttpClient CreateMockHttpClient(
            Func<HttpRequestMessage, (HttpStatusCode Status, string Content)> handler)
        {
            var mockHandler = new MockHttpMessageHandler(handler);
            var client = new HttpClient(mockHandler);
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "TestAgent/1.0");
            return client;
        }

        private static ResearchTools CreateSut(HttpClient client)
        {
            // ResearchTools reads OPENTRIPMAP_API_KEY from env; set it for tests that need it
            return new ResearchTools(client);
        }

        private class MockHttpMessageHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, (HttpStatusCode Status, string Content)> _handler;

            public MockHttpMessageHandler(
                Func<HttpRequestMessage, (HttpStatusCode Status, string Content)> handler)
                => _handler = handler;

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var (status, content) = _handler(request);
                return Task.FromResult(new HttpResponseMessage(status)
                {
                    Content = new StringContent(content, Encoding.UTF8, "application/json")
                });
            }
        }

        #endregion

        #region GetWeatherForecast Tests

        private const string TestCity = "TestCity";

        private const string NominatimGeocodingResponse = """
            [{"lat":"28.2096","lon":"83.9856","display_name":"TestCity"}]
            """;

        private const string OpenMeteoForecastResponse = """
            {
                "daily": {
                    "time": ["2026-03-23", "2026-03-24", "2026-03-25"],
                    "weather_code": [0, 61, 2],
                    "temperature_2m_max": [25.0, 22.0, 24.0],
                    "temperature_2m_min": [12.0, 10.0, 11.0],
                    "precipitation_sum": [0.0, 5.2, 0.0]
                }
            }
            """;

        [Fact]
        public async Task GetWeatherForecast_WhenApiSucceeds_ReturnsLiveData()
        {
            var client = CreateMockHttpClient(req =>
            {
                var url = req.RequestUri!.ToString();
                if (url.Contains("nominatim"))
                    return (HttpStatusCode.OK, NominatimGeocodingResponse);
                if (url.Contains("open-meteo"))
                    return (HttpStatusCode.OK, OpenMeteoForecastResponse);
                return (HttpStatusCode.NotFound, "");
            });
            var sut = CreateSut(client);

            var result = await sut.GetWeatherForecast(TestCity, 3);

            result.Should().Contain("Open-Meteo");
            result.Should().Contain("2026-03-23");
            result.Should().Contain("Clear sky");
            result.Should().Contain("Rain");
        }

        [Fact]
        public async Task GetWeatherForecast_WhenApiThrows_ReturnsFallback()
        {
            var client = CreateMockHttpClient(_ =>
                throw new HttpRequestException("Network error"));
            var sut = CreateSut(client);

            var result = await sut.GetWeatherForecast(TestCity, 3);

            result.Should().Contain("temporarily unavailable");
            result.Should().Contain(TestCity);
        }

        [Fact]
        public async Task GetWeatherForecast_WhenGeocodingReturnsEmpty_ReturnsFallback()
        {
            var client = CreateMockHttpClient(req =>
            {
                if (req.RequestUri!.ToString().Contains("nominatim"))
                    return (HttpStatusCode.OK, "[]");
                return (HttpStatusCode.NotFound, "");
            });
            var sut = CreateSut(client);

            var result = await sut.GetWeatherForecast("NonexistentCity", 3);

            result.Should().Contain("temporarily unavailable");
        }

        [Theory]
        [InlineData(0, 1)]
        [InlineData(20, 16)]
        public async Task GetWeatherForecast_ClampsDaysBetween1And16(int input, int expected)
        {
            string? capturedUrl = null;
            var client = CreateMockHttpClient(req =>
            {
                var url = req.RequestUri!.ToString();
                if (url.Contains("nominatim"))
                    return (HttpStatusCode.OK, NominatimGeocodingResponse);
                if (url.Contains("open-meteo"))
                {
                    capturedUrl = url;
                    return (HttpStatusCode.OK, OpenMeteoForecastResponse);
                }
                return (HttpStatusCode.NotFound, "");
            });
            var sut = CreateSut(client);

            await sut.GetWeatherForecast(TestCity, input);

            capturedUrl.Should().Contain($"forecast_days={expected}");
        }

        #endregion

        #region SearchHotels Tests

        private const string OtmRadiusHotelsResponse = """
            {
                "type": "FeatureCollection",
                "features": [
                    {
                        "type": "Feature",
                        "properties": { "xid": "W123", "name": "Hotel Lakeside" }
                    },
                    {
                        "type": "Feature",
                        "properties": { "xid": "W456", "name": "Mountain View Inn" }
                    },
                    {
                        "type": "Feature",
                        "properties": { "xid": "W789", "name": "" }
                    }
                ]
            }
            """;

        [Fact]
        public async Task SearchHotels_WhenApiSucceeds_ReturnsHotelNames()
        {
            Environment.SetEnvironmentVariable("OPENTRIPMAP_API_KEY", "test-key");
            try
            {
                var client = CreateMockHttpClient(req =>
                {
                    var url = req.RequestUri!.ToString();
                    if (url.Contains("nominatim"))
                        return (HttpStatusCode.OK, NominatimGeocodingResponse);
                    if (url.Contains("opentripmap"))
                        return (HttpStatusCode.OK, OtmRadiusHotelsResponse);
                    return (HttpStatusCode.NotFound, "");
                });
                var sut = CreateSut(client);

                var result = await sut.SearchHotels(TestCity, "Mid-range");

                result.Should().Contain("Hotel Lakeside");
                result.Should().Contain("Mountain View Inn");
                result.Should().NotContain("3."); // empty name filtered out
            }
            finally
            {
                Environment.SetEnvironmentVariable("OPENTRIPMAP_API_KEY", null);
            }
        }

        [Fact]
        public async Task SearchHotels_WhenNoApiKey_ReturnsFallback()
        {
            Environment.SetEnvironmentVariable("OPENTRIPMAP_API_KEY", null);
            var client = CreateMockHttpClient(_ => (HttpStatusCode.OK, "{}"));
            var sut = CreateSut(client);

            var result = await sut.SearchHotels(TestCity, "Budget");

            result.Should().Contain("unavailable");
        }

        #endregion

        #region GetAttractions Tests

        private const string OtmRadiusAttractionsResponse = """
            {
                "type": "FeatureCollection",
                "features": [
                    {
                        "type": "Feature",
                        "properties": { "xid": "A1", "name": "Ancient Temple", "rate": 7 }
                    },
                    {
                        "type": "Feature",
                        "properties": { "xid": "A2", "name": "Lake View Point", "rate": 5 }
                    }
                ]
            }
            """;

        private const string OtmDetailResponse = """
            {
                "name": "Ancient Temple",
                "kinds": "historic,cultural",
                "wikipedia_extracts": {
                    "text": "A historic temple dating back to the 15th century."
                }
            }
            """;

        [Fact]
        public async Task GetAttractions_WhenApiSucceeds_ReturnsFormattedAttractions()
        {
            Environment.SetEnvironmentVariable("OPENTRIPMAP_API_KEY", "test-key");
            try
            {
                var client = CreateMockHttpClient(req =>
                {
                    var url = req.RequestUri!.ToString();
                    if (url.Contains("nominatim"))
                        return (HttpStatusCode.OK, NominatimGeocodingResponse);
                    if (url.Contains("places/radius"))
                        return (HttpStatusCode.OK, OtmRadiusAttractionsResponse);
                    if (url.Contains("places/xid"))
                        return (HttpStatusCode.OK, OtmDetailResponse);
                    return (HttpStatusCode.NotFound, "");
                });
                var sut = CreateSut(client);

                var result = await sut.GetAttractions(TestCity);

                result.Should().Contain("OpenTripMap");
                result.Should().Contain("Ancient Temple");
                result.Should().Contain("historic,cultural");
            }
            finally
            {
                Environment.SetEnvironmentVariable("OPENTRIPMAP_API_KEY", null);
            }
        }

        [Fact]
        public async Task GetAttractions_WhenDetailFetchFails_StillReturnsName()
        {
            Environment.SetEnvironmentVariable("OPENTRIPMAP_API_KEY", "test-key");
            try
            {
                var client = CreateMockHttpClient(req =>
                {
                    var url = req.RequestUri!.ToString();
                    if (url.Contains("nominatim"))
                        return (HttpStatusCode.OK, NominatimGeocodingResponse);
                    if (url.Contains("places/radius"))
                        return (HttpStatusCode.OK, OtmRadiusAttractionsResponse);
                    if (url.Contains("places/xid"))
                        throw new HttpRequestException("Detail fetch failed");
                    return (HttpStatusCode.NotFound, "");
                });
                var sut = CreateSut(client);

                var result = await sut.GetAttractions(TestCity);

                result.Should().Contain("Ancient Temple");
                result.Should().Contain("Lake View Point");
            }
            finally
            {
                Environment.SetEnvironmentVariable("OPENTRIPMAP_API_KEY", null);
            }
        }

        #endregion

        #region GetFoodRecommendations Tests

        private const string OtmRadiusFoodResponse = """
            {
                "type": "FeatureCollection",
                "features": [
                    {
                        "type": "Feature",
                        "properties": { "xid": "F1", "name": "Lakeside Cafe", "kinds": "foods,cafes" }
                    },
                    {
                        "type": "Feature",
                        "properties": { "xid": "F2", "name": "", "kinds": "foods" }
                    }
                ]
            }
            """;

        [Fact]
        public async Task GetFoodRecommendations_WhenApiSucceeds_ReturnsRestaurants()
        {
            Environment.SetEnvironmentVariable("OPENTRIPMAP_API_KEY", "test-key");
            try
            {
                var client = CreateMockHttpClient(req =>
                {
                    var url = req.RequestUri!.ToString();
                    if (url.Contains("nominatim"))
                        return (HttpStatusCode.OK, NominatimGeocodingResponse);
                    if (url.Contains("opentripmap"))
                        return (HttpStatusCode.OK, OtmRadiusFoodResponse);
                    return (HttpStatusCode.NotFound, "");
                });
                var sut = CreateSut(client);

                var result = await sut.GetFoodRecommendations(TestCity);

                result.Should().Contain("Lakeside Cafe");
                result.Should().Contain("OpenTripMap");
            }
            finally
            {
                Environment.SetEnvironmentVariable("OPENTRIPMAP_API_KEY", null);
            }
        }

        [Fact]
        public async Task GetFoodRecommendations_WhenNoApiKey_ReturnsFallback()
        {
            Environment.SetEnvironmentVariable("OPENTRIPMAP_API_KEY", null);
            var client = CreateMockHttpClient(_ => (HttpStatusCode.OK, "{}"));
            var sut = CreateSut(client);

            var result = await sut.GetFoodRecommendations(TestCity);

            result.Should().Contain("unavailable");
        }

        #endregion

        #region GetTransportOptions Tests

        private const string NominatimCityAResponse = """
            [{"lat":"48.8566","lon":"2.3522","display_name":"CityA"}]
            """;

        private const string NominatimCityBResponse = """
            [{"lat":"51.5074","lon":"-0.1278","display_name":"CityB"}]
            """;

        private const string OsrmRouteResponse = """
            {
                "code": "Ok",
                "routes": [{
                    "distance": 450000,
                    "duration": 28800
                }]
            }
            """;

        [Fact]
        public async Task GetTransportOptions_WhenOsrmSucceeds_ReturnsDistanceBasedOptions()
        {
            var client = CreateMockHttpClient(req =>
            {
                var url = req.RequestUri!.ToString();
                if (url.Contains("nominatim") && url.Contains("CityA"))
                    return (HttpStatusCode.OK, NominatimCityAResponse);
                if (url.Contains("nominatim") && url.Contains("CityB"))
                    return (HttpStatusCode.OK, NominatimCityBResponse);
                if (url.Contains("router.project-osrm"))
                    return (HttpStatusCode.OK, OsrmRouteResponse);
                return (HttpStatusCode.NotFound, "");
            });
            var sut = CreateSut(client);

            var result = await sut.GetTransportOptions("CityA", "CityB");

            result.Should().Contain("CityA");
            result.Should().Contain("CityB");
            result.Should().Contain("450 km");
            result.Should().Contain("BUS");
            result.Should().Contain("PRIVATE CAR");
            result.Should().Contain("FLIGHT"); // >200km triggers flight
            result.Should().Contain("TRAIN"); // >100km triggers train
        }

        [Fact]
        public async Task GetTransportOptions_WhenApiThrows_ReturnsFallback()
        {
            var client = CreateMockHttpClient(_ =>
                throw new HttpRequestException("Network error"));
            var sut = CreateSut(client);

            var result = await sut.GetTransportOptions("CityA", "CityB");

            result.Should().Contain("temporarily unavailable");
        }

        #endregion

        #region GetSafetyInfo Tests

        [Theory]
        [InlineData("Tokyo")]
        [InlineData("Paris")]
        [InlineData("Nepal")]
        public void GetSafetyInfo_AnyLocation_ReturnsGenericSafetyGuidance(string location)
        {
            var client = CreateMockHttpClient(_ => (HttpStatusCode.OK, "{}"));
            var sut = CreateSut(client);

            var result = sut.GetSafetyInfo(location);

            result.Should().Contain(location);
            result.Should().Contain("GENERAL SAFETY");
            result.Should().Contain("HEALTH CONSIDERATIONS");
            result.Should().Contain("PERSONAL SECURITY");
            result.Should().Contain("EMERGENCY PREPARATION");
        }

        #endregion
    }
}

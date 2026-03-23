using System.Net;
using System.Text;
using FluentAssertions;
using LocalAgentTravelPlanner.Tools;

namespace LocalAgentTravelPlanner.Tests.Tools
{
    public class TravelToolsTests
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

        private const string TestCity = "TestCity";

        private const string NominatimGeocodingResponse = """
            [{"lat":"28.2096","lon":"83.9856","display_name":"TestCity"}]
            """;

        private const string OpenMeteoCurrentResponse = """
            {
                "current": {
                    "temperature_2m": 23.5,
                    "weather_code": 2,
                    "precipitation": 0.0
                }
            }
            """;

        #endregion

        #region GetWeather Tests

        [Fact]
        public async Task GetWeather_WhenApiSucceeds_ReturnsLiveData()
        {
            var client = CreateMockHttpClient(req =>
            {
                var url = req.RequestUri!.ToString();
                if (url.Contains("nominatim"))
                    return (HttpStatusCode.OK, NominatimGeocodingResponse);
                if (url.Contains("open-meteo"))
                    return (HttpStatusCode.OK, OpenMeteoCurrentResponse);
                return (HttpStatusCode.NotFound, "");
            });
            var sut = new TravelTools(client);

            var result = await sut.GetWeather(TestCity);

            result.Should().Contain("Open-Meteo");
            result.Should().Contain("23.5");
            result.Should().Contain("Partly cloudy");
        }

        [Fact]
        public async Task GetWeather_WhenApiThrows_ReturnsFallback()
        {
            var client = CreateMockHttpClient(_ =>
                throw new HttpRequestException("Connection refused"));
            var sut = new TravelTools(client);

            var result = await sut.GetWeather(TestCity);

            result.Should().Contain("temporarily unavailable");
        }

        [Fact]
        public async Task GetWeather_WhenGeocodingReturnsEmpty_ReturnsFallback()
        {
            var client = CreateMockHttpClient(req =>
            {
                if (req.RequestUri!.ToString().Contains("nominatim"))
                    return (HttpStatusCode.OK, "[]");
                return (HttpStatusCode.NotFound, "");
            });
            var sut = new TravelTools(client);

            var result = await sut.GetWeather("NonexistentCity");

            result.Should().Contain("temporarily unavailable");
        }

        [Fact]
        public async Task GetWeather_WhenRaining_IncludesPrecipitation()
        {
            var rainyResponse = """
                {
                    "current": {
                        "temperature_2m": 18.0,
                        "weather_code": 61,
                        "precipitation": 3.5
                    }
                }
                """;
            var client = CreateMockHttpClient(req =>
            {
                var url = req.RequestUri!.ToString();
                if (url.Contains("nominatim"))
                    return (HttpStatusCode.OK, NominatimGeocodingResponse);
                if (url.Contains("open-meteo"))
                    return (HttpStatusCode.OK, rainyResponse);
                return (HttpStatusCode.NotFound, "");
            });
            var sut = new TravelTools(client);

            var result = await sut.GetWeather(TestCity);

            result.Should().Contain("Rain");
            result.Should().Contain("3.5");
        }

        #endregion

        #region GetFlightEstimate Tests

        private const string OsrmRouteResponse = """
            {
                "code": "Ok",
                "routes": [{
                    "distance": 150000,
                    "duration": 18000
                }]
            }
            """;

        private const string OsrmLongRouteResponse = """
            {
                "code": "Ok",
                "routes": [{
                    "distance": 500000,
                    "duration": 36000
                }]
            }
            """;

        private const string NominatimCityAResponse = """
            [{"lat":"48.8566","lon":"2.3522","display_name":"CityA"}]
            """;

        private const string NominatimCityBResponse = """
            [{"lat":"51.5074","lon":"-0.1278","display_name":"CityB"}]
            """;

        [Fact]
        public async Task GetFlightEstimate_WhenOsrmSucceeds_ReturnsDistanceBasedEstimates()
        {
            var client = CreateMockHttpClient(req =>
            {
                var url = req.RequestUri!.ToString();
                if (url.Contains("nominatim") && url.Contains("CityA"))
                    return (HttpStatusCode.OK, NominatimCityAResponse);
                if (url.Contains("nominatim") && url.Contains("CityB"))
                    return (HttpStatusCode.OK, NominatimCityBResponse);
                if (url.Contains("router.project-osrm"))
                    return (HttpStatusCode.OK, OsrmLongRouteResponse);
                return (HttpStatusCode.NotFound, "");
            });
            var sut = new TravelTools(client);

            var result = await sut.GetFlightEstimate("CityA", "CityB");

            result.Should().Contain("CityA");
            result.Should().Contain("CityB");
            result.Should().Contain("500 km");
            result.Should().Contain("DRIVING");
            result.Should().Contain("FLIGHT"); // >300km triggers flight suggestion
        }

        [Fact]
        public async Task GetFlightEstimate_ShortDistance_OmitsFlight()
        {
            var client = CreateMockHttpClient(req =>
            {
                var url = req.RequestUri!.ToString();
                if (url.Contains("nominatim") && url.Contains("CityA"))
                    return (HttpStatusCode.OK, NominatimCityAResponse);
                if (url.Contains("nominatim") && url.Contains("CityB"))
                    return (HttpStatusCode.OK, NominatimCityBResponse);
                if (url.Contains("router.project-osrm"))
                    return (HttpStatusCode.OK, OsrmRouteResponse); // 150km
                return (HttpStatusCode.NotFound, "");
            });
            var sut = new TravelTools(client);

            var result = await sut.GetFlightEstimate("CityA", "CityB");

            result.Should().Contain("DRIVING");
            result.Should().Contain("BUS");
            result.Should().Contain("TRAIN"); // >100km triggers train
            result.Should().NotContain("FLIGHT"); // <300km, no flight
        }

        [Fact]
        public async Task GetFlightEstimate_WhenApiThrows_ReturnsFallback()
        {
            var client = CreateMockHttpClient(_ =>
                throw new HttpRequestException("Network error"));
            var sut = new TravelTools(client);

            var result = await sut.GetFlightEstimate("CityA", "CityB");

            result.Should().Contain("temporarily unavailable");
        }

        #endregion

        #region GetTravelTime Tests

        [Fact]
        public async Task GetTravelTime_WhenOsrmSucceeds_ReturnsDistanceAndTime()
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
            var sut = new TravelTools(client);

            var result = await sut.GetTravelTime("CityA", "CityB", "drive");

            result.Should().Contain("150.0 km");
            result.Should().Contain("CityA");
            result.Should().Contain("CityB");
        }

        [Fact]
        public async Task GetTravelTime_ByBus_IncludesBusEstimate()
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
            var sut = new TravelTools(client);

            var result = await sut.GetTravelTime("CityA", "CityB", "bus");

            result.Should().Contain("bus estimate");
        }

        [Fact]
        public async Task GetTravelTime_WhenApiThrows_ReturnsFallback()
        {
            var client = CreateMockHttpClient(_ =>
                throw new HttpRequestException("Network error"));
            var sut = new TravelTools(client);

            var result = await sut.GetTravelTime("CityA", "CityB", "drive");

            result.Should().Contain("temporarily unavailable");
        }

        #endregion

        #region GetEmergencyContacts Tests

        [Theory]
        [InlineData("Nepal", "Police: 100")]
        [InlineData("Tokyo", "Police: 110")]
        [InlineData("London", "999 or 112")]
        [InlineData("New York", "911")]
        [InlineData("Paris", "112")]
        [InlineData("Sydney", "000")]
        [InlineData("Delhi", "Police: 100")]
        [InlineData("Bangkok", "191")]
        public void GetEmergencyContacts_KnownLocations_ReturnsCorrectNumbers(
            string location, string expectedNumber)
        {
            var sut = new TravelTools(CreateMockHttpClient(_ => (HttpStatusCode.OK, "{}")));

            var result = sut.GetEmergencyContacts(location);

            result.Should().Contain(expectedNumber);
            result.Should().Contain("EMERGENCY NUMBERS");
        }

        [Fact]
        public void GetEmergencyContacts_UnknownLocation_ReturnsGenericWithUniversalNumbers()
        {
            var sut = new TravelTools(CreateMockHttpClient(_ => (HttpStatusCode.OK, "{}")));

            var result = sut.GetEmergencyContacts("Antartica");

            result.Should().Contain("Antartica");
            result.Should().Contain("112");
            result.Should().Contain("911");
        }

        #endregion

        #region GetVisaInfo Tests

        [Fact]
        public void GetVisaInfo_ReturnsGenericGuidance()
        {
            var sut = new TravelTools(CreateMockHttpClient(_ => (HttpStatusCode.OK, "{}")));

            var result = sut.GetVisaInfo("American", "Japan");

            result.Should().Contain("American");
            result.Should().Contain("Japan");
            result.Should().Contain("COMMON ENTRY TYPES");
            result.Should().Contain("TYPICAL REQUIREMENTS");
            result.Should().Contain("passport");
        }

        [Fact]
        public void GetVisaInfo_WithoutDestination_ReturnsGenericDestinationText()
        {
            var sut = new TravelTools(CreateMockHttpClient(_ => (HttpStatusCode.OK, "{}")));

            var result = sut.GetVisaInfo("British");

            result.Should().Contain("British");
            result.Should().Contain("your destination");
        }

        #endregion
    }
}

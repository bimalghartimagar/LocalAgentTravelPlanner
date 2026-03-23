using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace LocalAgentTravelPlanner.Tools
{
    /// <summary>
    /// Research-specific tools for gathering destination information.
    /// Uses Open-Meteo (weather), Nominatim (geocoding), and OpenTripMap (attractions/food/hotels).
    /// All methods fall back to hardcoded data when APIs are unavailable.
    /// </summary>
    public class ResearchTools
    {
        private readonly HttpClient _http;
        private readonly string _otmKey;

        public ResearchTools(HttpClient http)
        {
            _http = http;
            _otmKey = Environment.GetEnvironmentVariable("OPENTRIPMAP_API_KEY") ?? "";
        }

        [Description("Gets current weather and forecast for a destination.")]
        public async Task<string> GetWeatherForecast(
            [Description("The city name")] string city,
            [Description("Number of days to forecast")] int days = 3)
        {
            try
            {
                var coords = await GeocodingHelper.GetCoordinatesAsync(_http, city);
                if (coords is { } c)
                {
                    var lat = c.Lat.ToString("F4", CultureInfo.InvariantCulture);
                    var lon = c.Lon.ToString("F4", CultureInfo.InvariantCulture);
                    var forecastDays = Math.Clamp(days, 1, 16);
                    var url = $"https://api.open-meteo.com/v1/forecast" +
                              $"?latitude={lat}&longitude={lon}" +
                              $"&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_sum" +
                              $"&forecast_days={forecastDays}&timezone=auto";

                    var json = await _http.GetStringAsync(url);
                    using var doc = JsonDocument.Parse(json);
                    var daily = doc.RootElement.GetProperty("daily");

                    var times = daily.GetProperty("time").EnumerateArray()
                        .Select(x => x.GetString() ?? "").ToList();
                    var maxTemps = daily.GetProperty("temperature_2m_max").EnumerateArray()
                        .Select(x => x.ValueKind == JsonValueKind.Null ? 0.0 : x.GetDouble()).ToList();
                    var minTemps = daily.GetProperty("temperature_2m_min").EnumerateArray()
                        .Select(x => x.ValueKind == JsonValueKind.Null ? 0.0 : x.GetDouble()).ToList();
                    var precipitation = daily.GetProperty("precipitation_sum").EnumerateArray()
                        .Select(x => x.ValueKind == JsonValueKind.Null ? 0.0 : x.GetDouble()).ToList();
                    var codes = daily.GetProperty("weather_code").EnumerateArray()
                        .Select(x => x.ValueKind == JsonValueKind.Null ? 0 : (int)x.GetDouble()).ToList();

                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine($"Weather for {city} ({times.Count}-day forecast, source: Open-Meteo):");
                    for (int i = 0; i < times.Count; i++)
                    {
                        var desc = DescribeWeatherCode(codes[i]);
                        var rain = precipitation[i] > 0 ? $", Rain: {precipitation[i]:F1}mm" : "";
                        sb.AppendLine($"- {times[i]}: {minTemps[i]:F0}°C – {maxTemps[i]:F0}°C, {desc}{rain}");
                    }
                    sb.AppendLine("- Packing tip: Dress in layers, check local updates on day of travel");
                    return sb.ToString();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ResearchTools] GetWeatherForecast failed for '{city}': {ex.Message}");
            }

            return FallbackWeatherForecast(city, days);
        }

        private static string DescribeWeatherCode(int code) => code switch
        {
            0 => "Clear sky",
            1 => "Mainly clear",
            2 => "Partly cloudy",
            3 => "Overcast",
            45 or 48 => "Foggy",
            51 or 53 or 55 => "Drizzle",
            61 or 63 or 65 => "Rain",
            71 or 73 or 75 => "Snow",
            80 or 81 or 82 => "Rain showers",
            95 or 96 or 99 => "Thunderstorm",
            _ => "Variable conditions"
        };

        private static string FallbackWeatherForecast(string city, int days)
        {
            return $"""
                Weather for {city} ({days}-day forecast):
                - Forecast data temporarily unavailable (Open-Meteo API unreachable).
                - Recommended: Check local forecast before departure.
                """;
        }

        [Description("Finds hotels in a destination within a budget range.")]
        public async Task<string> SearchHotels(
            [Description("The destination city")] string city,
            [Description("Budget category: Budget, Mid-range, or Luxury")] string category)
        {
            if (!string.IsNullOrEmpty(_otmKey))
            {
                try
                {
                    var coords = await GeocodingHelper.GetCoordinatesAsync(_http, city);
                    if (coords is { } c)
                    {
                        var lat = c.Lat.ToString("F4", CultureInfo.InvariantCulture);
                        var lon = c.Lon.ToString("F4", CultureInfo.InvariantCulture);
                        var url = $"https://api.opentripmap.com/0.1/en/places/radius" +
                                  $"?radius=10000&lon={lon}&lat={lat}" +
                                  $"&kinds=accomodations&limit=10&apikey={_otmKey}";

                        var json = await _http.GetStringAsync(url);
                        using var doc = JsonDocument.Parse(json);
                        var features = doc.RootElement.GetProperty("features").EnumerateArray().ToList();

                        if (features.Count > 0)
                        {
                            var sb = new System.Text.StringBuilder();
                            sb.AppendLine($"Accommodations in {city} ({category}):");
                            sb.AppendLine("(Names from OpenStreetMap via OpenTripMap; prices not available — check booking platforms for rates)");
                            int i = 1;
                            foreach (var f in features.Take(10))
                            {
                                var name = f.GetProperty("properties").GetProperty("name").GetString();
                                if (!string.IsNullOrWhiteSpace(name))
                                    sb.AppendLine($"{i++}. {name}");
                            }
                            sb.AppendLine("Tip: Use booking.com or agoda.com for current prices and availability.");
                            return sb.ToString();
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ResearchTools] SearchHotels failed for '{city}': {ex.Message}");
                }
            }

            return FallbackSearchHotels(city, category);
        }

        private static string FallbackSearchHotels(string city, string category)
        {
            return $"""
                Hotels in {city} ({category}):
                Live hotel data unavailable (OPENTRIPMAP_API_KEY not set or API unreachable).
                Use booking.com or agoda.com for current availability and pricing.
                """;
        }

        [Description("Finds top attractions and things to do at a destination.")]
        public async Task<string> GetAttractions([Description("The destination city")] string city)
        {
            if (!string.IsNullOrEmpty(_otmKey))
            {
                try
                {
                    var coords = await GeocodingHelper.GetCoordinatesAsync(_http, city);
                    if (coords is { } c)
                    {
                        var lat = c.Lat.ToString("F4", CultureInfo.InvariantCulture);
                        var lon = c.Lon.ToString("F4", CultureInfo.InvariantCulture);
                        var kinds = "interesting_places,historic,natural,cultural,sport";
                        var listUrl = $"https://api.opentripmap.com/0.1/en/places/radius" +
                                      $"?radius=10000&lon={lon}&lat={lat}" +
                                      $"&kinds={kinds}&limit=12&rate=3&apikey={_otmKey}";

                        var listJson = await _http.GetStringAsync(listUrl);
                        using var listDoc = JsonDocument.Parse(listJson);
                        var features = listDoc.RootElement.GetProperty("features").EnumerateArray()
                            .Select(f =>
                            {
                                var props = f.GetProperty("properties");
                                return new
                                {
                                    Xid = props.GetProperty("xid").GetString() ?? "",
                                    Name = props.GetProperty("name").GetString() ?? "",
                                    Rate = props.TryGetProperty("rate", out var r)
                                        ? (int)r.GetDouble() : 0
                                };
                            })
                            .Where(f => !string.IsNullOrWhiteSpace(f.Name))
                            .OrderByDescending(f => f.Rate)
                            .Take(5)
                            .ToList();

                        if (features.Count > 0)
                        {
                            var detailTasks = features.Select(async (f, idx) =>
                            {
                                try
                                {
                                    var detailUrl = $"https://api.opentripmap.com/0.1/en/places/xid/{f.Xid}?apikey={_otmKey}";
                                    var detailJson = await _http.GetStringAsync(detailUrl);
                                    using var detailDoc = JsonDocument.Parse(detailJson);
                                    var root = detailDoc.RootElement;

                                    var name = root.TryGetProperty("name", out var n) ? n.GetString() ?? f.Name : f.Name;
                                    var placeKinds = root.TryGetProperty("kinds", out var k) ? k.GetString() ?? "" : "";
                                    var extract = "";
                                    if (root.TryGetProperty("wikipedia_extracts", out var wiki) &&
                                        wiki.TryGetProperty("text", out var text))
                                    {
                                        var full = text.GetString() ?? "";
                                        extract = full.Length > 200 ? full[..200] + "..." : full;
                                    }

                                    return $"{idx + 1}. {name}\n   Categories: {placeKinds}\n" +
                                           (string.IsNullOrEmpty(extract) ? "" : $"   {extract}\n");
                                }
                                catch (Exception ex)
                                {
                                    Debug.WriteLine($"[ResearchTools] GetAttractions detail fetch failed for XID '{f.Xid}': {ex.Message}");
                                    return $"{idx + 1}. {f.Name}\n";
                                }
                            });

                            var details = await Task.WhenAll(detailTasks);
                            var sb = new System.Text.StringBuilder();
                            sb.AppendLine($"Top Attractions in {city} (source: OpenTripMap):");
                            sb.AppendLine();
                            foreach (var d in details)
                                sb.Append(d);
                            sb.AppendLine("Note: Entry fees and opening hours not available from this source — check locally.");
                            return sb.ToString();
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ResearchTools] GetAttractions failed for '{city}': {ex.Message}");
                }
            }

            return FallbackGetAttractions(city);
        }

        private static string FallbackGetAttractions(string city)
        {
            return $"""
                Attractions in {city}:
                Live attraction data unavailable (OPENTRIPMAP_API_KEY not set or API unreachable).
                Check local tourism board or TripAdvisor for comprehensive listings.
                """;
        }

        [Description("Gets local food recommendations and average meal costs.")]
        public async Task<string> GetFoodRecommendations([Description("The destination city")] string city)
        {
            if (!string.IsNullOrEmpty(_otmKey))
            {
                try
                {
                    var coords = await GeocodingHelper.GetCoordinatesAsync(_http, city);
                    if (coords is { } c)
                    {
                        var lat = c.Lat.ToString("F4", CultureInfo.InvariantCulture);
                        var lon = c.Lon.ToString("F4", CultureInfo.InvariantCulture);
                        var url = $"https://api.opentripmap.com/0.1/en/places/radius" +
                                  $"?radius=10000&lon={lon}&lat={lat}" +
                                  $"&kinds=foods&limit=15&apikey={_otmKey}";

                        var json = await _http.GetStringAsync(url);
                        using var doc = JsonDocument.Parse(json);
                        var features = doc.RootElement.GetProperty("features").EnumerateArray().ToList();

                        if (features.Count > 0)
                        {
                            var sb = new System.Text.StringBuilder();
                            sb.AppendLine($"Food & Dining in {city} (source: OpenTripMap/OSM):");
                            sb.AppendLine("(Prices not available from this source — expect local market rates)");
                            sb.AppendLine();
                            int i = 1;
                            foreach (var f in features)
                            {
                                var props = f.GetProperty("properties");
                                var name = props.GetProperty("name").GetString();
                                var placeKinds = props.TryGetProperty("kinds", out var k) ? k.GetString() ?? "" : "";
                                if (!string.IsNullOrWhiteSpace(name))
                                    sb.AppendLine($"{i++}. {name} ({placeKinds})");
                            }
                            sb.AppendLine();
                            sb.AppendLine("Tip: Ask locals for current recommendations and pricing.");
                            return sb.ToString();
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ResearchTools] GetFoodRecommendations failed for '{city}': {ex.Message}");
                }
            }

            return FallbackFoodRecommendations(city);
        }

        private static string FallbackFoodRecommendations(string city)
        {
            return $"""
                Food in {city}:
                Live restaurant data unavailable (OPENTRIPMAP_API_KEY not set or API unreachable).
                Ask locals or check Google Maps for current dining recommendations.
                """;
        }

        [Description("Gets transportation options between two cities.")]
        public async Task<string> GetTransportOptions(
            [Description("Origin city")] string fromCity,
            [Description("Destination city")] string toCity)
        {
            try
            {
                var fromCoords = await GeocodingHelper.GetCoordinatesAsync(_http, fromCity);
                var toCoords = await GeocodingHelper.GetCoordinatesAsync(_http, toCity);

                if (fromCoords is { } fc && toCoords is { } tc)
                {
                    var route = await GetOsrmRouteAsync(fc.Lat, fc.Lon, tc.Lat, tc.Lon);
                    if (route != null)
                        return FormatTransportOptions(fromCity, toCity, route.Value.DistanceKm, route.Value.DurationHours);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ResearchTools] GetTransportOptions failed for '{fromCity}' -> '{toCity}': {ex.Message}");
            }

            return $"""
                Transport from {fromCity} to {toCity}:
                - Route data temporarily unavailable.
                - Check local transport services, Google Maps, or Rome2Rio for schedules and pricing.
                """;
        }

        private async Task<(double DistanceKm, double DurationHours)?> GetOsrmRouteAsync(
            double lat1, double lon1, double lat2, double lon2)
        {
            var url = $"https://router.project-osrm.org/route/v1/driving/" +
                      $"{lon1.ToString("F4", CultureInfo.InvariantCulture)},{lat1.ToString("F4", CultureInfo.InvariantCulture)};" +
                      $"{lon2.ToString("F4", CultureInfo.InvariantCulture)},{lat2.ToString("F4", CultureInfo.InvariantCulture)}" +
                      $"?overview=false";

            var json = await _http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);
            var routes = doc.RootElement.GetProperty("routes");
            if (routes.GetArrayLength() > 0)
            {
                var first = routes[0];
                var distanceM = first.GetProperty("distance").GetDouble();
                var durationS = first.GetProperty("duration").GetDouble();
                return (distanceM / 1000.0, durationS / 3600.0);
            }
            return null;
        }

        private static string FormatTransportOptions(string from, string to, double distKm, double driveHours)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Transport from {from} to {to} (estimated {distKm:F0} km):");
            sb.AppendLine();

            // Flight suggestion for long distances
            if (distKm > 200)
            {
                var flightMin = (int)(distKm / 800.0 * 60) + 30; // rough cruise speed + overhead
                sb.AppendLine($"  FLIGHT (if available)");
                sb.AppendLine($"  - Estimated flight time: ~{flightMin} minutes");
                sb.AppendLine($"  - Check Skyscanner, Google Flights, or local airlines for routes and fares");
                sb.AppendLine();
            }

            // Bus estimate: ~1.3x driving time
            var busHours = driveHours * 1.3;
            sb.AppendLine($"  BUS / COACH");
            sb.AppendLine($"  - Estimated duration: {FormatDuration(busHours)}");
            sb.AppendLine($"  - Typically the most affordable option");
            sb.AppendLine($"  - Check local bus operators or Rome2Rio for schedules");
            sb.AppendLine();

            // Driving / private car
            sb.AppendLine($"  PRIVATE CAR / TAXI");
            sb.AppendLine($"  - Estimated driving time: {FormatDuration(driveHours)}");
            sb.AppendLine($"  - Most flexible option; negotiate fare before departure");
            sb.AppendLine();

            // Train suggestion for medium+ distances
            if (distKm > 100)
            {
                sb.AppendLine($"  TRAIN (if available)");
                sb.AppendLine($"  - Check national rail services for this route");
                sb.AppendLine($"  - Often a comfortable option for distances over 100 km");
                sb.AppendLine();
            }

            sb.AppendLine($"  Note: Prices vary widely by country and season. Use local booking platforms for current fares.");
            return sb.ToString();
        }

        private static string FormatDuration(double hours)
        {
            var h = (int)hours;
            var m = (int)((hours - h) * 60);
            return h > 0 ? $"{h}h {m}m" : $"{m} minutes";
        }

        [Description("Gets general safety information and travel advisories for a destination.")]
        public string GetSafetyInfo([Description("The destination city or region")] string location)
        {
            return $"""
                Safety Information for {location}:

                GENERAL SAFETY
                - Check your government's travel advisory for {location} before departure
                  (e.g., travel.state.gov, gov.uk/foreign-travel-advice, smartraveller.gov.au)
                - Register with your country's embassy or consular service
                - Share your itinerary with someone at home

                HEALTH CONSIDERATIONS
                - Check if vaccinations are required or recommended
                - Verify if tap water is safe to drink; when in doubt, use bottled/filtered water
                - Locate the nearest hospital or clinic on arrival
                - Carry basic first aid supplies and any personal medications

                PERSONAL SECURITY
                - Keep copies of passport, visa, and travel insurance (digital + physical)
                - Use hotel safes for valuables; carry only what you need for the day
                - Be cautious with unsolicited offers (tours, transport, deals)
                - Agree on taxi/transport prices before departure

                MONEY & CONNECTIVITY
                - Notify your bank of travel dates to avoid card blocks
                - Carry some local currency in cash for areas without card acceptance
                - Get a local SIM card or eSIM for data access and emergency calls
                - Download offline maps for areas with limited connectivity

                EMERGENCY PREPARATION
                - Save local emergency numbers on your phone before arrival
                - Note the address and phone number of your country's nearest embassy or consulate
                - Consider travel insurance that covers medical evacuation
                """;
        }
    }
}

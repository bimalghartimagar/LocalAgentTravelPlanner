using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace LocalAgentTravelPlanner.Tools
{
    /// <summary>
    /// General travel utilities and helper tools.
    /// GetWeather uses Open-Meteo (free, no key) via Nominatim geocoding.
    /// GetFlightEstimate and GetTravelTime use OSRM for distance-based estimates.
    /// GetEmergencyContacts covers major regions; GetVisaInfo provides generic guidance.
    /// </summary>
    public class TravelTools
    {
        private readonly HttpClient _http;

        public TravelTools(HttpClient http) => _http = http;

        [Description("Gets the current weather for a specific city.")]
        public async Task<string> GetWeather([Description("The city name, e.g., Tokyo, Paris, Barcelona")] string city)
        {
            try
            {
                var coords = await GeocodingHelper.GetCoordinatesAsync(_http, city);
                if (coords is { } c)
                {
                    var lat = c.Lat.ToString("F4", CultureInfo.InvariantCulture);
                    var lon = c.Lon.ToString("F4", CultureInfo.InvariantCulture);
                    var url = $"https://api.open-meteo.com/v1/forecast" +
                              $"?latitude={lat}&longitude={lon}" +
                              $"&current=temperature_2m,weather_code,precipitation" +
                              $"&timezone=auto";

                    var json = await _http.GetStringAsync(url);
                    using var doc = JsonDocument.Parse(json);
                    var current = doc.RootElement.GetProperty("current");

                    var temp = current.GetProperty("temperature_2m").GetDouble();
                    var code = (int)current.GetProperty("weather_code").GetDouble();
                    var precip = current.GetProperty("precipitation").GetDouble();

                    var conditions = DescribeWeatherCode(code);
                    var rainNote = precip > 0 ? $", Precipitation: {precip:F1}mm" : "";

                    return $"""
                        Weather in {city} (source: Open-Meteo):
                        - Temperature: {temp:F1}°C
                        - Conditions: {conditions}{rainNote}
                        - Tip: Check local forecast for updates throughout the day
                        """;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[TravelTools] GetWeather failed for '{city}': {ex.Message}");
            }

            return FallbackGetWeather(city);
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

        private static string FallbackGetWeather(string city)
        {
            return $"""
                Weather in {city}:
                - Live weather data temporarily unavailable (Open-Meteo API unreachable).
                - Recommended: Check local forecast before departure.
                """;
        }

        [Description("Gets flight or transport cost estimates between two cities.")]
        public async Task<string> GetFlightEstimate(
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
                    {
                        var (distKm, driveHours) = route.Value;
                        var sb = new System.Text.StringBuilder();
                        sb.AppendLine($"Transport from {fromCity} to {toCity} (~{distKm:F0} km):");
                        sb.AppendLine();

                        if (distKm > 300)
                        {
                            var flightMin = (int)(distKm / 800.0 * 60) + 30;
                            sb.AppendLine($"  FLIGHT (if route exists)");
                            sb.AppendLine($"  - Estimated flight time: ~{flightMin} minutes");
                            sb.AppendLine($"  - Search Google Flights or Skyscanner for fares");
                            sb.AppendLine();
                        }

                        sb.AppendLine($"  DRIVING / PRIVATE CAR");
                        sb.AppendLine($"  - Estimated driving time: {FormatDuration(driveHours)}");
                        sb.AppendLine();

                        var busHours = driveHours * 1.3;
                        sb.AppendLine($"  BUS / COACH");
                        sb.AppendLine($"  - Estimated duration: {FormatDuration(busHours)}");
                        sb.AppendLine();

                        if (distKm > 100)
                        {
                            sb.AppendLine($"  TRAIN (if route exists)");
                            sb.AppendLine($"  - Check national rail services for availability");
                            sb.AppendLine();
                        }

                        sb.AppendLine($"  Note: Prices vary by country and season. Use local booking platforms for current fares.");
                        return sb.ToString();
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[TravelTools] GetFlightEstimate failed for '{fromCity}' -> '{toCity}': {ex.Message}");
            }

            return $"""
                Transport from {fromCity} to {toCity}:
                - Route data temporarily unavailable.
                - Check Google Maps, Rome2Rio, or local transport services for options and pricing.
                """;
        }

        [Description("Gets the travel time between two locations.")]
        public async Task<string> GetTravelTime(
            [Description("Start location")] string from,
            [Description("End location")] string to,
            [Description("Mode: walk, drive, bus")] string mode = "drive")
        {
            try
            {
                var fromCoords = await GeocodingHelper.GetCoordinatesAsync(_http, from);
                var toCoords = await GeocodingHelper.GetCoordinatesAsync(_http, to);

                if (fromCoords is { } fc && toCoords is { } tc)
                {
                    var osrmMode = mode.ToLower() switch
                    {
                        "walk" or "walking" => "foot",
                        "bike" or "bicycle" or "cycling" => "bike",
                        _ => "driving"
                    };

                    var route = await GetOsrmRouteAsync(fc.Lat, fc.Lon, tc.Lat, tc.Lon, osrmMode);
                    if (route != null)
                    {
                        var busNote = mode.ToLower() == "bus"
                            ? $" (bus estimate: {FormatDuration(route.Value.DurationHours * 1.3)} with stops)"
                            : "";

                        return $"""
                            Travel from {from} to {to} by {mode}:
                            - Distance: {route.Value.DistanceKm:F1} km
                            - Estimated time: {FormatDuration(route.Value.DurationHours)}{busNote}
                            - Tip: Allow extra time during peak hours and for unfamiliar routes
                            """;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[TravelTools] GetTravelTime failed for '{from}' -> '{to}': {ex.Message}");
            }

            return $"""
                Travel time from {from} to {to} by {mode}:
                - Route data temporarily unavailable.
                - Check Google Maps or a local maps app for estimated travel time.
                """;
        }

        [Description("Gets local emergency contacts for a destination country or city.")]
        public string GetEmergencyContacts([Description("The country or city")] string location)
        {
            var loc = location.ToLower();

            // Match against known countries/regions for emergency numbers
            var match = EmergencyNumbers.FirstOrDefault(e =>
                e.Keywords.Any(k => loc.Contains(k)));

            if (match.Country != null)
            {
                return $"""
                    Emergency Contacts for {location} ({match.Country}):

                    EMERGENCY NUMBERS
                    {match.FormattedNumbers}

                    GENERAL ADVICE
                    - Save these numbers on your phone before arrival
                    - Register with your country's embassy or consular service
                    - Locate the nearest hospital or clinic on arrival
                    - Consider travel insurance that covers medical evacuation
                    - Keep digital and physical copies of passport and insurance documents
                    """;
            }

            return $"""
                Emergency contacts for {location}:
                - Universal emergency numbers to try: 112 (international/EU), 911 (Americas), 999 (UK/Asia)
                - Look up the specific emergency number for your destination before travel
                - Register with your country's embassy or consular service
                - Download the International SOS app for worldwide emergency assistance
                - Keep digital and physical copies of important documents
                """;
        }

        private static readonly (string Country, string[] Keywords, string FormattedNumbers)[] EmergencyNumbers =
        [
            ("United States", ["united states", "usa", "new york", "los angeles", "chicago", "san francisco", "miami", "seattle", "boston", "las vegas", "hawaii", "washington"],
                "- Emergency (Police/Fire/Ambulance): 911\n    - Non-emergency police: 311 (most cities)"),
            ("Canada", ["canada", "toronto", "vancouver", "montreal", "ottawa", "calgary"],
                "- Emergency (Police/Fire/Ambulance): 911"),
            ("United Kingdom", ["united kingdom", "england", "scotland", "wales", "london", "edinburgh", "manchester", "birmingham"],
                "- Emergency (Police/Fire/Ambulance): 999 or 112\n    - Non-emergency police: 101\n    - NHS health advice: 111"),
            ("European Union", ["france", "paris", "germany", "berlin", "munich", "spain", "madrid", "barcelona", "italy", "rome", "milan", "netherlands", "amsterdam", "portugal", "lisbon", "greece", "athens", "austria", "vienna", "switzerland", "zurich", "geneva", "belgium", "brussels", "czech", "prague", "poland", "warsaw", "ireland", "dublin", "sweden", "stockholm", "norway", "oslo", "denmark", "copenhagen", "finland", "helsinki"],
                "- Emergency (all services): 112\n    - This number works across all EU/EEA countries"),
            ("Japan", ["japan", "tokyo", "osaka", "kyoto", "hiroshima", "nagoya", "sapporo", "okinawa"],
                "- Police: 110\n    - Fire/Ambulance: 119\n    - English helpline (JNTO): 050-3816-2787"),
            ("South Korea", ["south korea", "korea", "seoul", "busan", "jeju", "incheon"],
                "- Emergency: 112 (police), 119 (fire/ambulance)\n    - Tourist helpline: 1330 (English available)"),
            ("China", ["china", "beijing", "shanghai", "shenzhen", "guangzhou", "chengdu", "hong kong", "macau"],
                "- Police: 110\n    - Fire: 119\n    - Ambulance: 120\n    - Traffic: 122"),
            ("Australia", ["australia", "sydney", "melbourne", "brisbane", "perth", "adelaide"],
                "- Emergency (all services): 000\n    - From mobile: 112"),
            ("New Zealand", ["new zealand", "auckland", "wellington", "queenstown", "christchurch"],
                "- Emergency (all services): 111"),
            ("India", ["india", "delhi", "mumbai", "bangalore", "chennai", "kolkata", "jaipur", "goa", "agra", "varanasi"],
                "- Police: 100\n    - Fire: 101\n    - Ambulance: 102 or 108\n    - Women helpline: 1091\n    - Tourist helpline: 1363 or 1800-111-363"),
            ("Nepal", ["nepal", "kathmandu", "pokhara", "chitwan", "lumbini", "nagarkot"],
                "- Police: 100\n    - Tourist Police: 1144 (English speaking)\n    - Ambulance: 102\n    - Fire: 101"),
            ("Thailand", ["thailand", "bangkok", "chiang mai", "phuket", "pattaya", "krabi", "koh samui"],
                "- Emergency: 191 (police), 1669 (ambulance)\n    - Tourist police: 1155 (English available)"),
            ("Vietnam", ["vietnam", "hanoi", "ho chi minh", "da nang", "hoi an", "ha long"],
                "- Police: 113\n    - Fire: 114\n    - Ambulance: 115"),
            ("Mexico", ["mexico", "mexico city", "cancun", "playa del carmen", "guadalajara", "oaxaca", "tulum"],
                "- Emergency (all services): 911\n    - Tourist assistance: 078"),
            ("Brazil", ["brazil", "sao paulo", "rio de janeiro", "salvador", "brasilia"],
                "- Police: 190\n    - Fire: 193\n    - Ambulance (SAMU): 192"),
            ("Turkey", ["turkey", "istanbul", "ankara", "cappadocia", "antalya", "izmir"],
                "- Emergency (all services): 112\n    - Police: 155\n    - Tourist police: 153"),
            ("United Arab Emirates", ["uae", "dubai", "abu dhabi", "sharjah"],
                "- Police: 999\n    - Ambulance: 998\n    - Fire: 997"),
            ("Singapore", ["singapore"],
                "- Police: 999\n    - Ambulance/Fire: 995"),
            ("Indonesia", ["indonesia", "bali", "jakarta", "yogyakarta", "lombok"],
                "- Police: 110\n    - Ambulance: 118 or 119\n    - Fire: 113\n    - Tourist police (Bali): 0361-224111"),
            ("Egypt", ["egypt", "cairo", "luxor", "aswan", "sharm el sheikh", "hurghada"],
                "- Police: 122\n    - Ambulance: 123\n    - Fire: 180\n    - Tourist police: 126"),
            ("South Africa", ["south africa", "cape town", "johannesburg", "durban", "kruger"],
                "- Emergency (all services): 10111 (police), 10177 (ambulance)\n    - From mobile: 112"),
            ("Morocco", ["morocco", "marrakech", "fez", "casablanca", "chefchaouen", "rabat"],
                "- Police: 19\n    - Fire/Ambulance: 15\n    - Gendarmerie (rural): 177"),
            ("Peru", ["peru", "lima", "cusco", "machu picchu", "arequipa"],
                "- Police: 105\n    - Fire: 116\n    - Ambulance (SAMU): 106\n    - Tourist police: 0800-22221"),
            ("Colombia", ["colombia", "bogota", "medellin", "cartagena", "cali"],
                "- Emergency (all services): 123\n    - Police: 112"),
        ];

        [Description("Gets visa and entry requirements for a destination country.")]
        public string GetVisaInfo(
            [Description("Traveler's nationality")] string nationality,
            [Description("Destination country")] string destination = "")
        {
            var dest = string.IsNullOrWhiteSpace(destination) ? "your destination" : destination;
            return $"""
                Visa Information for {nationality} Citizens Traveling to {dest}:

                GENERAL GUIDANCE
                - Visa requirements depend on BOTH your nationality AND your destination
                - Requirements change frequently — always verify before booking travel

                COMMON ENTRY TYPES
                - Visa-free / visa waiver: Many country pairs allow short stays (30–90 days) without a visa
                - Visa on arrival: Available at airports/borders in many countries; fee varies ($20–$100+)
                - eVisa: Online application processed before travel (growing trend worldwide)
                - Embassy visa: Traditional application at a consulate; allow 2–8 weeks processing

                TYPICAL REQUIREMENTS
                - Valid passport with 6+ months validity beyond your travel dates
                - Passport-size photos (check specific size requirements by country)
                - Proof of onward/return travel
                - Proof of accommodation or invitation letter
                - Travel insurance (required by some countries, e.g., Schengen area)
                - Sufficient funds for duration of stay

                WHERE TO CHECK
                - Your government's travel advisory site for {dest}
                - {dest} immigration or foreign affairs website
                - IATA Travel Centre (iatatravelcentre.com) for airline-verified requirements
                - Sherpa or VisaHQ for visa application services

                TIP: Check entry requirements at least 4 weeks before departure. Some visas require
                in-person interviews or mailed documents and cannot be obtained last-minute.
                """;
        }

        private async Task<(double DistanceKm, double DurationHours)?> GetOsrmRouteAsync(
            double lat1, double lon1, double lat2, double lon2, string profile = "driving")
        {
            var url = $"https://router.project-osrm.org/route/v1/{profile}/" +
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

        private static string FormatDuration(double hours)
        {
            var h = (int)hours;
            var m = (int)((hours - h) * 60);
            return h > 0 ? $"{h}h {m}m" : $"{m} minutes";
        }
    }
}

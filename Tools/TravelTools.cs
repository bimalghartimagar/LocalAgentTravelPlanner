using System.ComponentModel;
using System.Globalization;
using System.Text.Json;

namespace LocalAgentTravelPlanner.Tools
{
    /// <summary>
    /// General travel utilities and helper tools.
    /// GetWeather uses Open-Meteo (free, no key) via Nominatim geocoding.
    /// Other methods remain hardcoded (transport estimates, emergency contacts, visa info).
    /// </summary>
    public class TravelTools
    {
        private readonly HttpClient _http;

        public TravelTools(HttpClient http) => _http = http;

        [Description("Gets the current weather for a specific city.")]
        public async Task<string> GetWeather([Description("The city name, e.g., Pokhara")] string city)
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
            catch { }

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
            var cityLower = city.ToLower();

            var weathers = new Dictionary<string, (string temp, string conditions, string tip)>
            {
                ["pokhara"] = ("24°C", "Partly cloudy with clear views of Machapuchare", "Light layers recommended"),
                ["kathmandu"] = ("18°C", "Hazy with light smog", "Consider face mask for sensitive individuals"),
                ["butwal"] = ("28°C", "Humid and sunny", "Stay hydrated, seek shade midday"),
                ["chitwan"] = ("30°C", "Hot and humid, good for jungle safari", "Light cotton clothes, insect repellent"),
                ["lumbini"] = ("29°C", "Warm and clear", "Carry water, hat recommended"),
                ["nagarkot"] = ("15°C", "Cool and clear, excellent mountain views", "Warm jacket needed, especially mornings")
            };

            if (weathers.TryGetValue(cityLower, out var weather))
            {
                return $"""
                    Weather in {city}:
                    - Temperature: {weather.temp}
                    - Conditions: {weather.conditions}
                    - Tip: {weather.tip}
                    """;
            }

            return $"Weather in {city}: 22°C, conditions variable. Check local forecast for updates.";
        }

        [Description("Gets flight or transport cost estimates between two cities.")]
        public string GetFlightEstimate(
            [Description("Origin city")] string fromCity,
            [Description("Destination city")] string toCity)
        {
            var from = fromCity.ToLower();
            var to = toCity.ToLower();

            // Nepal domestic routes
            if (from.Contains("kathmandu") && to.Contains("pokhara"))
            {
                return """
                    Kathmandu to Pokhara Transport Options:

                    ✈️ FLIGHT (Fastest, Scenic)
                    - Price: $80-120 USD (NPR 10,700-16,000)
                    - Duration: 25 minutes
                    - Airlines: Buddha Air, Yeti Airlines
                    - Highlight: Stunning Himalayan views of Annapurna range
                    - Booking: Book 2-3 days ahead in peak season

                    🚌 TOURIST BUS (Popular)
                    - Price: NPR 800-1,200 ($6-9 USD)
                    - Duration: 6-7 hours
                    - Comfort: AC, reclining seats, rest stops
                    - Route: Prithvi Highway (scenic river valleys)

                    🚗 PRIVATE CAR (Flexible)
                    - Price: NPR 8,000-12,000 ($60-90 USD)
                    - Duration: 5-6 hours
                    - Advantage: Stop for photos, flexible schedule
                    """;
            }

            if (from.Contains("butwal") && to.Contains("pokhara"))
            {
                return """
                    Butwal to Pokhara Transport:

                    🚌 TOURIST BUS
                    - Price: NPR 600-800 ($4.5-6 USD)
                    - Duration: 4 hours

                    🚗 PRIVATE TAXI
                    - Price: NPR 5,000-7,000 ($37-52 USD)
                    - Duration: 3 hours

                    🚐 MICRO BUS
                    - Price: NPR 500 ($3.75 USD)
                    - Duration: 3.5 hours
                    """;
            }

            return $"""
                Transport from {fromCity} to {toCity}:
                - Bus (estimated): NPR 500-1,500 ($4-12 USD)
                - Private Car (estimated): NPR 5,000-15,000 ($37-112 USD)
                - Check local bus park for exact schedules and current prices.
                """;
        }

        [Description("Gets the travel time between two locations.")]
        public string GetTravelTime(
            [Description("Start location")] string from,
            [Description("End location")] string to,
            [Description("Mode: walk, drive, bus")] string mode = "drive")
        {
            var modeLower = mode.ToLower();

            // Within Pokhara estimates
            if (from.ToLower().Contains("lakeside") || to.ToLower().Contains("lakeside"))
            {
                var times = modeLower switch
                {
                    "walk" => "15-45 minutes depending on destination",
                    "drive" or "taxi" => "10-20 minutes to most attractions",
                    "bus" => "20-40 minutes with stops",
                    _ => "10-30 minutes"
                };

                return $"Travel from {from} to {to} by {mode}: {times}";
            }

            // Generic estimate
            return $"""
                Travel time from {from} to {to} by {mode}:
                - Estimated: 30-60 minutes (varies by traffic and distance)
                - Tip: Allow extra time during peak hours (8-10 AM, 5-7 PM)
                """;
        }

        [Description("Gets local emergency contacts for a destination.")]
        public string GetEmergencyContacts([Description("The country or city")] string location)
        {
            var locationLower = location.ToLower();

            if (locationLower.Contains("nepal") ||
                locationLower.Contains("pokhara") ||
                locationLower.Contains("kathmandu") ||
                locationLower.Contains("butwal"))
            {
                return """
                    Nepal Emergency Contacts:

                    🚨 EMERGENCY NUMBERS
                    - Police: 100
                    - Tourist Police: 1144 (English speaking, 24/7)
                    - Ambulance: 102
                    - Fire: 101

                    🏥 MEDICAL (Pokhara)
                    - Western Regional Hospital: 061-520066
                    - Manipal Teaching Hospital: 061-526416
                    - Grande International Hospital: 061-538900

                    🏥 MEDICAL (Kathmandu)
                    - CIWEC Clinic (expat/tourist): 01-4424111
                    - Norvic Hospital: 01-4258554
                    - Grande Hospital: 01-5159266

                    🏛️ EMBASSIES (Kathmandu)
                    - US Embassy: 01-4234000
                    - UK Embassy: 01-4237100
                    - India Embassy: 01-4410900
                    - China Embassy: 01-4434792

                    📱 USEFUL APPS
                    - Nepal Police App (safety alerts)
                    - Tootle/Pathao (ride sharing)
                    - Khalti/eSewa (mobile payments)

                    💡 TIP: Save these numbers before traveling!
                    """;
            }

            return $"""
                Emergency contacts for {location}:
                - Research local emergency numbers before travel
                - Register with your country's embassy
                - Keep digital and physical copies of important documents
                - Note: International SOS app recommended for travelers
                """;
        }

        [Description("Gets visa and entry requirements for Nepal.")]
        public string GetVisaInfo([Description("Traveler's nationality")] string nationality)
        {
            return $"""
                Nepal Visa Information for {nationality} Citizens:

                📋 VISA ON ARRIVAL (Most nationalities)
                - Available at: Tribhuvan Airport, land borders
                - Processing: 15-30 minutes

                💰 VISA FEES
                - 15 days: $30 USD
                - 30 days: $50 USD
                - 90 days: $125 USD

                📄 REQUIREMENTS
                - Valid passport (6+ months validity)
                - Passport-size photo
                - Completed arrival form
                - Cash (USD preferred for visa fee)

                ⚠️ EXCEPTIONS
                - India, China (different rules apply)
                - Some nationalities require pre-approval

                💡 TIP: Carry exact USD amount for faster processing

                Note: Verify current requirements at nepalimmigration.gov.np
                """;
        }
    }
}

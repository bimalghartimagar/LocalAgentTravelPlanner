using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace LocalAgentTravelPlanner.Tools
{
    internal static class GeocodingHelper
    {
        internal static async Task<(double Lat, double Lon)?> GetCoordinatesAsync(
            HttpClient client, string city)
        {
            try
            {
                if (!client.DefaultRequestHeaders.Contains("User-Agent"))
                    client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "LocalTravelPlanner/1.0");

                var url = $"https://nominatim.openstreetmap.org/search?q={Uri.EscapeDataString(city)}&format=json&limit=1";
                var json = await client.GetStringAsync(url);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
                {
                    var first = root[0];
                    var lat = double.Parse(first.GetProperty("lat").GetString()!, CultureInfo.InvariantCulture);
                    var lon = double.Parse(first.GetProperty("lon").GetString()!, CultureInfo.InvariantCulture);
                    return (lat, lon);
                }

                Debug.WriteLine($"[GeocodingHelper] No results for city: {city}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GeocodingHelper] Failed to geocode '{city}': {ex.Message}");
            }
            return null;
        }
    }
}

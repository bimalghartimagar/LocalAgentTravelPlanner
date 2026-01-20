using System.ComponentModel;

namespace LocalAgentTravelPlanner.Tools
{
    /// <summary>
    /// Research-specific tools for gathering destination information.
    /// </summary>
    public class ResearchTools
    {
        [Description("Gets current weather and forecast for a destination.")]
        public string GetWeatherForecast(
            [Description("The city name")] string city,
            [Description("Number of days to forecast")] int days = 3)
        {
            // Simulated - in production, use OpenWeatherMap API
            var cityLower = city.ToLower();

            if (cityLower.Contains("pokhara"))
            {
                return $"""
                    Weather for Pokhara ({days}-day forecast):
                    - Today: 24°C, Partly cloudy, clear views of Annapurna
                    - Tomorrow: 22°C, Sunny, excellent visibility
                    - Day 3: 20°C, Light rain expected in afternoon
                    - Humidity: 65-75%
                    - Best time for Sarangkot sunrise: 5:30 AM
                    - Recommended: Light layers, bring rain jacket for Day 3
                    """;
            }

            if (cityLower.Contains("kathmandu"))
            {
                return $"""
                    Weather for Kathmandu ({days}-day forecast):
                    - Today: 18°C, Hazy with light smog
                    - Tomorrow: 20°C, Partly cloudy
                    - Day 3: 19°C, Clear skies expected
                    - Air Quality: Moderate (wear mask if sensitive)
                    - Recommended: Layers for morning chill, light jacket
                    """;
            }

            return $"""
                Weather for {city} ({days}-day forecast):
                - Today: 22°C, Partly cloudy
                - Tomorrow: 24°C, Sunny
                - Day 3: 21°C, Chance of rain
                - Recommended: Check local forecast for updates
                """;
        }

        [Description("Finds hotels in a destination within a budget range.")]
        public string SearchHotels(
            [Description("The destination city")] string city,
            [Description("Budget category: Budget, Mid-range, or Luxury")] string category)
        {
            var cityLower = city.ToLower();
            var categoryLower = category.ToLower();

            if (cityLower.Contains("pokhara"))
            {
                return categoryLower switch
                {
                    "budget" => """
                        Hotels in Pokhara (Budget):
                        1. Lakeside Hostel - NPR 800/night - Dorms available, breakfast included, lake view terrace
                        2. Traveler's Inn - NPR 1,500/night - Private room, fan, hot shower, rooftop cafe
                        3. Mountain View Guesthouse - NPR 1,200/night - Garden view, quiet location, family run
                        4. Pokhara Backpackers - NPR 600/night - Dorm beds, common kitchen, social atmosphere
                        All verified and operating as of 2024.
                        """,
                    "mid-range" or "midrange" => """
                        Hotels in Pokhara (Mid-range):
                        1. Hotel Barahi - NPR 4,500/night - 3-star, pool, lake view, restaurant
                        2. Temple Tree Resort & Spa - NPR 5,500/night - Spa, garden, excellent breakfast
                        3. Lakefront Resort - NPR 3,800/night - Direct lake access, kayaks available
                        4. Atithi Resort & Spa - NPR 4,000/night - Pool, spa, mountain views
                        All verified and operating as of 2024.
                        """,
                    "luxury" => """
                        Hotels in Pokhara (Luxury):
                        1. Fish Tail Lodge - NPR 15,000/night - 5-star, iconic location, boat access only
                        2. Tiger Mountain Pokhara Lodge - NPR 25,000/night - Boutique eco-lodge, stunning views
                        3. Pavilions Himalayas - NPR 35,000/night - Eco-luxury, private villas, farm-to-table
                        4. Waterfront Resort - NPR 12,000/night - Premium lakeside, infinity pool
                        All verified and operating as of 2024.
                        """,
                    _ => $"Unknown category '{category}'. Use: Budget, Mid-range, or Luxury"
                };
            }

            // Generic hotels for other cities
            return $"""
                Hotels in {city} ({category}):
                Search returned limited results. Consider using major hotel booking platforms
                for the most up-to-date availability and pricing.
                Estimated prices: Budget NPR 1000-2000, Mid-range NPR 3000-6000, Luxury NPR 10000+
                """;
        }

        [Description("Finds top attractions and things to do at a destination.")]
        public string GetAttractions([Description("The destination city")] string city)
        {
            var cityLower = city.ToLower();

            if (cityLower.Contains("pokhara"))
            {
                return """
                    Top Attractions in Pokhara (Verified):

                    🌊 LAKES & NATURE
                    1. Phewa Lake - Free entry, boat ride NPR 500-800/hr
                       - Best time: Early morning or sunset
                       - Duration: 1-2 hours
                       - Highlight: Tal Barahi Temple on island

                    2. Begnas Lake - NPR 300 boat ride, less crowded
                       - Best time: Any time, peaceful
                       - Duration: 2-3 hours
                       - Highlight: Authentic local experience

                    ⛰️ VIEWPOINTS
                    3. Sarangkot - NPR 100 entry
                       - Best time: Sunrise (5:30 AM)
                       - Duration: 2-3 hours with hike
                       - Highlight: Panoramic Annapurna views

                    4. World Peace Pagoda - Free entry
                       - Best time: Morning or late afternoon
                       - Duration: 3-4 hours (including hike)
                       - Highlight: Buddhist stupa, lake views

                    🏛️ CULTURAL SITES
                    5. International Mountain Museum - NPR 400 entry
                       - Best time: Midday (indoor)
                       - Duration: 2-3 hours
                       - Highlight: Everest expedition history

                    6. Gupteshwor Cave - NPR 100 entry
                       - Best time: Any time (underground)
                       - Duration: 1 hour
                       - Highlight: Sacred Shiva shrine

                    💦 WATERFALLS
                    7. Davis Falls (Patale Chhango) - NPR 50 entry
                       - Best time: After monsoon (fuller water)
                       - Duration: 30-45 minutes
                       - Highlight: Unique underground waterfall

                    🪂 ADVENTURE
                    8. Paragliding - NPR 8,000-12,000
                       - Best time: 9 AM - 2 PM
                       - Duration: 20-30 minutes flight
                       - Highlight: Tandem flight over lake

                    9. Zip Flyer - NPR 4,000-5,000
                       - Best time: Morning
                       - Duration: 2-3 hours total
                       - Highlight: World's most extreme zip line

                    All attractions verified and open as of 2024.
                    """;
            }

            return $"""
                Attractions in {city}:
                Query the local tourism board for comprehensive listings.
                Common attractions include: temples, viewpoints, local markets, and nature spots.
                """;
        }

        [Description("Gets transportation options between two cities in Nepal.")]
        public string GetTransportOptions(
            [Description("Origin city")] string fromCity,
            [Description("Destination city")] string toCity)
        {
            var from = fromCity.ToLower();
            var to = toCity.ToLower();

            if (from.Contains("butwal") && to.Contains("pokhara"))
            {
                return """
                    Transport from Butwal to Pokhara (Verified Routes):

                    🚌 TOURIST BUS (Recommended for comfort)
                    - Price: NPR 600-800
                    - Duration: 4 hours
                    - Frequency: Multiple departures 6 AM - 2 PM
                    - Comfort: AC, reclining seats, rest stops
                    - Booking: Book day before at bus park

                    🚐 MICRO BUS (Faster, less comfort)
                    - Price: NPR 500
                    - Duration: 3.5 hours
                    - Frequency: Every 30 minutes
                    - Comfort: Cramped but quick
                    - Note: Leaves when full

                    🚌 LOCAL BUS (Budget option)
                    - Price: NPR 350
                    - Duration: 4.5 hours
                    - Frequency: Every 15 minutes
                    - Comfort: Basic, crowded
                    - Note: Multiple stops

                    🚗 PRIVATE TAXI (Door-to-door)
                    - Price: NPR 5,000-7,000 (whole car)
                    - Duration: 3 hours
                    - Flexibility: Your schedule
                    - Comfort: Air-conditioned, private
                    - Note: Negotiate price beforehand

                    🚙 SHARED JEEP
                    - Price: NPR 700
                    - Duration: 3.5 hours
                    - Frequency: Morning departures
                    - Comfort: Moderate
                    - Note: Usually 6-8 passengers

                    📍 Route: Via Siddhartha Highway (scenic mountain views)
                    ⚠️ Note: Morning departures recommended for safety
                    """;
            }

            if (from.Contains("kathmandu") && to.Contains("pokhara"))
            {
                return """
                    Transport from Kathmandu to Pokhara:

                    ✈️ FLIGHT (Fastest)
                    - Price: $80-120 USD
                    - Duration: 25 minutes
                    - Airlines: Buddha Air, Yeti Airlines
                    - Highlight: Stunning Himalayan views

                    🚌 TOURIST BUS
                    - Price: NPR 800-1,200
                    - Duration: 6-7 hours
                    - Comfort: AC, comfortable seats
                    - Route: Prithvi Highway

                    🚗 PRIVATE CAR
                    - Price: NPR 8,000-12,000
                    - Duration: 5-6 hours
                    - Flexibility: Stops for photos
                    """;
            }

            return $"""
                Transport from {fromCity} to {toCity}:
                - Estimated bus fare: NPR 500-1,500
                - Estimated private car: NPR 5,000-10,000
                - Check local bus park for exact schedules
                """;
        }

        [Description("Gets local food recommendations and average meal costs.")]
        public string GetFoodRecommendations([Description("The destination city")] string city)
        {
            var cityLower = city.ToLower();

            if (cityLower.Contains("pokhara"))
            {
                return """
                    Food in Pokhara (Lakeside Area):

                    🍜 STREET FOOD (NPR 50-150/item)
                    - Momos (dumplings) - NPR 80-120
                    - Chowmein - NPR 100-150
                    - Sel Roti (sweet bread) - NPR 30-50
                    - Best spots: Street vendors near Barahi Chowk

                    🍛 LOCAL RESTAURANTS (NPR 200-400/meal)
                    - Dal Bhat (unlimited refills) - NPR 250-350
                    - Thakali Set - NPR 300-400
                    - Newari Khaja Set - NPR 350-450
                    - Recommended: Local eateries on Baidam Road

                    ☕ CAFES (NPR 300-600/meal)
                    - Busy Bee Cafe - Excellent coffee, NPR 200-400
                    - Moondance Restaurant - Lake view, NPR 400-800
                    - OR2K - Mediterranean, vegetarian friendly, NPR 500-900
                    - Caffe Concerto - Italian, NPR 600-1000

                    🍽️ FINE DINING (NPR 1,000-3,000/meal)
                    - The Harbour Restaurant - Lakeside, NPR 1,500-2,500
                    - Nepali Kitchen - Traditional cuisine, NPR 1,000-2,000
                    - Olive Cafe - Continental, NPR 1,200-2,000

                    📊 DAILY FOOD BUDGET ESTIMATES
                    - Frugal: NPR 500-800/day (street food + local)
                    - Medium: NPR 1,200-2,000/day (mix of all)
                    - High-end: NPR 3,000-5,000/day (cafes + fine dining)

                    💡 TIP: Dal Bhat is the best value - unlimited rice and lentils!
                    """;
            }

            return $"""
                Food in {city}:
                - Street Food: NPR 50-150/meal
                - Local Restaurant: NPR 200-400/meal
                - Mid-range: NPR 500-1,000/meal
                - Fine Dining: NPR 1,500-3,000/meal
                """;
        }

        [Description("Gets safety information and travel advisories for a destination.")]
        public string GetSafetyInfo([Description("The destination city or region")] string location)
        {
            var locationLower = location.ToLower();

            if (locationLower.Contains("pokhara") || locationLower.Contains("nepal"))
            {
                return """
                    Safety Information for Pokhara, Nepal:

                    ✅ GENERAL SAFETY
                    - Overall: Very safe for tourists, welcoming locals
                    - Crime: Low crime rate, petty theft rare but possible
                    - Solo travel: Safe, including for women

                    🏥 HEALTH CONSIDERATIONS
                    - Water: Drink only bottled/filtered water
                    - Altitude: Pokhara is 820m (no altitude sickness risk)
                    - Food: Eat at busy restaurants, avoid raw vegetables from street stalls
                    - Hospitals: Western Regional Hospital, several private clinics

                    ⚠️ COMMON SCAMS
                    - Inflated taxi prices: Negotiate before getting in
                    - Tiger balm sellers: Politely decline
                    - "Free" tours: Usually lead to shops
                    - TIP: Always agree on prices beforehand

                    📞 EMERGENCY CONTACTS
                    - Police: 100
                    - Tourist Police: 1144 (English speaking)
                    - Ambulance: 102
                    - Fire: 101

                    🚫 RESTRICTED AREAS (Require Permits)
                    - Upper Mustang: Special permit required
                    - Manaslu Region: Restricted, permit $70/week
                    - Dolpa: ACAP and special permits needed
                    - NOTE: Pokhara and surrounding areas = NO permit needed

                    🏔️ TREKKING SAFETY
                    - Register with TIMS for multi-day treks
                    - Hire licensed guides for high-altitude treks
                    - Carry first aid kit and emergency supplies

                    💡 GENERAL TIPS
                    - ATMs widely available in Lakeside
                    - Mobile data: Ncell or NTC SIM cards easily available
                    - Dress modestly when visiting temples
                    - Remove shoes before entering homes/temples
                    """;
            }

            return $"""
                Safety for {location}:
                - Research local emergency numbers
                - Register with your embassy
                - Check travel advisories from your home country
                - Carry copies of important documents
                """;
        }
    }
}

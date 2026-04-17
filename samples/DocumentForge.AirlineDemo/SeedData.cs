namespace DocumentForge.AirlineDemo;

public static class SeedData
{
    private static readonly string[] FirstNames = { "James", "Mary", "John", "Patricia", "Robert", "Jennifer", "Michael", "Linda", "William", "Elizabeth", "David", "Barbara", "Richard", "Susan", "Joseph", "Jessica", "Thomas", "Sarah", "Charles", "Karen", "Christopher", "Lisa", "Daniel", "Nancy", "Matthew", "Betty", "Anthony", "Margaret", "Mark", "Sandra", "Donald", "Ashley", "Steven", "Kimberly", "Paul", "Emily", "Andrew", "Donna", "Joshua", "Michelle" };
    private static readonly string[] LastNames = { "Smith", "Johnson", "Williams", "Brown", "Jones", "Garcia", "Miller", "Davis", "Rodriguez", "Martinez", "Hernandez", "Lopez", "Gonzalez", "Wilson", "Anderson", "Thomas", "Taylor", "Moore", "Jackson", "Martin", "Lee", "Perez", "Thompson", "White", "Harris", "Sanchez", "Clark", "Ramirez", "Lewis", "Robinson" };
    private static readonly string[] Airlines = { "AA", "UA", "DL", "BA", "LH", "AF", "EK", "SQ", "QF", "NH" };
    private static readonly string[] AirlineNames = { "American Airlines", "United Airlines", "Delta Air Lines", "British Airways", "Lufthansa", "Air France", "Emirates", "Singapore Airlines", "Qantas", "ANA" };
    private static readonly string[] Airports = { "JFK", "LAX", "ORD", "LHR", "CDG", "FRA", "DXB", "SIN", "SYD", "NRT", "ATL", "DFW", "DEN", "SFO", "SEA", "MIA", "BOS", "IAD", "EWR", "PHX" };
    private static readonly string[] Aircraft = { "B738", "B77W", "A320", "A388", "B789", "A321", "B737", "A350", "B772", "E190" };
    private static readonly string[] Cabins = { "ECONOMY", "PREMIUM_ECONOMY", "BUSINESS", "FIRST" };
    private static readonly string[] Statuses = { "CONFIRMED", "CONFIRMED", "CONFIRMED", "CONFIRMED", "TICKETED", "TICKETED", "CANCELLED", "PENDING" };
    private static readonly string[] MealCodes = { "VGML", "VLML", "AVML", "HNML", "MOML", "KSML", "BBML" };

    public static string GenerateOrder(Random rng, int index)
    {
        var pnr = GeneratePnr(rng);
        var firstName = FirstNames[rng.Next(FirstNames.Length)];
        var lastName = LastNames[rng.Next(LastNames.Length)];
        var airlineIdx = rng.Next(Airlines.Length);
        var airline = Airlines[airlineIdx];
        var airlineName = AirlineNames[airlineIdx];
        var status = Statuses[rng.Next(Statuses.Length)];

        var departureIdx = rng.Next(Airports.Length);
        int arrivalIdx;
        do { arrivalIdx = rng.Next(Airports.Length); } while (arrivalIdx == departureIdx);

        var departureAirport = Airports[departureIdx];
        var arrivalAirport = Airports[arrivalIdx];
        var aircraft = Aircraft[rng.Next(Aircraft.Length)];
        var cabin = Cabins[rng.Next(Cabins.Length)];
        var flightNum = rng.Next(100, 9999);
        var baseFare = Math.Round(150 + rng.NextDouble() * 2000, 2);
        var taxes = Math.Round(baseFare * 0.15, 2);
        var total = baseFare + taxes;

        var departDate = DateTime.Today.AddDays(rng.Next(1, 180));
        var departHour = rng.Next(5, 22);
        var departTime = new DateTime(departDate.Year, departDate.Month, departDate.Day, departHour, rng.Next(0, 12) * 5, 0);
        var flightDuration = TimeSpan.FromHours(1 + rng.NextDouble() * 14);
        var arriveTime = departTime.Add(flightDuration);

        var hasReturn = rng.NextDouble() > 0.3;
        var returnFlightJson = "";
        if (hasReturn)
        {
            var returnDate = departDate.AddDays(rng.Next(1, 14));
            var returnDepartTime = new DateTime(returnDate.Year, returnDate.Month, returnDate.Day, rng.Next(5, 22), rng.Next(0, 12) * 5, 0);
            var returnArriveTime = returnDepartTime.Add(flightDuration);
            var returnFlightNum = rng.Next(100, 9999);

            returnFlightJson = $@",
        {{
            ""segmentId"": 2,
            ""flightNumber"": ""{airline}{returnFlightNum}"",
            ""airline"": ""{airlineName}"",
            ""departureAirport"": ""{arrivalAirport}"",
            ""arrivalAirport"": ""{departureAirport}"",
            ""departureTime"": ""{returnDepartTime:yyyy-MM-ddTHH:mm:ssZ}"",
            ""arrivalTime"": ""{returnArriveTime:yyyy-MM-ddTHH:mm:ssZ}"",
            ""cabin"": ""{cabin}"",
            ""status"": ""{status}"",
            ""aircraft"": ""{Aircraft[rng.Next(Aircraft.Length)]}""
        }}";
        }

        var mealCode = MealCodes[rng.Next(MealCodes.Length)];
        var createdAt = DateTime.UtcNow.AddDays(-rng.Next(0, 90));

        return $$"""
{
    "pnr": "{{pnr}}",
    "status": "{{status}}",
    "createdAt": "{{createdAt:yyyy-MM-ddTHH:mm:ssZ}}",
    "passenger": {
        "firstName": "{{firstName}}",
        "lastName": "{{lastName}}",
        "email": "{{firstName.ToLower()}}.{{lastName.ToLower()}}@email.com",
        "phone": "+1-555-{{rng.Next(1000, 9999):D4}}",
        "dateOfBirth": "{{1950 + rng.Next(55)}}-{{rng.Next(1, 13):D2}}-{{rng.Next(1, 29):D2}}"
    },
    "flights": [
        {
            "segmentId": 1,
            "flightNumber": "{{airline}}{{flightNum}}",
            "airline": "{{airlineName}}",
            "departureAirport": "{{departureAirport}}",
            "arrivalAirport": "{{arrivalAirport}}",
            "departureTime": "{{departTime:yyyy-MM-ddTHH:mm:ssZ}}",
            "arrivalTime": "{{arriveTime:yyyy-MM-ddTHH:mm:ssZ}}",
            "cabin": "{{cabin}}",
            "status": "{{status}}",
            "aircraft": "{{aircraft}}"
        }{{returnFlightJson}}
    ],
    "tickets": [
        {
            "ticketNumber": "{{rng.Next(100, 999)}}-{{rng.Next(1000000000, int.MaxValue)}}",
            "passengerName": "{{lastName.ToUpper()}}/{{firstName.ToUpper()}}",
            "fare": {
                "baseFare": {{baseFare}},
                "taxes": {{taxes}},
                "total": {{total}},
                "currency": "USD"
            }
        }
    ],
    "payment": {
        "method": "CREDIT_CARD",
        "last4": "{{rng.Next(1000, 9999)}}",
        "amount": {{total}},
        "currency": "USD",
        "status": "CHARGED"
    },
    "remarks": [
        { "type": "SSR", "code": "MEAL", "text": "{{mealCode}}", "segment": 1 }
    ]
}
""";
    }

    public static string GenerateFlight(Random rng, string flightNumber, string date)
    {
        var airlineIdx = Airlines.ToList().FindIndex(a => flightNumber.StartsWith(a));
        if (airlineIdx < 0) airlineIdx = 0;
        var airlineName = AirlineNames[airlineIdx];

        var departureIdx = rng.Next(Airports.Length);
        int arrivalIdx;
        do { arrivalIdx = rng.Next(Airports.Length); } while (arrivalIdx == departureIdx);

        var departHour = rng.Next(5, 22);
        var departTime = $"{date}T{departHour:D2}:{rng.Next(0, 12) * 5:D2}:00Z";
        var arriveHour = departHour + rng.Next(1, 10);
        var arriveTime = $"{date}T{Math.Min(arriveHour, 23):D2}:{rng.Next(0, 12) * 5:D2}:00Z";

        var firstTotal = rng.Next(8, 20);
        var firstSold = rng.Next(0, firstTotal + 1);
        var bizTotal = rng.Next(20, 40);
        var bizSold = rng.Next(0, bizTotal + 1);
        var econTotal = rng.Next(100, 200);
        var econSold = rng.Next(0, econTotal + 1);

        return $$"""
{
    "flightNumber": "{{flightNumber}}",
    "operatingDate": "{{date}}",
    "departureAirport": "{{Airports[departureIdx]}}",
    "arrivalAirport": "{{Airports[arrivalIdx]}}",
    "scheduledDeparture": "{{departTime}}",
    "scheduledArrival": "{{arriveTime}}",
    "aircraft": {
        "type": "{{Aircraft[rng.Next(Aircraft.Length)]}}",
        "registration": "N{{rng.Next(10000, 99999)}}"
    },
    "capacity": {
        "FIRST": { "total": {{firstTotal}}, "sold": {{firstSold}}, "available": {{firstTotal - firstSold}} },
        "BUSINESS": { "total": {{bizTotal}}, "sold": {{bizSold}}, "available": {{bizTotal - bizSold}} },
        "ECONOMY": { "total": {{econTotal}}, "sold": {{econSold}}, "available": {{econTotal - econSold}} }
    },
    "status": "SCHEDULED"
}
""";
    }

    // Counter-based unique PNR: increments across calls AND across process runs
    // (seeded from timestamp so different runs get different ranges)
    private static long _pnrCounter = DateTimeOffset.UtcNow.ToUnixTimeSeconds() % 1_000_000;

    private static string GeneratePnr(Random rng)
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        const int n = 36;
        long val = System.Threading.Interlocked.Increment(ref _pnrCounter);
        // Encode as 8-char base36 for guaranteed uniqueness
        var buf = new char[8];
        for (int i = 7; i >= 0; i--)
        {
            buf[i] = chars[(int)(val % n)];
            val /= n;
        }
        return new string(buf);
    }
}

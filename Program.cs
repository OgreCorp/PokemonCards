using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;

// Get Pokemon cards from API
async Task<string> GetCardsJson(int limit)
{
    string currentMethodName = nameof(GetCardsJson);
    Console.WriteLine($"Begin [{currentMethodName}()]");
    Console.WriteLine($"Limit [{limit}]");

    var endpointUri = @"https://api.pokemontcg.io/v2/";
    var endpointQueryTemplate = 
        @"cards?q={types} {hp} {rarity}"
        + @"&page=1"
        + @"&pageSize={limit}"
        + @"&select=id,name,types,hp,rarity"
        + @"&orderBy=id";

    var endpointQuery = endpointQueryTemplate
        .Replace("{types}", "(types:fire OR types:grass)")
        .Replace("{hp}", "hp:[90 TO *]")
        .Replace("{rarity}", "rarity:rare")
        .Replace("{limit}", limit.ToString());

    string content;
    using (var client = new HttpClient())
    {
        client.BaseAddress = new Uri(endpointUri);
        client.DefaultRequestHeaders.Add("User-Agent", "MyCoolPokemonCardsClient");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var requestCount = 1;
        const int maxRetries = 6;
        const int retryDelayMultiplier = 10;
        HttpResponseMessage response;
        // Splunk/New Relic/whatever instrumentation framework should look for output around here
        // Alert on errors
        // Make cool graphs based on response time
        var stopwatch = new Stopwatch();
        stopwatch.Start();
        while (!(response = await client.GetAsync(endpointQuery)).IsSuccessStatusCode)
        {
            var errorMessage = $"Error! Status code [{response.StatusCode}] Reason [{response.ReasonPhrase}]";
            Console.WriteLine(errorMessage);
            // Some back-off retry logic for "Too Many Requests"
            // Otherwise fast fail
            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                // We waited a few times, even tried the 60 second API limit but still got throttled
                // Fail fast and trigger an investigation
                if (requestCount >= maxRetries)
                {
                    throw new Exception($"429 detected at least [{maxRetries}] times, fast failing");
                }
                var sleepSeconds = requestCount * retryDelayMultiplier;
                var sleepMilliseconds = sleepSeconds * 1000;
                Console.WriteLine($"429 detected, sleeping [{sleepSeconds}] requestCount [{requestCount}]");
                await Task.Delay(sleepMilliseconds);
            }
            else
            {
                throw new Exception(errorMessage);
            }
            requestCount++;
        }
        stopwatch.Stop();
        var elapsed = stopwatch.Elapsed;
        var elapsedString = string.Format("{0:00}:{1:00}:{2:00}.{3:00}",
            elapsed.Hours, elapsed.Minutes, elapsed.Seconds,
            elapsed.Milliseconds / 10);
        Console.WriteLine($"Result obtained in [{elapsedString}] via [{requestCount}] tries");
        content = await response.Content.ReadAsStringAsync();
        if (string.IsNullOrEmpty(content))
        {
            // We got an empty string from the API
            // Fail fast and trigger investigation
            throw new Exception("Error! Empty string returned from API! We should have at least got some JSON with an empty Data payload.");
        }
    }
    var contentLength = content.Length;
    Console.WriteLine($"Content length [{contentLength}]");
    Console.WriteLine($"End [{currentMethodName}()]");
    return content;
}

// Convert from input objects to output objects
// Note that we have a mismatch between the supplied requirements and the actual API
// The API has Types as a list of strings but the requirements want Type as a string
// If there is only one element in Types, use that single element
// If there are multiple elements in Types, convert all elements into a comma separated string
// Report discrepancy to Producer as an issue but do not block ongoing development
PokemonCardsOutput ConvertFromInputToOutput(PokemonCardsInput cardsJson)
{
    string currentMethodName = nameof(ConvertFromInputToOutput);
    Console.WriteLine($"Begin [{currentMethodName}()]");
    var cards = new List<PokemonCardOutput>();
    if (cardsJson.Data != null)
    {
        foreach (var card in cardsJson.Data)
        {
            var cardOutput = new PokemonCardOutput
            {
                ID = card.ID,
                HP = card.HP,
                Name = card.Name,
                Rarity = card.Rarity,
            };
            if (card.Types?.Count > 1)
            {
                cardOutput.Type = string.Join(",", card.Types);
            }
            else
            {
                cardOutput.Type = card.Types?.First();
            }
            cards.Add(cardOutput);
        }
    }
    var output = new PokemonCardsOutput
    {
        Cards = cards
    };
    Console.WriteLine($"End [{currentMethodName}()]");
    return output;
}

// New style .NET main entry point
// Logging to console for simplicity since we do not have a logging framework
var appName = AppDomain.CurrentDomain.FriendlyName;
Console.WriteLine($"Begin [{appName}()]");

var limit = 0;
var optionsHelp = "Options:"
    + "\n\t--limit [number]\tLimit results from API"
    + "\n\t--debug\t\t\tRead debug JSON from file"
    + "\n\nNote that parameters are mutually exclusive";
bool debug = false;

// Non-suave args processing since we cannot use any packages
if (args.Length == 0)
{
    Console.WriteLine(optionsHelp);
    return 1;
}
if (args[0] == "--debug")
{
    debug = true;
}
else if (args.Length != 2 || args[0] != "--limit" || !int.TryParse(args[1], out limit))
{
    Console.WriteLine(optionsHelp);
    return 1;
}

// If debug mode, use file
// Otherwise read from API
string cardsInputJsonString;
if (debug)
{
    cardsInputJsonString = await File.ReadAllTextAsync(@".\test.json");
}
else
{
    cardsInputJsonString = await GetCardsJson(limit);

}
if (string.IsNullOrEmpty(cardsInputJsonString))
{
    // Something weird happened with getting the data
    // Fast fail and trigger investigation
    throw new Exception("Error! Null or empty string returned when obtaining JSON string!");
}
Console.WriteLine($"Cards JSON [ {cardsInputJsonString} ]");

// Built-in .NET deserialization since we cannot use the Newtonsoft package
var options = new JsonSerializerOptions()
{
    PropertyNameCaseInsensitive = true,
    AllowTrailingCommas = true,
    IncludeFields = true,
    WriteIndented = true,
};
var cardsInput = JsonSerializer.Deserialize<PokemonCardsInput>(cardsInputJsonString, options);
if (cardsInput == null)
{
    // Something weird happened with deserialization
    // Fast fail and trigger investigation
    throw new Exception("Error! Null value returned from JSON Deserializer");
}
var cardsOutput = ConvertFromInputToOutput(cardsInput);

// Results should be sorted when returned from API but sort anyway in case
// ConvertFromInputToOutput becomes non-deterministic via future development
var sortedCards = cardsOutput.Cards?.OrderBy(card => card.ID).ToList();
cardsOutput.Cards = sortedCards;

var cardsOutputJsonString = JsonSerializer.Serialize(cardsOutput, options);

Console.WriteLine(cardsOutputJsonString);
Console.WriteLine($"End [{appName}]");
return 0;

// Using classes as opposed to structs because we do not know how many potential cards there could be
// and we do not want to overflow the stack
class PokemonCardInput
{
    public string? ID;
    public string? Name;
    public string? HP;
    public List<string>? Types;
    public string? Rarity;
}
class PokemonCardsInput
{
    public List<PokemonCardInput>? Data;
    public int Page;
    public int PageSize;
    public int Count;
    public int TotalCount;
}

class PokemonCardOutput
{
    public string? ID;
    public string? Name;
    public string? Type;
    public string? HP;
    public string? Rarity;
}
class PokemonCardsOutput
{
    public List<PokemonCardOutput>? Cards;
}

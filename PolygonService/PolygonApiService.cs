using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using DataModels;
using Newtonsoft.Json;

namespace PolygonService
{
    /// <summary>
    /// Handles fetching market data from the Polygon.io REST API.
    /// </summary>
    public class PolygonApiService
    {
        private readonly string _apiKey;
        private readonly HttpClient _httpClient;

        public PolygonApiService(string apiKey)
        {
            if (string.IsNullOrWhiteSpace(apiKey) || apiKey == "YOUR_POLYGON_API_KEY")
            {
                throw new ArgumentException("Polygon API key is not set. Please update SharedConfig/appsettings.json.", nameof(apiKey));
            }
            _apiKey = apiKey;
            _httpClient = new HttpClient();
        }

        /// <summary>
        /// Fetches aggregate bars from Polygon.io for a given date range and time frame.
        /// </summary>
        public async Task<List<MarketBar>> GetAggregates(string ticker, string from, string to, string timeFrame, int multiplier)
        {
            var allBars = new List<MarketBar>();
            // Polygon API has a limit of 50,000 results per request, so we loop through the date range.
            for (var day = DateTime.Parse(from); day.Date <= DateTime.Parse(to); day = day.AddDays(1))
            {
                var dateStr = day.ToString("yyyy-MM-dd");
                string url = $"https://api.polygon.io/v2/aggs/ticker/{ticker}/range/{multiplier}/{timeFrame}/{dateStr}/{dateStr}?adjusted=true&sort=asc&limit=50000&apiKey={_apiKey}";

                try
                {
                    HttpResponseMessage response = await _httpClient.GetAsync(url);
                    response.EnsureSuccessStatusCode();
                    string responseBody = await response.Content.ReadAsStringAsync();

                    var result = JsonConvert.DeserializeObject<PolygonAggregatesResponse>(responseBody);

                    if (result?.Results != null)
                    {
                        allBars.AddRange(result.Results);
                        Console.WriteLine($"Fetched {result.ResultsCount} bars for {dateStr}");
                        // A small delay to respect API rate limits. 
                        // Polygon free tier allows 5 requests/minute.
                        //await Task.Delay(13000);
                    }
                }
                catch (HttpRequestException e)
                {
                    Console.WriteLine($"\nError fetching data for {dateStr}: {e.Message}");
                    // Stop if we hit an error (e.g., invalid API key or network issues)
                    break;
                }
            }
            return allBars.OrderBy(b => b.Timestamp).ToList();
        }
    }
}
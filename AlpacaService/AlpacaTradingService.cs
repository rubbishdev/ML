using Alpaca.Markets;
using DataModels;
using Newtonsoft.Json;
using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;

namespace AlpacaService
{
    /// <summary>
    /// Handles communication with the Alpaca API for account and trading operations.
    /// </summary>
    public class AlpacaTradingService
    {
        private readonly HttpClient _httpClient;
        private readonly string _baseUrl;

        public AlpacaTradingService(string apiKey, string apiSecret, bool isPaper)
        {
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Accept.Clear();
            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            _httpClient.DefaultRequestHeaders.Add("APCA-API-KEY-ID", apiKey);
            _httpClient.DefaultRequestHeaders.Add("APCA-API-SECRET-KEY", apiSecret);

            _baseUrl = isPaper ? "https://paper-api.alpaca.markets" : "https://api.alpaca.markets";
        }

        public async Task<AccountInfo> GetAccountInfo()
        {
            var response = await _httpClient.GetStringAsync($"{_baseUrl}/v2/account");
            return JsonConvert.DeserializeObject<AccountInfo>(response);
        }

        public async Task<Position> GetOpenPosition(string ticker)
        {
            try
            {
                var response = await _httpClient.GetStringAsync($"{_baseUrl}/v2/positions/{ticker}");
                return JsonConvert.DeserializeObject<Position>(response);
            }
            catch (HttpRequestException)
            {
                // API returns 404 if there is no position, which is normal.
                return null;
            }
        }

        public async Task ClosePosition(string ticker)
        {
            Console.WriteLine($"Attempting to close position for {ticker}...");
            var response = await _httpClient.DeleteAsync($"{_baseUrl}/v2/positions/{ticker}");
            response.EnsureSuccessStatusCode();
        }

        public async Task SubmitMarketOrder(string ticker, int quantity, bool isBuy)
        {
            var order = new
            {
                symbol = ticker,
                qty = quantity.ToString(),
                side = isBuy ? "buy" : "sell",
                type = "market",
                time_in_force = "day"
            };
            var json = JsonConvert.SerializeObject(order);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync($"{_baseUrl}/v2/orders", content);
            response.EnsureSuccessStatusCode();
        }
    }
}


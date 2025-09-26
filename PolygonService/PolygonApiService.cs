using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DataModels;
using Newtonsoft.Json;

namespace PolygonService
{
    /// <summary>
    /// Handles fetching market data from the Polygon.io REST API and WebSocket.
    /// </summary>
    public class PolygonApiService
    {
        private readonly string _apiKey;
        private readonly string _ticker; // New: Store ticker for subscription
        private readonly HttpClient _httpClient;
        private ClientWebSocket _webSocket;
        private readonly ConcurrentQueue<AggregateEvent> _realTimeMinuteEvents = new ConcurrentQueue<AggregateEvent>(); // Buffer for real-time minute events
        private CancellationTokenSource _wsCts;

        public PolygonApiService(string apiKey, string ticker)
        {
            if (string.IsNullOrWhiteSpace(apiKey) || apiKey == "YOUR_POLYGON_API_KEY")
            {
                throw new ArgumentException("Polygon API key is not set. Please update SharedConfig/appsettings.json.", nameof(apiKey));
            }
            _apiKey = apiKey;
            _ticker = ticker;
            _httpClient = new HttpClient();
        }

        /// <summary>
        /// Fetches aggregate bars from Polygon.io for a given date range and time frame (REST for historical).
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

        /// <summary>
        /// Connects to Polygon.io WebSocket for real-time data and subscribes to aggregates.
        /// </summary>
        public async Task ConnectToWebSocket()
        {
            _wsCts = new CancellationTokenSource();
            _webSocket = new ClientWebSocket();
            await _webSocket.ConnectAsync(new Uri("wss://socket.polygon.io/stocks"), _wsCts.Token);

            // Authenticate
            var authMessage = JsonConvert.SerializeObject(new { action = "auth", @params = _apiKey });
            await SendWebSocketMessage(authMessage);

            // Subscribe to minute aggregates for the ticker
            var subscribeMessage = JsonConvert.SerializeObject(new { action = "subscribe", @params = $"AM.{_ticker}" });
            await SendWebSocketMessage(subscribeMessage);

            // Start receiving messages
            _ = Task.Run(() => ReceiveWebSocketMessages(_wsCts.Token));
        }

        /// <summary>
        /// Disconnects from the Polygon.io WebSocket.
        /// </summary>
        public async Task DisconnectWebSocket()
        {
            if (_webSocket.State == WebSocketState.Open)
            {
                await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
            }
            _wsCts.Cancel();
        }

        private async Task SendWebSocketMessage(string message)
        {
            var bytes = Encoding.UTF8.GetBytes(message);
            await _webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _wsCts.Token);
        }

        private async Task ReceiveWebSocketMessages(CancellationToken token)
        {
            var buffer = new byte[4096];
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await DisconnectWebSocket();
                        break;
                    }
                    var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    var events = JsonConvert.DeserializeObject<List<AggregateEvent>>(message);
                    foreach (var ev in events.Where(e => e.ev == "AM"))
                    {
                        _realTimeMinuteEvents.Enqueue(ev);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"WebSocket error: {ex.Message}");
                    break;
                }
            }
        }

        /// <summary>
        /// Aggregates real-time minute bars into custom timeframe bars (e.g., 5-minute).
        /// </summary>
        public async Task<List<MarketBar>> GetRealTimeAggregatedBars(string ticker, int multiplier)
        {
            // Add a small delay to allow for data accumulation if needed
            await Task.Delay(100); // Optional, to ensure queue has data

            var aggregatedBars = new List<MarketBar>();
            var currentBar = new MarketBar();
            int count = 0;

            while (_realTimeMinuteEvents.TryDequeue(out var minuteEvent))
            {
                if (count == 0)
                {
                    currentBar.Timestamp = minuteEvent.t;
                    currentBar.Open = minuteEvent.o;
                    currentBar.High = minuteEvent.h;
                    currentBar.Low = minuteEvent.l;
                    currentBar.Close = minuteEvent.c;
                    currentBar.Volume = minuteEvent.v;
                    currentBar.VolumeWeightedAveragePrice = minuteEvent.vw;
                    currentBar.NumberOfTransactions = minuteEvent.n;
                }
                else
                {
                    currentBar.High = Math.Max(currentBar.High, minuteEvent.h);
                    currentBar.Low = Math.Min(currentBar.Low, minuteEvent.l);
                    currentBar.Close = minuteEvent.c;
                    currentBar.Volume += minuteEvent.v;
                    currentBar.NumberOfTransactions += minuteEvent.n;
                    // VWAP approximation: weighted average
                    currentBar.VolumeWeightedAveragePrice = ((currentBar.VolumeWeightedAveragePrice * (currentBar.Volume - minuteEvent.v)) + (minuteEvent.vw * minuteEvent.v)) / currentBar.Volume;
                }

                count++;
                if (count == multiplier)
                {
                    aggregatedBars.Add(currentBar);
                    currentBar = new MarketBar();
                    count = 0;
                }
            }

            return aggregatedBars;
        }

        private class AggregateEvent
        {
            public string ev { get; set; }
            public long t { get; set; }
            public decimal o { get; set; }
            public decimal h { get; set; }
            public decimal l { get; set; }
            public decimal c { get; set; }
            public long v { get; set; }
            public decimal vw { get; set; }
            public int n { get; set; }
        }
    }
}
using Newtonsoft.Json;
using System;
using System.Collections.Generic;

namespace DataModels
{
    // Represents a single bar of market data from Polygon.io
    public class MarketBar
    {
        [JsonProperty("o")]
        public decimal Open { get; set; }

        [JsonProperty("h")]
        public decimal High { get; set; }

        [JsonProperty("l")]
        public decimal Low { get; set; }

        [JsonProperty("c")]
        public decimal Close { get; set; }

        [JsonProperty("v")]
        public decimal Volume { get; set; }

        [JsonProperty("t")]
        public long Timestamp { get; set; }

        [JsonProperty("vw")]
        public decimal? VolumeWeightedAveragePrice { get; set; } // Added vw

        [JsonProperty("n")]
        public long? NumberOfTransactions { get; set; } // Added n

        // Ensures that the timestamp is always treated as UTC
        public DateTime Date => DateTimeOffset.FromUnixTimeMilliseconds(Timestamp).UtcDateTime;
    }

    // Represents the response from the Polygon.io aggregates API
    public class PolygonAggregatesResponse
    {
        [JsonProperty("results")]
        public List<MarketBar> Results { get; set; }

        [JsonProperty("resultsCount")]
        public int ResultsCount { get; set; }

        [JsonProperty("status")]
        public string Status { get; set; }
    }
}


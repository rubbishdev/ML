using Newtonsoft.Json;
using System;
using System.Collections.Generic;

namespace DataModels
{
    /// <summary>
    /// Represents a single bar of market data from Polygon.io
    /// </summary>
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
        public long Volume { get; set; }

        [JsonProperty("t")]
        public long Timestamp { get; set; }

        [JsonProperty("vw")]
        public decimal VolumeWeightedAveragePrice { get; set; } // Added vw

        [JsonProperty("n")]
        public long NumberOfTransactions { get; set; } // Added n

        // Ensures that the Timestamp is always treated as UTC
        public DateTime Date { get; set; } // Updated: Made settable
    }

    /// <summary>
    /// Represents the response from the Polygon.io aggregates API
    /// </summary>
    public class PolygonAggregatesResponse
    {
        public string Ticker { get; set; }
        public int ResultsCount { get; set; }
        public List<MarketBar> Results { get; set; }
    }
}
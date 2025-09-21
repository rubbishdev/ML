using Newtonsoft.Json;

namespace DataModels
{
    /// <summary>
    /// Represents account information from the Alpaca API.
    /// </summary>
    public class AccountInfo
    {
        [JsonProperty("buying_power")]
        public string BuyingPower { get; set; }

        [JsonProperty("cash")]
        public string Cash { get; set; }

        [JsonProperty("portfolio_value")]
        public string PortfolioValue { get; set; }

        [JsonProperty("equity")]
        public string Equity { get; set; }
    }

    /// <summary>
    /// Represents an open position from the Alpaca API.
    /// </summary>
    public class Position
    {
        [JsonProperty("symbol")]
        public string Symbol { get; set; }

        [JsonProperty("qty")]
        public string Quantity { get; set; }

        [JsonProperty("side")]
        public string Side { get; set; } // "long" or "short"

        [JsonProperty("unrealized_pl")]
        public string UnrealizedPl { get; set; }

        [JsonProperty("avg_entry_price")]
        public string AvgEntryPrice { get; set; }
    }
}


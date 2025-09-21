using System;

namespace DataModels
{
    /// <summary>
    /// Represents a simplified trade record for backtesting or logging.
    /// </summary>
    public class SimulatedTrade
    {
        public string Ticker { get; set; }
        public TradeDirection Direction { get; set; }
        public DateTime EntryTime { get; set; }
        public decimal EntryPrice { get; set; }
        public DateTime? ExitTime { get; set; }
        public decimal? ExitPrice { get; set; }
        public decimal? ProfitLoss { get; set; }
        public decimal Quantity { get; set; }
    }

    public enum TradeDirection
    {
        Long,
        Short
    }

    public class TimePeriodAnalysis
    {
        public string Period { get; set; }
        public int NumberOfTrades { get; set; }
        public double WinRate { get; set; }
        public decimal AverageProfitLoss { get; set; }
    }
}

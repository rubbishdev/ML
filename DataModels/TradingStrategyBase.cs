using DataModels;
using System.Collections.Generic;

namespace Strategies
{
    /// <summary>
    /// Abstract base class for trading strategies, enabling modularity and easy swapping.
    /// </summary>
    public abstract class TradingStrategyBase
    {
        public abstract string Name { get; }
        public abstract void Initialize();
        public abstract string GenerateSignal(MarketBar currentBar, List<MarketBar> historicalBars);
        public abstract Dictionary<string, object> GetParameters();
        public abstract void SetParameters(Dictionary<string, object> parameters);
    }
}
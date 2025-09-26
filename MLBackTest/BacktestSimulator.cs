using DataModels;
using Strategies;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MLBackTest
{
    public class BacktestResult
    {
        public List<TradeLog> TradeLog { get; set; }
        public decimal EndingCapital { get; set; }
        public double SharpeRatio { get; set; }
        public decimal MaxDrawdown { get; set; }
        public double WinRate { get; set; }
    }

    public class BacktestSimulator
    {
        private readonly TradingStrategyBase _strategy;
        private const decimal SlippageAndCommission = 0.001m;
        private const decimal DailyLossCap = -0.02m;
        private const decimal MaxPositionSizePercentage = 0.50m;
        private const int MaxTradesPerDay = 5;

        public BacktestSimulator(TradingStrategyBase strategy)
        {
            _strategy = strategy ?? throw new ArgumentNullException(nameof(strategy));
            _strategy.Initialize();
        }

        public BacktestResult Run(List<MarketBar> backtestData, decimal startingCapital, bool allowShortSelling, decimal takeProfitPercentage, decimal stopLossPercentage)
        {
            var tradeLog = new List<TradeLog>();
            decimal accountBalance = startingCapital;
            decimal peakBalance = startingCapital;
            List<decimal> equityCurve = new List<decimal> { startingCapital };
            List<decimal> dailyReturns = new List<decimal>();
            TradeLog currentTrade = null;
            int sharesToTrade = 0;
            decimal trailingStopPrice = 0m;
            decimal dailyStartingBalance = startingCapital;
            decimal dailyPnl = 0m;
            int dailyTradeCount = 0;
            DateTime currentDay = DateTime.MinValue;
            decimal previousBalance = startingCapital;

            TimeZoneInfo easternZone;
            try { easternZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"); }
            catch { easternZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York"); }

            var marketOpen = new TimeSpan(9, 30, 0);
            var marketClose = new TimeSpan(16, 0, 0);

            for (int i = Math.Max(26, 14); i < backtestData.Count; i++) // Ensure enough data for EMA/RSI
            {
                var currentDataPoint = backtestData[i];
                var currentUtcTime = DateTimeOffset.FromUnixTimeMilliseconds(currentDataPoint.Timestamp).UtcDateTime;
                var currentEtTime = TimeZoneInfo.ConvertTimeFromUtc(currentUtcTime, easternZone);
                bool isMarketHours = currentEtTime.TimeOfDay >= marketOpen && currentEtTime.TimeOfDay < marketClose;

                // Reset daily metrics
                if (currentEtTime.Date != currentDay)
                {
                    dailyStartingBalance = accountBalance;
                    dailyPnl = 0m;
                    dailyTradeCount = 0;
                    currentDay = currentEtTime.Date;
                }

                if (dailyPnl / dailyStartingBalance <= DailyLossCap || dailyTradeCount >= MaxTradesPerDay)
                    continue;

                var historicalBars = backtestData.Take(i + 1).ToList();
                var signal = _strategy.GenerateSignal(currentDataPoint, historicalBars);

                if (currentTrade == null && isMarketHours)
                {
                    bool isSellSignal = signal == "Sell";
                    if (signal == "Buy" || (isSellSignal && allowShortSelling))
                    {
                        var atr = CalculateATR(historicalBars, 14);
                        decimal riskPerShare = atr * 2; // 2x ATR for stop
                        sharesToTrade = (int)(accountBalance * 0.01m / riskPerShare); // 1% risk
                        sharesToTrade = Math.Min(sharesToTrade, (int)(accountBalance * MaxPositionSizePercentage / currentDataPoint.Close));

                        if (sharesToTrade > 0)
                        {
                            currentTrade = new TradeLog
                            {
                                EntryTime = currentEtTime,
                                Signal = signal,
                                EntryPrice = currentDataPoint.Close * (1 + (isSellSignal ? -SlippageAndCommission : SlippageAndCommission)),
                            };
                            trailingStopPrice = currentTrade.EntryPrice * (isSellSignal ? (1 + stopLossPercentage) : (1 - stopLossPercentage));
                            dailyTradeCount++;
                        }
                    }
                }
                else if (currentTrade != null)
                {
                    bool isSellSignal = currentTrade.Signal == "Sell";
                    decimal currentPrice = currentDataPoint.Close;
                    decimal takeProfitPrice = currentTrade.EntryPrice * (isSellSignal ? (1 - takeProfitPercentage) : (1 + takeProfitPercentage));
                    decimal stopLossPrice = currentTrade.EntryPrice * (isSellSignal ? (1 + stopLossPercentage) : (1 - stopLossPercentage));

                    // Update trailing stop
                    var atr = CalculateATR(historicalBars, 14);
                    if (isSellSignal)
                    {
                        if (currentPrice < trailingStopPrice)
                            trailingStopPrice = Math.Min(trailingStopPrice, currentPrice * (1 + atr * 2 / currentPrice));
                    }
                    else
                    {
                        if (currentPrice > trailingStopPrice)
                            trailingStopPrice = Math.Max(trailingStopPrice, currentPrice * (1 - atr * 2 / currentPrice));
                    }

                    bool exitCondition = isSellSignal
                        ? (currentPrice <= takeProfitPrice || currentPrice >= stopLossPrice || currentPrice >= trailingStopPrice || currentEtTime.TimeOfDay >= marketClose)
                        : (currentPrice >= takeProfitPrice || currentPrice <= stopLossPrice || currentPrice <= trailingStopPrice || currentEtTime.TimeOfDay >= marketClose);

                    if (exitCondition)
                    {
                        currentTrade.ExitPrice = currentPrice * (1 + (isSellSignal ? SlippageAndCommission : -SlippageAndCommission));
                        currentTrade.ProfitLoss = (isSellSignal ? (currentTrade.EntryPrice - currentTrade.ExitPrice) : (currentTrade.ExitPrice - currentTrade.EntryPrice)) * sharesToTrade;
                        accountBalance += currentTrade.ProfitLoss;
                        dailyPnl += currentTrade.ProfitLoss;
                        tradeLog.Add(currentTrade);

                        equityCurve.Add(accountBalance);
                        dailyReturns.Add((accountBalance - previousBalance) / previousBalance);
                        peakBalance = Math.Max(peakBalance, accountBalance);
                        previousBalance = accountBalance;

                        currentTrade = null;
                        sharesToTrade = 0;
                    }
                }
            }

            var winRate = tradeLog.Count > 0 ? tradeLog.Count(t => t.ProfitLoss > 0) / (double)tradeLog.Count : 0;
            var sharpe = dailyReturns.Count > 1 ? CalculateSharpeRatio(dailyReturns) : 0;
            var maxDD = CalculateMaxDrawdown(equityCurve, peakBalance);

            return new BacktestResult
            {
                TradeLog = tradeLog,
                EndingCapital = accountBalance,
                SharpeRatio = sharpe,
                MaxDrawdown = maxDD,
                WinRate = winRate
            };
        }

        public List<TimePeriodAnalysis> GenerateTimePeriodAnalysis(List<TradeLog> tradeLog)
        {
            var analysis = new List<TimePeriodAnalysis>();
            TimeZoneInfo easternZone;
            try { easternZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"); }
            catch { easternZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York"); }

            var periods = new Dictionary<string, (TimeSpan start, TimeSpan end)>
            {
                { "Market Open", (new TimeSpan(9, 30, 0), new TimeSpan(11, 0, 0)) },
                { "Mid-Day", (new TimeSpan(11, 0, 0), new TimeSpan(15, 0, 0)) },
                { "Market Close", (new TimeSpan(15, 0, 0), new TimeSpan(16, 0, 0)) }
            };

            foreach (var period in periods)
            {
                var trades = tradeLog.Where(t =>
                {
                    var entryTimeEt = TimeZoneInfo.ConvertTimeFromUtc(t.EntryTime, easternZone).TimeOfDay;
                    return entryTimeEt >= period.Value.start && entryTimeEt < period.Value.end;
                }).ToList();
                analysis.Add(AnalyzePeriod(period.Key, trades));
            }

            return analysis;
        }

        private TimePeriodAnalysis AnalyzePeriod(string periodName, List<TradeLog> trades)
        {
            var totalTrades = trades.Count;
            var winningTrades = trades.Count(t => t.ProfitLoss > 0);
            var totalPnl = trades.Sum(t => t.ProfitLoss);
            var avgPnl = totalTrades > 0 ? trades.Average(t => t.ProfitLoss) : 0;
            var returns = trades.Select(t => t.ProfitLoss / (t.EntryPrice * (t.ExitPrice - t.EntryPrice > 0 ? 1 : -1))).ToList();
            var sharpe = returns.Count > 1 ? CalculateSharpeRatio(returns) : 0;

            return new TimePeriodAnalysis
            {
                Period = periodName,
                NumberOfTrades = totalTrades,
                WinRate = totalTrades > 0 ? (double)winningTrades / totalTrades : 0,
                AverageProfitLoss = avgPnl,
                TotalProfitLoss = totalPnl,
                SharpeRatio = sharpe
            };
        }

        private double CalculateSharpeRatio(List<decimal> returns)
        {
            if (returns.Count == 0) return 0;
            var avgReturn = returns.Average();
            var stdDev = Math.Sqrt(returns.Average(v => Math.Pow((double)(v - avgReturn), 2)));
            return stdDev == 0 ? 0 : (double)avgReturn / stdDev * Math.Sqrt(252); // Annualized
        }

        private decimal CalculateMaxDrawdown(List<decimal> equityCurve, decimal peakBalance)
        {
            decimal maxDD = 0;
            decimal peak = peakBalance;
            foreach (var equity in equityCurve)
            {
                peak = Math.Max(peak, equity);
                decimal dd = (peak - equity) / peak;
                if (dd > maxDD) maxDD = dd;
            }
            return maxDD;
        }

        private decimal CalculateATR(List<MarketBar> bars, int period)
        {
            if (bars.Count < period) return 0;
            var tr = new List<decimal>();
            for (int j = 1; j < bars.Count; j++)
            {
                tr.Add(Math.Max(bars[j].High - bars[j].Low, Math.Max(Math.Abs(bars[j].High - bars[j - 1].Close), Math.Abs(bars[j].Low - bars[j - 1].Close))));
            }
            return tr.GetRange(tr.Count - period, period).Average();
        }
    }

    public class TimePeriodAnalysis
    {
        public string Period { get; set; }
        public int NumberOfTrades { get; set; }
        public double WinRate { get; set; }
        public decimal AverageProfitLoss { get; set; }
        public decimal TotalProfitLoss { get; set; }
        public double SharpeRatio { get; set; }
    }
}
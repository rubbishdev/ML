using DataModels;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Strategies
{
    /// <summary>
    /// A generic trading strategy class that can be extended or updated with different logic.
    /// Currently implements an optimized Opening Range Breakout (ORB) strategy for intraday QQQ, focusing on market open trends with quick exits.
    /// Incorporates profitability mods: long-only, high volume filter, RSI to avoid extremes, ATR-based SL/TP, and max hold time.
    /// </summary>
    public class GenericTradingStrategy : TradingStrategyBase
    {
        public override string Name => "GenericStrategy";
        private int OrbPeriodMinutes { get; set; } = 15;  // 15-min ORB (3 bars on 5-min chart)
        private int RsiPeriod { get; set; } = 14;         // RSI to filter extremes
        private double RsiOverbought { get; set; } = 80.0;  // Looser for more trades
        private double RsiOversold { get; set; } = 20.0;    // Looser
        private int AtrPeriod { get; set; } = 14;         // For dynamic SL/TP
        private decimal AtrMultiplierStop { get; set; } = 0.05m;  // Tight 5% of ATR as per research
        private decimal AtrMultiplierTarget { get; set; } = 3.0m;  // Asymmetric TP
        private bool EnableShorts { get; set; } = false;  // Disabled for higher win rate in QQQ
        private int VolumeMaPeriod { get; set; } = 20;    // Volume MA
        private decimal MinVolumeMultiplier { get; set; } = 1.2m;  // Mild for frequency
        private TimeSpan MaxTradeDuration { get; set; } = new TimeSpan(0, 0, 0);  // Hold to EOD as per profitable backtests (set to 0 for EOD)

        public override void Initialize()
        {
            if (RsiPeriod < 1 || AtrPeriod < 1 || VolumeMaPeriod < 1)
                throw new ArgumentException("Invalid indicator periods");
        }

        public override string GenerateSignal(MarketBar currentBar, List<MarketBar> historicalBars)
        {
            if (historicalBars.Count < Math.Max(RsiPeriod, Math.Max(AtrPeriod, VolumeMaPeriod)))
            {
                Console.WriteLine($"Skipped signal: Insufficient historical bars ({historicalBars.Count})");
                return "Hold";
            }

            // Time restriction: Signals post-ORB up to EOD for hold-to-close
            TimeZoneInfo easternZone;
            try { easternZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"); }
            catch { easternZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York"); }
            var currentUtcTime = DateTimeOffset.FromUnixTimeMilliseconds(currentBar.Timestamp).UtcDateTime;
            var currentEtTime = TimeZoneInfo.ConvertTimeFromUtc(currentUtcTime, easternZone);
            var currentTimeOfDay = currentEtTime.TimeOfDay;
            var marketOpen = new TimeSpan(9, 30, 0);
            var orbEnd = marketOpen.Add(new TimeSpan(0, OrbPeriodMinutes, 0));
            var marketClose = new TimeSpan(16, 0, 0);
            if (currentTimeOfDay < orbEnd || currentTimeOfDay > marketClose.Add(new TimeSpan(0, -5, 0)))  // Avoid last 5 min
            {
                Console.WriteLine($"Skipped signal: Outside ORB signal window ({currentTimeOfDay})");
                return "Hold";
            }

            // Filter historicalBars to today's bars (assume full history; group by date)
            var todayDate = currentEtTime.Date;
            var todaysBars = historicalBars.Where(b =>
            {
                var barUtc = DateTimeOffset.FromUnixTimeMilliseconds(b.Timestamp).UtcDateTime;
                var barEt = TimeZoneInfo.ConvertTimeFromUtc(barUtc, easternZone);
                return barEt.Date == todayDate && barEt.TimeOfDay >= marketOpen;
            }).ToList();

            if (todaysBars.Count < OrbPeriodMinutes / 5) return "Hold";  // Wait for ORB formation

            // Calculate ORB from first bars of today
            int barsInOrb = OrbPeriodMinutes / 5;
            var orbBars = todaysBars.Take(barsInOrb).ToList();
            if (orbBars.Count < barsInOrb) return "Hold";

            decimal orbHigh = orbBars.Max(b => b.High);
            decimal orbLow = orbBars.Min(b => b.Low);

            var closePrices = todaysBars.Select(b => b.Close).ToList();
            var highPrices = todaysBars.Select(b => b.High).ToList();
            var lowPrices = todaysBars.Select(b => b.Low).ToList();
            var volumes = todaysBars.Select(b => (decimal)b.Volume).ToList();

            // RSI filter
            var rsi = CalculateRSI(closePrices, RsiPeriod);

            // ATR (use full history for stability)
            var atr = CalculateATR(highPrices, lowPrices, closePrices, AtrPeriod);

            // Volume confirmation
            var volumeMa = volumes.Skip(volumes.Count - VolumeMaPeriod).Average();
            if ((decimal)currentBar.Volume < MinVolumeMultiplier * volumeMa)
            {
                Console.WriteLine($"Skipped signal: Low breakout volume ({currentBar.Volume} < {MinVolumeMultiplier} * {volumeMa})");
                return "Hold";
            }

            // Breakout signals
            if (currentBar.High > orbHigh && rsi < RsiOverbought)
            {
                Console.WriteLine($"Generated Buy: Upside ORB breakout {currentBar.High} > {orbHigh}, RSI {rsi:F2} < {RsiOverbought}");
                return "Buy";
            }

            if (currentBar.Low < orbLow && rsi > RsiOversold && EnableShorts)
            {
                Console.WriteLine($"Generated Sell: Downside ORB breakout {currentBar.Low} < {orbLow}, RSI {rsi:F2} > {RsiOversold}");
                return "Sell";
            }

            Console.WriteLine("Skipped: No ORB breakout or RSI condition met");
            return "Hold";
        }

        public override Dictionary<string, object> GetParameters()
        {
            return new Dictionary<string, object>
            {
                { nameof(OrbPeriodMinutes), OrbPeriodMinutes },
                { nameof(RsiPeriod), RsiPeriod },
                { nameof(RsiOverbought), RsiOverbought },
                { nameof(RsiOversold), RsiOversold },
                { nameof(AtrPeriod), AtrPeriod },
                { nameof(AtrMultiplierStop), AtrMultiplierStop },
                { nameof(AtrMultiplierTarget), AtrMultiplierTarget },
                { nameof(EnableShorts), EnableShorts },
                { nameof(VolumeMaPeriod), VolumeMaPeriod },
                { nameof(MinVolumeMultiplier), MinVolumeMultiplier },
                { nameof(MaxTradeDuration), MaxTradeDuration }
            };
        }

        public override void SetParameters(Dictionary<string, object> parameters)
        {
            if (parameters.ContainsKey(nameof(OrbPeriodMinutes))) OrbPeriodMinutes = Convert.ToInt32(parameters[nameof(OrbPeriodMinutes)]);
            if (parameters.ContainsKey(nameof(RsiPeriod))) RsiPeriod = Convert.ToInt32(parameters[nameof(RsiPeriod)]);
            if (parameters.ContainsKey(nameof(RsiOverbought))) RsiOverbought = Convert.ToDouble(parameters[nameof(RsiOverbought)]);
            if (parameters.ContainsKey(nameof(RsiOversold))) RsiOversold = Convert.ToDouble(parameters[nameof(RsiOversold)]);
            if (parameters.ContainsKey(nameof(AtrPeriod))) AtrPeriod = Convert.ToInt32(parameters[nameof(AtrPeriod)]);
            if (parameters.ContainsKey(nameof(AtrMultiplierStop))) AtrMultiplierStop = Convert.ToDecimal(parameters[nameof(AtrMultiplierStop)]);
            if (parameters.ContainsKey(nameof(AtrMultiplierTarget))) AtrMultiplierTarget = Convert.ToDecimal(parameters[nameof(AtrMultiplierTarget)]);
            if (parameters.ContainsKey(nameof(EnableShorts))) EnableShorts = Convert.ToBoolean(parameters[nameof(EnableShorts)]);
            if (parameters.ContainsKey(nameof(VolumeMaPeriod))) VolumeMaPeriod = Convert.ToInt32(parameters[nameof(VolumeMaPeriod)]);
            if (parameters.ContainsKey(nameof(MinVolumeMultiplier))) MinVolumeMultiplier = Convert.ToDecimal(parameters[nameof(MinVolumeMultiplier)]);
            if (parameters.ContainsKey(nameof(MaxTradeDuration))) MaxTradeDuration = (TimeSpan)parameters[nameof(MaxTradeDuration)];
        }

        private static List<decimal> CalculateEMA(List<decimal> prices, int period)
        {
            var ema = new List<decimal>(new decimal[prices.Count]);
            decimal multiplier = 2.0m / (period + 1);
            ema[0] = prices[0];
            for (int i = 1; i < prices.Count; i++)
            {
                ema[i] = (prices[i] - ema[i - 1]) * multiplier + ema[i - 1];
            }
            return ema;
        }

        private static double CalculateRSI(List<decimal> prices, int period)
        {
            if (prices.Count < period + 1) return 50.0;
            decimal gain = 0, loss = 0;
            for (int i = 1; i <= period; i++)
            {
                var diff = prices[prices.Count - i] - prices[prices.Count - i - 1];
                if (diff > 0) gain += diff;
                else loss -= diff;
            }
            if (loss == 0) return 100.0;
            var rs = (gain / period) / (loss / period);
            return 100.0 - (100.0 / (1.0 + (double)rs));
        }

        private static decimal CalculateATR(List<decimal> high, List<decimal> low, List<decimal> close, int period)
        {
            if (high.Count < period + 1) return 0;
            var tr = new List<decimal>();
            for (int j = 1; j < high.Count; j++)
            {
                tr.Add(Math.Max(high[j] - low[j], Math.Max(Math.Abs(high[j] - close[j - 1]), Math.Abs(low[j] - close[j - 1]))));
            }
            return CalculateEMA(tr.Skip(tr.Count - period).ToList(), period).Last();
        }
    }
}
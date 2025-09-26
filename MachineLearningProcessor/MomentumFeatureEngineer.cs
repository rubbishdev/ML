// Updated MachineLearningProcessor/MomentumFeatureEngineer.cs (with new features)
using DataModels;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MachineLearningProcessor
{
    public class MomentumFeatureEngineer
    {
        // Smoothed Indicator Periods for 5-minute chart
        private const int RsiPeriod = 14;
        private const int StochasticPeriod = 14;
        private const int MacdFastPeriod = 12;
        private const int MacdSlowPeriod = 26;
        private const int PriceChangePeriod = 5;
        private const int VolumeSpikePeriod = 20;
        private const int TransactionSpikePeriod = 20;
        private const int SmaPeriod = 50;
        private const int AtrPeriod = 14;
        private const int BollingerPeriod = 20; // Standard for Bollinger Bands
        private const int BollingerStdDev = 2; // Standard multiplier

        // How many bars into the future to look for a signal (6 * 5min = 30 minutes)
        private const int FutureWindow = 6;

        // Adjusted threshold: Increased to 0.3% for better signal separation
        private const decimal ProfitThreshold = 0.002m; // 0.3% profit or loss threshold for a signal

        public List<ModelInput> GenerateFeaturesAndLabels(List<MarketBar> rawData)
        {
            var allData = new List<ModelInput>();
            var closePrices = rawData.Select(b => (decimal)b.Close).ToList();
            var highPrices = rawData.Select(b => (decimal)b.High).ToList();
            var lowPrices = rawData.Select(b => (decimal)b.Low).ToList();
            var volumes = rawData.Select(b => (decimal)b.Volume).ToList();
            var transactions = rawData.Select(b => (decimal)b.NumberOfTransactions).ToList();
            var vwap = rawData.Select(b => (decimal)b.VolumeWeightedAveragePrice).ToList();

            // Calculate SMAs for price, volume, and transactions
            var priceSma = CalculateSMA(closePrices, SmaPeriod);
            var volumeSma = CalculateSMA(volumes, VolumeSpikePeriod);
            var transactionSma = CalculateSMA(transactions, TransactionSpikePeriod);

            // Precompute RSIs for lagging
            var rsis = new List<decimal>();
            for (int i = 0; i < rawData.Count; i++)
            {
                rsis.Add(CalculateRSI(closePrices.GetRange(0, i + 1), RsiPeriod));
            }

            // Precompute Bollinger %B
            var bollingerPercentB = CalculateBollingerPercentB(closePrices, BollingerPeriod, BollingerStdDev);

            for (int i = SmaPeriod; i < rawData.Count - FutureWindow; i++)
            {
                var currentBar = rawData[i];
                var dto = DateTimeOffset.FromUnixTimeMilliseconds(currentBar.Timestamp);
                var modelInput = new ModelInput
                {
                    // Raw Data
                    Open = (float)currentBar.Open,
                    High = (float)currentBar.High,
                    Low = (float)currentBar.Low,
                    Close = (float)currentBar.Close,
                    Volume = (float)currentBar.Volume,
                    Timestamp = currentBar.Timestamp,

                    // Engineered Features
                    RSI = (float)rsis[i],
                    StochasticOscillator = (float)CalculateStochasticOscillator(closePrices.GetRange(0, i + 1), highPrices.GetRange(0, i + 1), lowPrices.GetRange(0, i + 1), StochasticPeriod),
                    MACD = (float)CalculateMACD(closePrices.GetRange(0, i + 1), MacdFastPeriod, MacdSlowPeriod),
                    PriceChangePercentage = i > PriceChangePeriod ? (float)((currentBar.Close - rawData[i - PriceChangePeriod].Close) / rawData[i - PriceChangePeriod].Close) : 0,
                    VolumeSpike = volumeSma[i] > 0 ? (float)((volumes[i] - volumeSma[i]) / volumeSma[i]) : 0,
                    VwapCloseDifference = (float)((currentBar.Close - currentBar.VolumeWeightedAveragePrice) / currentBar.VolumeWeightedAveragePrice),
                    TransactionSpike = transactionSma[i] > 0 ? (float)((transactions[i] - transactionSma[i]) / transactionSma[i]) : 0,
                    PriceSmaDifference = priceSma[i] > 0 ? (float)((currentBar.Close - priceSma[i]) / priceSma[i]) : 0,
                    TimeOfDay = (float)(dto.LocalDateTime.TimeOfDay.TotalMinutes / 1440.0),
                    ATR = (float)CalculateATR(highPrices.GetRange(0, i + 1), lowPrices.GetRange(0, i + 1), closePrices.GetRange(0, i + 1), AtrPeriod),
                    BollingerPercentB = (float)bollingerPercentB[i],
                    RSI_Lag1 = i > 0 ? (float)rsis[i - 1] : (float)rsis[i] // Lag1, default to current if first
                };

                // Label Generation
                decimal maxFutureHigh = rawData.GetRange(i + 1, FutureWindow).Max(b => (decimal)b.High);
                decimal minFutureLow = rawData.GetRange(i + 1, FutureWindow).Min(b => (decimal)b.Low);
                decimal potentialProfit = (maxFutureHigh - (decimal)currentBar.Close) / (decimal)currentBar.Close;
                decimal potentialLoss = (minFutureLow - (decimal)currentBar.Close) / (decimal)currentBar.Close;

                if (potentialProfit > ProfitThreshold)
                {
                    modelInput.Label = "Buy";
                }
                else if (potentialLoss < -ProfitThreshold)
                {
                    modelInput.Label = "Sell";
                }
                else
                {
                    modelInput.Label = "Hold";
                }

                allData.Add(modelInput);
            }
            return allData;
        }

        // --- New: Bollinger %B Calculation ---
        private static List<decimal> CalculateBollingerPercentB(List<decimal> closePrices, int period, int stdDevMultiplier)
        {
            var percentB = new List<decimal>(new decimal[closePrices.Count]);
            var sma = CalculateSMA(closePrices, period);

            for (int i = 0; i < closePrices.Count; i++)
            {
                if (i < period - 1)
                {
                    percentB[i] = 50; // Default neutral
                    continue;
                }

                // Calculate STD
                var slice = closePrices.GetRange(i - period + 1, period);
                decimal avg = sma[i];
                decimal variance = slice.Sum(p => (p - avg) * (p - avg)) / period;
                decimal stdDev = (decimal)Math.Sqrt((double)variance);

                decimal upperBand = avg + stdDev * stdDevMultiplier;
                decimal lowerBand = avg - stdDev * stdDevMultiplier;

                if (upperBand == lowerBand)
                {
                    percentB[i] = 50;
                }
                else
                {
                    percentB[i] = 100 * (closePrices[i] - lowerBand) / (upperBand - lowerBand);
                }
            }
            return percentB;
        }

        // --- Technical Indicator Calculation Methods ---

        private static List<decimal> CalculateSMA(List<decimal> prices, int period)
        {
            var sma = new List<decimal>(new decimal[prices.Count]);
            decimal sum = 0;
            for (int i = 0; i < prices.Count; i++)
            {
                sum += prices[i];
                if (i >= period)
                {
                    sum -= prices[i - period];
                    sma[i] = sum / period;
                }
            }
            return sma;
        }

        private static decimal CalculateRSI(List<decimal> closePrices, int period)
        {
            if (closePrices.Count < period + 1) return 50;

            decimal gain = 0;
            decimal loss = 0;

            for (int i = closePrices.Count - period; i < closePrices.Count; i++)
            {
                var diff = closePrices[i] - closePrices[i - 1];
                if (diff > 0)
                    gain += diff;
                else
                    loss -= diff;
            }

            if (loss == 0) return 100;

            var rs = (gain / period) / (loss / period);
            return 100 - (100 / (1 + rs));
        }

        private static decimal CalculateStochasticOscillator(List<decimal> closePrices, List<decimal> highPrices, List<decimal> lowPrices, int period)
        {
            if (closePrices.Count < period) return 50;

            var relevantHighs = highPrices.GetRange(closePrices.Count - period, period);
            var relevantLows = lowPrices.GetRange(closePrices.Count - period, period);

            var highestHigh = relevantHighs.Max();
            var lowestLow = relevantLows.Min();

            if (highestHigh == lowestLow) return 50;

            return 100 * ((closePrices.Last() - lowestLow) / (highestHigh - lowestLow));
        }

        private static decimal CalculateMACD(List<decimal> closePrices, int fastPeriod, int slowPeriod)
        {
            if (closePrices.Count < slowPeriod) return 0;
            var fastEma = CalculateEMA(closePrices, fastPeriod).Last();
            var slowEma = CalculateEMA(closePrices, slowPeriod).Last();
            return fastEma - slowEma;
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

        private static decimal CalculateATR(List<decimal> high, List<decimal> low, List<decimal> close, int period)
        {
            if (high.Count < period) return 0;
            var tr = new List<decimal>();
            for (int j = 1; j < high.Count; j++)
            {
                tr.Add(Math.Max(high[j] - low[j], Math.Max(Math.Abs(high[j] - close[j - 1]), Math.Abs(low[j] - close[j - 1]))));
            }
            return CalculateEMA(tr.GetRange(tr.Count - period, period), period).Last();
        }
    }
}
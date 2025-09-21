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

        // How many bars into the future to look for a signal (6 * 5min = 30 minutes)
        private const int FutureWindow = 6;

        // EXPERIMENT: Reverting to a symmetrical threshold to improve precision.
        private const decimal ProfitThreshold = 0.002m; // 0.2% profit or loss threshold for a signal

        public List<ModelInput> GenerateFeaturesAndLabels(List<MarketBar> rawData)
        {
            var allData = new List<ModelInput>();
            var closePrices = rawData.Select(b => b.Close).ToList();
            var highPrices = rawData.Select(b => b.High).ToList();
            var lowPrices = rawData.Select(b => b.Low).ToList();
            var volumes = rawData.Select(b => b.Volume).ToList();
            var transactions = rawData.Select(b => b.NumberOfTransactions).ToList();
            var vwap = rawData.Select(b => b.VolumeWeightedAveragePrice).ToList();

            // Calculate SMAs for price, volume, and transactions
            var priceSma = CalculateSMA(closePrices, SmaPeriod);
            var volumeSma = CalculateSMA(volumes.Select(v => (decimal)v).ToList(), VolumeSpikePeriod);
            var transactionSma = CalculateSMA(transactions.Select(t => (decimal)t).ToList(), TransactionSpikePeriod);

            for (int i = SmaPeriod; i < rawData.Count - FutureWindow; i++)
            {
                var currentBar = rawData[i];
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
                    RSI = (float)CalculateRSI(closePrices.GetRange(0, i + 1), RsiPeriod),
                    StochasticOscillator = (float)CalculateStochasticOscillator(closePrices.GetRange(0, i + 1), highPrices.GetRange(0, i + 1), lowPrices.GetRange(0, i + 1), StochasticPeriod),
                    MACD = (float)CalculateMACD(closePrices.GetRange(0, i + 1), MacdFastPeriod, MacdSlowPeriod),
                    PriceChangePercentage = i > PriceChangePeriod ? (float)((currentBar.Close - rawData[i - PriceChangePeriod].Close) / rawData[i - PriceChangePeriod].Close) : 0,
                    VolumeSpike = volumeSma[i] > 0 ? (float)((currentBar.Volume - volumeSma[i]) / volumeSma[i]) : 0,
                    VwapCloseDifference = currentBar.VolumeWeightedAveragePrice > 0 ? (float)((currentBar.Close - currentBar.VolumeWeightedAveragePrice) / currentBar.VolumeWeightedAveragePrice) : 0,
                    TransactionSpike = transactionSma[i] > 0 ? (float)((currentBar.NumberOfTransactions - transactionSma[i]) / transactionSma[i]) : 0,
                    PriceSmaDifference = priceSma[i] > 0 ? (float)((currentBar.Close - priceSma[i]) / priceSma[i]) : 0,
                    TimeOfDay = (float)currentBar.Date.TimeOfDay.TotalHours,
                };

                // Updated Labeling logic with symmetrical thresholds
                var futureSlice = rawData.GetRange(i + 1, FutureWindow);
                var peakFuturePrice = futureSlice.Max(b => b.High);
                var troughFuturePrice = futureSlice.Min(b => b.Low);

                var potentialProfit = (peakFuturePrice - currentBar.Close) / currentBar.Close;
                var potentialLoss = (troughFuturePrice - currentBar.Close) / currentBar.Close;

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
    }
}


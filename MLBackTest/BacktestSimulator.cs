using DataModels;
using Microsoft.ML;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MLBackTest
{
    public class BacktestResult
    {
        public List<TradeLog> TradeLog { get; set; }
        public decimal EndingCapital { get; set; }
    }

    public class BacktestSimulator
    {
        private const decimal SlippageAndCommission = 0.0005m;

        public BacktestResult Run(List<ModelInput> backtestData, ITransformer model, MLContext mlContext, decimal startingCapital, bool allowShortSelling, decimal takeProfitPercentage, decimal stopLossPercentage)
        {
            var tradeLog = new List<TradeLog>();
            var predictionEngine = mlContext.Model.CreatePredictionEngine<ModelInput, ModelOutput>(model);

            decimal accountBalance = startingCapital;
            TradeLog currentTrade = null;
            int sharesToTrade = 0;

            TimeZoneInfo easternZone;
            try { easternZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"); }
            catch { easternZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York"); }

            var marketOpen = new TimeSpan(9, 30, 0);
            var marketClose = new TimeSpan(16, 0, 0);

            for (int i = 0; i < backtestData.Count; i++)
            {
                var currentDataPoint = backtestData[i];
                var prediction = predictionEngine.Predict(currentDataPoint);

                var currentUtcTime = DateTimeOffset.FromUnixTimeMilliseconds(currentDataPoint.Timestamp).UtcDateTime;
                var currentEtTime = TimeZoneInfo.ConvertTimeFromUtc(currentUtcTime, easternZone);
                bool isMarketHours = currentEtTime.TimeOfDay >= marketOpen && currentEtTime.TimeOfDay < marketClose;

                if (currentTrade == null && isMarketHours)
                {
                    bool isSellSignal = prediction.PredictedLabel == "Sell";
                    if (prediction.PredictedLabel == "Buy" || (isSellSignal && allowShortSelling))
                    {
                        sharesToTrade = (int)(accountBalance / (decimal)currentDataPoint.Close);
                        if (sharesToTrade > 0)
                        {
                            currentTrade = new TradeLog
                            {
                                EntryTime = currentUtcTime,
                                Signal = prediction.PredictedLabel,
                                EntryPrice = (decimal)currentDataPoint.Close
                            };
                            var entryTimeEt = TimeZoneInfo.ConvertTimeFromUtc(currentTrade.EntryTime, easternZone);
                            Console.WriteLine($"{entryTimeEt:yyyy-MM-dd HH:mm:ss ET} | OPEN  | {currentTrade.Signal.ToUpper(),-4} | {sharesToTrade,5} shares @ {currentTrade.EntryPrice,8:C} | Value: {(currentTrade.EntryPrice * sharesToTrade),12:C}");
                        }
                    }
                }
                else if (currentTrade != null)
                {
                    decimal currentPrice = (decimal)currentDataPoint.Close;
                    decimal pnlPercentage = 0;
                    if (currentTrade.EntryPrice > 0)
                    {
                        pnlPercentage = (currentTrade.Signal == "Buy")
                            ? (currentPrice - currentTrade.EntryPrice) / currentTrade.EntryPrice
                            : (currentTrade.EntryPrice - currentPrice) / currentTrade.EntryPrice;
                    }

                    bool takeProfitHit = pnlPercentage >= takeProfitPercentage;
                    bool stopLossHit = pnlPercentage <= -stopLossPercentage;
                    bool contrarySignal = (currentTrade.Signal == "Buy" && prediction.PredictedLabel == "Sell") || (currentTrade.Signal == "Sell" && prediction.PredictedLabel == "Buy");
                    bool isEndOfDay = currentEtTime.TimeOfDay >= marketClose;

                    if (contrarySignal || isEndOfDay || takeProfitHit || stopLossHit)
                    {
                        currentTrade.ExitPrice = currentPrice;
                        var profitPerShare = currentTrade.ExitPrice - currentTrade.EntryPrice;
                        if (currentTrade.Signal == "Sell") profitPerShare = -profitPerShare;

                        var totalProfit = (profitPerShare * sharesToTrade) - (currentTrade.EntryPrice * sharesToTrade * SlippageAndCommission) - (currentTrade.ExitPrice * sharesToTrade * SlippageAndCommission);
                        currentTrade.ProfitLoss = totalProfit;
                        accountBalance += totalProfit;
                        tradeLog.Add(currentTrade);

                        var exitTimeEt = TimeZoneInfo.ConvertTimeFromUtc(currentUtcTime, easternZone);
                        Console.Write($"{exitTimeEt:yyyy-MM-dd HH:mm:ss ET} | CLOSE | {currentTrade.Signal.ToUpper(),-4} | {sharesToTrade,5} shares @ {currentTrade.ExitPrice,8:C} | P/L:   ");
                        Console.ForegroundColor = currentTrade.ProfitLoss >= 0 ? ConsoleColor.Green : ConsoleColor.Red;
                        Console.WriteLine($"{currentTrade.ProfitLoss,12:C}");
                        Console.ResetColor();

                        currentTrade = null;
                        sharesToTrade = 0;
                    }
                }
            }
            return new BacktestResult { TradeLog = tradeLog, EndingCapital = accountBalance };
        }

        public List<TimePeriodAnalysis> GenerateTimePeriodAnalysis(List<TradeLog> tradeLog)
        {
            var analysis = new List<TimePeriodAnalysis>();
            TimeZoneInfo easternZone;
            try { easternZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"); }
            catch { easternZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York"); }

            var openTrades = tradeLog.Where(r =>
            {
                var entryTimeEt = TimeZoneInfo.ConvertTimeFromUtc(r.EntryTime, easternZone);
                return entryTimeEt.Hour >= 9 && entryTimeEt.Hour < 11;
            }).ToList();

            var midDayTrades = tradeLog.Where(r =>
            {
                var entryTimeEt = TimeZoneInfo.ConvertTimeFromUtc(r.EntryTime, easternZone);
                return entryTimeEt.Hour >= 11 && entryTimeEt.Hour < 15;
            }).ToList();

            var closeTrades = tradeLog.Where(r =>
            {
                var entryTimeEt = TimeZoneInfo.ConvertTimeFromUtc(r.EntryTime, easternZone);
                return entryTimeEt.Hour >= 15 && entryTimeEt.Hour < 16;
            }).ToList();

            analysis.Add(AnalyzePeriod("Market Open", openTrades));
            analysis.Add(AnalyzePeriod("Mid-Day", midDayTrades));
            analysis.Add(AnalyzePeriod("Market Close", closeTrades));

            return analysis;
        }

        private TimePeriodAnalysis AnalyzePeriod(string periodName, List<TradeLog> trades)
        {
            var totalTrades = trades.Count;
            var winningTrades = trades.Count(t => t.ProfitLoss > 0);

            return new TimePeriodAnalysis
            {
                Period = periodName,
                NumberOfTrades = totalTrades,
                WinRate = totalTrades > 0 ? (double)winningTrades / totalTrades : 0,
                AverageProfitLoss = totalTrades > 0 ? trades.Average(t => t.ProfitLoss) : 0
            };
        }
    }
}
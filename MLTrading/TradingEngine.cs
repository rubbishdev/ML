using AlpacaService;
using DataModels;
using MachineLearningProcessor;
using Microsoft.ML;
using PolygonService;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MLTrading
{
    public class TradingEngine
    {
        private readonly PolygonApiService _polygon;
        private readonly AlpacaTradingService _alpaca;
        private readonly MomentumFeatureEngineer _featureEngineer;
        private readonly ITransformer _model;
        private readonly MLContext _mlContext;
        private readonly string _ticker;
        private readonly string _timeFrame;
        private readonly int _timeFrameMultiplier;
        private readonly PredictionEngine<ModelInput, ModelOutput> _predictionEngine;

        private readonly TimeZoneInfo _easternZone;
        private readonly TimeSpan _marketOpen;
        private readonly TimeSpan _marketClose;
        private readonly TimeSpan _liquidateTime; // New time to force close positions
        private readonly List<DateTime> _holidays;
        private readonly LoggerService _logger;
        private readonly bool _allowShortSelling;
        private readonly decimal _takeProfitPercentage;
        private readonly decimal _stopLossPercentage;

        public TradingEngine(PolygonApiService polygon, AlpacaTradingService alpaca, MomentumFeatureEngineer featureEngineer, ITransformer model, MLContext mlContext, string ticker, string timeFrame, int timeFrameMultiplier, LoggerService logger, bool allowShortSelling, decimal takeProfitPercentage, decimal stopLossPercentage)
        {
            _polygon = polygon;
            _alpaca = alpaca;
            _featureEngineer = featureEngineer;
            _model = model;
            _mlContext = mlContext;
            _ticker = ticker;
            _timeFrame = timeFrame;
            _timeFrameMultiplier = timeFrameMultiplier;
            _predictionEngine = _mlContext.Model.CreatePredictionEngine<ModelInput, ModelOutput>(_model);
            _logger = logger;
            _allowShortSelling = allowShortSelling;
            _takeProfitPercentage = takeProfitPercentage;
            _stopLossPercentage = stopLossPercentage;

            try
            {
                _easternZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
            }
            catch (TimeZoneNotFoundException)
            {
                _easternZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
            }
            _marketOpen = new TimeSpan(9, 30, 0);
            _marketClose = new TimeSpan(16, 0, 0); // The official market close
            // --- MODIFICATION 1: Set liquidation time to 5 seconds before market close ---
            _liquidateTime = new TimeSpan(15, 59, 55); // Time to start closing all positions
            _holidays = GetUsStockMarketHolidays(DateTime.Now.Year);
        }

        public async Task Run(CancellationToken token)
        {
            // --- PRE-MARKET WAIT LOGIC ---
            while (!token.IsCancellationRequested)
            {
                var nowEt = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _easternZone);
                bool isTradingDay = nowEt.DayOfWeek >= DayOfWeek.Monday && nowEt.DayOfWeek <= DayOfWeek.Friday && !_holidays.Contains(nowEt.Date);
                bool isMarketHours = isTradingDay && nowEt.TimeOfDay >= _marketOpen && nowEt.TimeOfDay < _marketClose;

                if (isMarketHours)
                {
                    _logger.Log("Market is open. Starting trading engine...");
                    break;
                }

                DateTime nextMarketOpenEt = nowEt.Date.Add(_marketOpen);
                if (nowEt.TimeOfDay >= _marketOpen || !isTradingDay)
                {
                    nextMarketOpenEt = nextMarketOpenEt.AddDays(1);
                    while (nextMarketOpenEt.DayOfWeek == DayOfWeek.Saturday || nextMarketOpenEt.DayOfWeek == DayOfWeek.Sunday || _holidays.Contains(nextMarketOpenEt.Date))
                    {
                        nextMarketOpenEt = nextMarketOpenEt.AddDays(1);
                    }
                }

                var timeUntilOpen = nextMarketOpenEt - nowEt;
                _logger.Log($"Market is closed. Waiting for next open in {timeUntilOpen.Days}d {timeUntilOpen.Hours}h {timeUntilOpen.Minutes}m...");

                await Task.Delay(TimeSpan.FromMinutes(1), token);
            }

            // --- MAIN TRADING LOOP ---
            while (!token.IsCancellationRequested)
            {
                var nowUtc = DateTime.UtcNow;
                var nowEt = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, _easternZone);
                bool isTradingDay = nowEt.DayOfWeek >= DayOfWeek.Monday && nowEt.DayOfWeek <= DayOfWeek.Friday && !_holidays.Contains(nowEt.Date);
                bool isMarketHours = isTradingDay && nowEt.TimeOfDay >= _marketOpen && nowEt.TimeOfDay < _marketClose;

                try
                {
                    // Failsafe: If loop runs past market close, liquidate and restart for next day
                    if (!isMarketHours)
                    {
                        Position finalPosition = await _alpaca.GetOpenPosition(_ticker);
                        if (finalPosition != null)
                        {
                            _logger.Log($"{nowEt:HH:mm:ss ET} | EXIT: MARKET IS CLOSED (FAILSAFE) | Submitting order to liquidate position.");
                            await _alpaca.ClosePosition(_ticker);
                        }

                        _logger.Log("Market has closed for the day. Consolidating logs and restarting pre-market wait logic.");
                        _logger.ConsolidateLogsForDay();
                        await Run(token);
                        return;
                    }

                    Position currentPosition = await _alpaca.GetOpenPosition(_ticker);

                    if (currentPosition == null)
                    {
                        // Stop opening new positions in the last few minutes of the day
                        if (nowEt.TimeOfDay >= _liquidateTime.Subtract(TimeSpan.FromMinutes(2)))
                        {
                            _logger.Log($"{nowEt:HH:mm:ss ET} | End-of-day window. No new positions will be opened.");
                        }
                        else
                        {
                            var prediction = await GetPrediction();
                            _logger.Log($"{nowEt:HH:mm:ss ET} | Polling... | Prediction: {prediction}");

                            bool isSellSignal = prediction == "Sell";
                            if (prediction == "Buy" || (isSellSignal && _allowShortSelling))
                            {
                                var accountInfo = await _alpaca.GetAccountInfo();
                                var latestPrice = await GetLatestPrice();

                                if (latestPrice > 0)
                                {
                                    int sharesToTrade = 0;
                                    if (prediction == "Buy")
                                    {
                                        decimal buyingPower = decimal.Parse(accountInfo.Equity);
                                        sharesToTrade = (int)(buyingPower / latestPrice);
                                    }
                                    else // This is a "Sell" signal
                                    {
                                        // EXPERIMENT: Calculate short position size based on 50% of account equity.
                                        decimal equity = decimal.Parse(accountInfo.Equity);
                                        decimal capitalToShort = equity;
                                        sharesToTrade = (int)(capitalToShort / latestPrice);
                                    }

                                    if (sharesToTrade > 0)
                                    {
                                        _logger.Log($"{nowEt:HH:mm:ss ET} | SIGNAL: {prediction.ToUpper(),-4} | Submitting market order for {sharesToTrade} shares.");
                                        await _alpaca.SubmitMarketOrder(_ticker, sharesToTrade, prediction == "Buy");
                                    }
                                }
                            }
                        }
                    }
                    else
                    {
                        string currentSide = currentPosition.Side == "long" ? "Buy" : "Sell";
                        bool isTimeToLiquidate = nowEt.TimeOfDay >= _liquidateTime;

                        if (isTimeToLiquidate)
                        {
                            _logger.Log($"{nowEt:HH:mm:ss ET} | EXIT: END OF DAY LIQUIDATION | Submitting order to close {currentPosition.Quantity} shares.");
                            await _alpaca.ClosePosition(_ticker);
                        }
                        else
                        {
                            var latestPrice = await GetLatestPrice();
                            var entryPrice = decimal.Parse(currentPosition.AvgEntryPrice);
                            decimal pnlPercentage = 0;

                            if (entryPrice > 0)
                            {
                                pnlPercentage = (currentSide == "Buy") ? (latestPrice - entryPrice) / entryPrice : (entryPrice - latestPrice) / entryPrice;
                            }

                            // --- EXIT CONDITIONS ---
                            bool takeProfitHit = pnlPercentage >= _takeProfitPercentage;
                            bool stopLossHit = pnlPercentage <= -_stopLossPercentage;

                            var prediction = await GetPrediction();
                            // CORRECTED LOGIC: A "Sell" signal should always exit a "Buy" position, regardless of short selling permissions.
                            bool contrarySignal = (currentSide == "Buy" && prediction == "Sell") ||
                                                  (currentSide == "Sell" && prediction == "Buy");

                            if (contrarySignal || takeProfitHit || stopLossHit)
                            {
                                string reason = "Unknown";
                                if (takeProfitHit) reason = $"TAKE PROFIT ({pnlPercentage:P2})";
                                else if (stopLossHit) reason = $"STOP LOSS ({pnlPercentage:P2})";
                                else if (contrarySignal) reason = $"CONTRARY SIGNAL ({prediction.ToUpper()})";

                                _logger.Log($"{nowEt:HH:mm:ss ET} | EXIT: {reason} | Submitting order to close {currentPosition.Quantity} shares.");
                                await _alpaca.ClosePosition(_ticker);
                            }
                            else
                            {
                                _logger.Log($"{nowEt:HH:mm:ss ET} | HOLDING  | Side: {currentSide,-4}, Qty: {currentPosition.Quantity,5}, P/L: {pnlPercentage,8:P2}");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.Log($"An error occurred in the trading loop: {ex.Message}");
                }

                // --- MODIFICATION 2: DYNAMIC DELAY CALCULATION ---
                // This ensures the loop wakes up specifically for the end-of-day liquidation,
                // regardless of the 5-minute polling interval.
                var standardDelay = TimeSpan.FromMinutes(5);
                var liquidationTimeTodayEt = nowEt.Date.Add(_liquidateTime);
                var timeUntilLiquidation = liquidationTimeTodayEt - nowEt;

                var nextDelay = standardDelay;
                // Check if liquidation time is in the future and is sooner than the next standard poll.
                if (timeUntilLiquidation > TimeSpan.Zero && timeUntilLiquidation < standardDelay)
                {
                    // We'll sleep until it's time to liquidate. Add a tiny buffer to ensure the time check passes.
                    nextDelay = timeUntilLiquidation.Add(TimeSpan.FromMilliseconds(100));
                }

                _logger.Log($"Sleeping for {nextDelay.TotalMinutes:F2} minutes...");
                await Task.Delay(nextDelay, token);
            }
        }

        private async Task<string> GetPrediction()
        {
            var today = DateTime.UtcNow.Date;
            var endDate = today.ToString("yyyy-MM-dd");
            string startDate;

            // Determine the start date based on the day of the week to handle weekends correctly.
            switch (today.DayOfWeek)
            {
                case DayOfWeek.Monday:
                    // If it's Monday, we need to go back 3 days to get Friday's data.
                    startDate = today.AddDays(-3).ToString("yyyy-MM-dd");
                    break;
                case DayOfWeek.Sunday:
                    // If it's Sunday, look back to Friday (2 days).
                    startDate = today.AddDays(-2).ToString("yyyy-MM-dd");
                    break;
                default:
                    // For any other day (Tue-Sat), just go back 1 day.
                    startDate = today.AddDays(-1).ToString("yyyy-MM-dd");
                    break;
            }

            var bars = await _polygon.GetAggregates(_ticker, startDate, endDate, _timeFrame, _timeFrameMultiplier);

            if (bars.Count < 100) return "Hold";

            var featuredData = _featureEngineer.GenerateFeaturesAndLabels(bars);
            if (!featuredData.Any()) return "Hold";

            var latestDataPoint = featuredData.Last();
            var prediction = _predictionEngine.Predict(latestDataPoint);
            return prediction.PredictedLabel;
        }

        private async Task<decimal> GetLatestPrice()
        {
            var to = DateTime.UtcNow;
            var from = to.AddMinutes(-5);
            var bars = await _polygon.GetAggregates(_ticker, from.ToString("yyyy-MM-dd"), to.ToString("yyyy-MM-dd"), "minute", 1);
            return bars.LastOrDefault()?.Close ?? 0;
        }

        private List<DateTime> GetUsStockMarketHolidays(int year)
        {
            var holidays = new List<DateTime>();
            holidays.Add(new DateTime(year, 1, 1));
            holidays.Add(GetNthDayOfWeek(year, 1, DayOfWeek.Monday, 3));
            holidays.Add(GetNthDayOfWeek(year, 2, DayOfWeek.Monday, 3));
            holidays.Add(GetNthDayOfWeek(year, 5, DayOfWeek.Monday, -1));
            holidays.Add(new DateTime(year, 6, 19));
            holidays.Add(new DateTime(year, 7, 4));
            holidays.Add(GetNthDayOfWeek(year, 9, DayOfWeek.Monday, 1));
            holidays.Add(GetNthDayOfWeek(year, 11, DayOfWeek.Thursday, 4));
            holidays.Add(new DateTime(year, 12, 25));

            var finalHolidays = new List<DateTime>();
            foreach (var holiday in holidays)
            {
                if (holiday.DayOfWeek == DayOfWeek.Saturday)
                {
                    finalHolidays.Add(holiday.AddDays(-1));
                }
                else if (holiday.DayOfWeek == DayOfWeek.Sunday)
                {
                    finalHolidays.Add(holiday.AddDays(1));
                }
                else
                {
                    finalHolidays.Add(holiday);
                }
            }
            return finalHolidays;
        }

        private DateTime GetNthDayOfWeek(int year, int month, DayOfWeek dayOfWeek, int n)
        {
            if (n > 0)
            {
                var date = new DateTime(year, month, 1);
                int count = 0;
                while (count < n)
                {
                    if (date.DayOfWeek == dayOfWeek)
                    {
                        count++;
                    }
                    if (count < n)
                    {
                        date = date.AddDays(1);
                    }
                }
                return date;
            }
            else
            {
                var date = new DateTime(year, month, 1).AddMonths(1).AddDays(-1);
                while (date.DayOfWeek != dayOfWeek)
                {
                    date = date.AddDays(-1);
                }
                return date;
            }
        }
    }
}
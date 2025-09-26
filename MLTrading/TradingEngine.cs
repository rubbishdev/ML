using AlpacaService;
using DataModels;
using PolygonService;
using Strategies;
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
        private readonly TradingStrategyBase _strategy;
        private readonly string _ticker;
        private readonly string _timeFrame;
        private readonly int _timeFrameMultiplier;
        private readonly LoggerService _logger;
        private readonly bool _allowShortSelling;
        private readonly decimal _takeProfitPercentage;
        private readonly decimal _stopLossPercentage;

        private readonly TimeZoneInfo _easternZone;
        private readonly TimeSpan _marketOpen;
        private readonly TimeSpan _marketClose;
        private readonly TimeSpan _liquidateTime;
        private readonly List<DateTime> _holidays;
        private decimal _dailyPnl = 0m;
        private decimal _dailyStartingBalance = 0m;
        private int _dailyTradeCount = 0;
        private DateTime _currentDay = DateTime.MinValue;

        private const decimal RiskPerTrade = 0.01m;
        private const decimal MaxDailyLoss = -0.05m;
        private const decimal MaxPositionSizePercentage = 0.50m;
        private const int MaxTradesPerDay = 5;
        private List<TradeLog> _sessionTrades = new List<TradeLog>();

        public TradingEngine(PolygonApiService polygon, AlpacaTradingService alpaca, TradingStrategyBase strategy, string ticker, string timeFrame, int timeFrameMultiplier, LoggerService logger, bool allowShortSelling, decimal takeProfitPercentage, decimal stopLossPercentage)
        {
            _polygon = polygon;
            _alpaca = alpaca;
            _strategy = strategy ?? throw new ArgumentNullException(nameof(strategy));
            _ticker = ticker;
            _timeFrame = timeFrame;
            _timeFrameMultiplier = timeFrameMultiplier;
            _logger = logger;
            _allowShortSelling = allowShortSelling;
            _takeProfitPercentage = takeProfitPercentage;
            _stopLossPercentage = stopLossPercentage;

            try
            {
                _easternZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
            }
            catch
            {
                _easternZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
            }
            _marketOpen = new TimeSpan(9, 30, 0);
            _marketClose = new TimeSpan(16, 0, 0);
            _liquidateTime = new TimeSpan(15, 45, 0);
            _holidays = GetUsStockMarketHolidays(DateTime.UtcNow.Year);
            _strategy.Initialize();
        }

        public async Task Run(CancellationToken cancellationToken)
        {
            _logger.Log($"Starting trading engine for {_ticker} on {_timeFrameMultiplier}-{_timeFrame} bars using {_strategy.Name}");
            await _polygon.ConnectToWebSocket();

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var openPosition = new Position();
                    var nowEt = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _easternZone);
                    if (_holidays.Contains(nowEt.Date) || nowEt.DayOfWeek == DayOfWeek.Saturday || nowEt.DayOfWeek == DayOfWeek.Sunday)
                    {
                        _logger.Log("Market closed (holiday or weekend). Sleeping for 1 hour...");
                        await Task.Delay(TimeSpan.FromHours(1), cancellationToken);
                        continue;
                    }

                    if (_currentDay != nowEt.Date)
                    {
                        var accountInfo = await _alpaca.GetAccountInfo();
                        _dailyStartingBalance = !string.IsNullOrEmpty(accountInfo.Equity) ? decimal.Parse(accountInfo.Equity) : 0m;
                        _dailyPnl = 0m;
                        _dailyTradeCount = 0;
                        _currentDay = nowEt.Date;
                        _sessionTrades.Clear();
                        _logger.Log($"New trading day: {nowEt:yyyy-MM-dd}");
                    }

                    if (nowEt.TimeOfDay < _marketOpen || nowEt.TimeOfDay >= _marketClose)
                    {
                        await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken);
                        continue;
                    }

                    if (nowEt.TimeOfDay >= _liquidateTime)
                    {
                        openPosition = await _alpaca.GetOpenPosition(_ticker);
                        if (openPosition != null)
                        {
                            await _alpaca.ClosePosition(_ticker);
                            _logger.Log($"Liquidated position at {nowEt:HH:mm:ss} ET");
                        }
                        await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken);
                        continue;
                    }

                    if (_dailyPnl / _dailyStartingBalance <= MaxDailyLoss || _dailyTradeCount >= MaxTradesPerDay)
                    {
                        _logger.Log("Daily loss cap or trade limit reached. Halting trading.");
                        await Task.Delay(TimeSpan.FromMinutes(5), cancellationToken);
                        continue;
                    }

                    var bars = await _polygon.GetRealTimeAggregatedBars(_ticker, _timeFrameMultiplier);
                    if (bars.Count == 0)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                        continue;
                    }

                    var currentBar = bars.Last();
                    var historicalBars = await GetRecentHistoricalBars(currentBar.Timestamp);
                    var signal = _strategy.GenerateSignal(currentBar, historicalBars);

                    openPosition = await _alpaca.GetOpenPosition(_ticker);
                    if (openPosition == null && signal != "Hold")
                    {
                        var atr = CalculateATR(historicalBars, 14);
                        decimal riskPerShare = atr * 2;
                        int quantity = (int)(_dailyStartingBalance * RiskPerTrade / riskPerShare);
                        quantity = Math.Min(quantity, (int)(_dailyStartingBalance * MaxPositionSizePercentage / currentBar.Close));

                        if (quantity > 0)
                        {
                            bool isBuy = signal == "Buy";
                            await _alpaca.SubmitMarketOrder(_ticker, quantity, isBuy);
                            _dailyTradeCount++;
                            var trade = new TradeLog
                            {
                                EntryTime = nowEt,
                                Signal = signal,
                                EntryPrice = currentBar.Close
                            };
                            _sessionTrades.Add(trade);
                            _logger.Log($"Entered {signal} position: {quantity} shares at {currentBar.Close:C} at {nowEt:HH:mm:ss} ET");
                        }
                    }
                    else if (openPosition != null)
                    {
                        bool isSellPosition = openPosition.Side == "short";
                        decimal entryPrice = decimal.Parse(openPosition.AvgEntryPrice);
                        decimal currentPrice = currentBar.Close;
                        decimal takeProfitPrice = entryPrice * (isSellPosition ? (1 - _takeProfitPercentage) : (1 + _takeProfitPercentage));
                        decimal stopLossPrice = entryPrice * (isSellPosition ? (1 + _stopLossPercentage) : (1 - _stopLossPercentage));
                        decimal trailingStopPrice = isSellPosition
                            ? Math.Min(entryPrice * (1 + _stopLossPercentage), currentPrice * (1 + CalculateATR(historicalBars, 14) * 2 / currentPrice))
                            : Math.Max(entryPrice * (1 - _stopLossPercentage), currentPrice * (1 - CalculateATR(historicalBars, 14) * 2 / currentPrice));

                        bool exitCondition = isSellPosition
                            ? (currentPrice <= takeProfitPrice || currentPrice >= stopLossPrice || currentPrice >= trailingStopPrice)
                            : (currentPrice >= takeProfitPrice || currentPrice <= stopLossPrice || currentPrice <= trailingStopPrice);

                        if (exitCondition)
                        {
                            await _alpaca.ClosePosition(_ticker);
                            var trade = _sessionTrades.Last();
                            trade.ExitPrice = currentPrice;
                            trade.ProfitLoss = (isSellPosition ? (entryPrice - currentPrice) : (currentPrice - entryPrice)) * int.Parse(openPosition.Quantity);
                            _dailyPnl += trade.ProfitLoss;
                            _logger.Log($"Closed position: P/L {trade.ProfitLoss:C} at {nowEt:HH:mm:ss} ET");
                            LogPeriodAnalysis();
                        }
                    }

                    await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.Log($"Error in trading loop: {ex.Message}");
                    await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
                }
            }
        }

        private void LogPeriodAnalysis()
        {
            var periods = new Dictionary<string, (TimeSpan start, TimeSpan end)>
            {
                { "Market Open", (new TimeSpan(9, 30, 0), new TimeSpan(11, 0, 0)) },
                { "Mid-Day", (new TimeSpan(11, 0, 0), new TimeSpan(15, 0, 0)) },
                { "Market Close", (new TimeSpan(15, 0, 0), new TimeSpan(16, 0, 0)) }
            };

            foreach (var period in periods)
            {
                var trades = _sessionTrades.Where(t =>
                {
                    var entryTimeEt = TimeZoneInfo.ConvertTimeFromUtc(t.EntryTime, _easternZone).TimeOfDay;
                    return entryTimeEt >= period.Value.start && entryTimeEt < period.Value.end;
                }).ToList();

                var totalTrades = trades.Count;
                var winningTrades = trades.Count(t => t.ProfitLoss > 0);
                var totalPnl = trades.Sum(t => t.ProfitLoss);
                var avgPnl = totalTrades > 0 ? trades.Average(t => t.ProfitLoss) : 0;

                if (totalTrades > 0)
                {
                    _logger.Log($"Period {period.Key}: Trades={totalTrades}, WinRate={(double)winningTrades / totalTrades:P2}, TotalP/L={totalPnl:C}, AvgP/L={avgPnl:C}");
                }
            }
        }

        private async Task<List<MarketBar>> GetRecentHistoricalBars(long currentTimestamp)
        {
            var to = DateTimeOffset.FromUnixTimeMilliseconds(currentTimestamp).UtcDateTime;
            var from = to.AddDays(-5);
            var bars = await _polygon.GetAggregates(_ticker, from.ToString("yyyy-MM-dd"), to.ToString("yyyy-MM-dd"), _timeFrame, _timeFrameMultiplier);
            return bars.OrderBy(b => b.Timestamp).ToList();
        }

        private decimal CalculateATR(List<MarketBar> bars, int period)
        {
            if (bars.Count < period) return 0;
            var tr = new List<decimal>();
            for (int j = 1; j < bars.Count; j++)
            {
                tr.Add(Math.Max(bars[j].High - bars[j].Low, Math.Max(Math.Abs(bars[j].High - bars[j - 1].Close), Math.Abs(bars[j].Low - bars[j - 1].Close))));
            }
            var multiplier = 2.0m / (period + 1);
            var ema = tr[0];
            for (int i = 1; i < tr.Count; i++)
            {
                ema = (tr[i] - ema) * multiplier + ema;
            }
            return ema;
        }

        private List<DateTime> GetUsStockMarketHolidays(int year)
        {
            var holidays = new List<DateTime>
            {
                new DateTime(year, 1, 1),
                GetNthDayOfWeek(year, 1, DayOfWeek.Monday, 3),
                GetNthDayOfWeek(year, 2, DayOfWeek.Monday, 3),
                GetNthDayOfWeek(year, 5, DayOfWeek.Monday, -1),
                new DateTime(year, 6, 19),
                new DateTime(year, 7, 4),
                GetNthDayOfWeek(year, 9, DayOfWeek.Monday, 1),
                GetNthDayOfWeek(year, 11, DayOfWeek.Thursday, 4),
                new DateTime(year, 12, 25)
            };

            var finalHolidays = new List<DateTime>();
            foreach (var holiday in holidays)
            {
                if (holiday.DayOfWeek == DayOfWeek.Saturday)
                    finalHolidays.Add(holiday.AddDays(-1));
                else if (holiday.DayOfWeek == DayOfWeek.Sunday)
                    finalHolidays.Add(holiday.AddDays(1));
                else
                    finalHolidays.Add(holiday);
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
                        count++;
                    if (count < n)
                        date = date.AddDays(1);
                }
                return date;
            }
            else
            {
                var date = new DateTime(year, month, 1).AddMonths(1).AddDays(-1);
                while (date.DayOfWeek != dayOfWeek)
                    date = date.AddDays(-1);
                return date;
            }
        }
    }
}
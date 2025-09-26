using AlpacaService;
using DataModels;
using PolygonService;
using Strategies;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using MLTrading;

namespace MLBackTest
{
    class Program
    {
        class DailyStats
        {
            public decimal TotalPnl { get; set; }
            public int TotalTrades { get; set; }
            public int WinningTrades { get; set; }
        }

        static async Task Main(string[] args)
        {
            Console.Clear();
            Console.WriteLine("--- Algorithmic Trading Backtester ---");

            // --- CONFIGURATION ---
            var builder = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
            IConfiguration config = builder.Build();

            var polygonApiKey = config["PolygonApiKey"];
            var ticker = config.GetSection("TradingConfig")["Ticker"];
            var startDate = config.GetSection("TradingConfig")["BackTestStartDate"];
            int currentYear = DateTime.UtcNow.Year;
            var endDate = $"{currentYear}-12-31";
            var timeFrame = config.GetSection("TradingConfig")["TimeFrame"];
            var timeFrameMultiplier = int.Parse(config.GetSection("TradingConfig")["TimeFrameMultiplier"]);
            var allowShortSelling = bool.Parse(config.GetSection("TradingConfig")["AllowShortSelling"]);
            var startingCapital = decimal.Parse(config.GetSection("BacktestConfig")["StartingCapital"]);
            var takeProfitPercentage = decimal.Parse(config.GetSection("RiskManagement")["TakeProfitPercentage"]);
            var stopLossPercentage = decimal.Parse(config.GetSection("RiskManagement")["StopLossPercentage"]);
            var strategyConfig = config.GetSection("StrategyConfig").GetChildren().ToDictionary(c => c.Key, c => (object)c.Value);

            // --- PATHS ---
            string solutionRoot = Directory.GetParent(AppContext.BaseDirectory).Parent.Parent.Parent.Parent.FullName;
            var dataPath = Path.Combine(solutionRoot, "MachineLearningProcessor", "Data");
            var logsPath = Path.Combine(Directory.GetParent(AppContext.BaseDirectory).Parent.Parent.FullName, "Logs");
            Directory.CreateDirectory(dataPath);
            Directory.CreateDirectory(logsPath);

            var fileNameSafeStartDate = startDate.Replace("-", "");
            var fileNameSafeEndDate = endDate.Replace("-", "");
            var dataFileIdentifier = $"{ticker}_{timeFrameMultiplier}_{timeFrame}_{fileNameSafeStartDate}_{fileNameSafeEndDate}";
            var rawDataCsvPath = Path.Combine(dataPath, $"{dataFileIdentifier}_raw_data.csv");
            var backtestLogPath = Path.Combine(logsPath, $"{dataFileIdentifier}_backtest_results.csv");

            // --- SERVICES ---
            TimeZoneInfo easternZone;
            try { easternZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"); }
            catch { easternZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York"); }

            var logger = new LoggerService(logsPath, easternZone);
            var dataHandler = new DataHandler();
            var polygonService = new PolygonApiService(polygonApiKey, ticker);
            var strategy = new GenericTradingStrategy();
            strategy.SetParameters(strategyConfig);
            var simulator = new BacktestSimulator(strategy);

            // --- DATA ---
            List<MarketBar> rawData;
            if (File.Exists(rawDataCsvPath))
            {
                rawData = dataHandler.LoadFromCsv<MarketBar>(rawDataCsvPath);
                logger.Log($"Loaded {rawData.Count} bars from cache: {rawDataCsvPath}");
            }
            else
            {
                rawData = await polygonService.GetAggregates(ticker, startDate, endDate, timeFrame, timeFrameMultiplier);
                dataHandler.SaveToCsv(rawData, rawDataCsvPath);
                logger.Log($"Fetched and cached {rawData.Count} bars to: {rawDataCsvPath}");
            }

            if (rawData.Count == 0)
            {
                logger.Log("No data available. Exiting.");
                return;
            }

            // Split data: 80% train, 20% test (chronological)
            //int testStartIndex = (int)(rawData.Count * 0.8);
            //var trainData = rawData.Take(testStartIndex).ToList();
            var testData = rawData.ToList();
            //logger.Log($"Data split: {trainData.Count} train bars, {testData.Count} test bars");

            // --- BACKTEST ---
            //logger.Log("Running backtest on training data...");
            //var trainResult = simulator.Run(trainData, startingCapital, allowShortSelling, takeProfitPercentage, stopLossPercentage);
            //LogBacktestResults(trainResult, "Training", logger, backtestLogPath);

            logger.Log("Running backtest on test data (OOS)...");
            var testResult = simulator.Run(testData, startingCapital, allowShortSelling, takeProfitPercentage, stopLossPercentage);
            LogBacktestResults(testResult, "Test (OOS)", logger, backtestLogPath);

            logger.ConsolidateLogsForDay();
        }

        private static void LogBacktestResults(BacktestResult result, string dataset, LoggerService logger, string backtestLogPath)
        {
            logger.Log($"\n--- {dataset} Backtest Results ---");
            logger.Log($"Ending Capital: {result.EndingCapital:C}");
            logger.Log($"Sharpe Ratio: {result.SharpeRatio:F2}");
            logger.Log($"Max Drawdown: {result.MaxDrawdown:P2}");
            logger.Log($"Win Rate: {result.WinRate:P2}");
            logger.Log($"Total Trades: {result.TradeLog.Count}");

            var periodAnalysis = new BacktestSimulator(new GenericTradingStrategy()).GenerateTimePeriodAnalysis(result.TradeLog);
            logger.Log("\n--- Intraday Performance ---");
            logger.Log("+----------------+--------+----------+---------------+---------------+----------+");
            logger.Log("| Period         | Trades | Win Rate | Avg P/L       | Total P/L     | Sharpe   |");
            logger.Log("+----------------+--------+----------+---------------+---------------+----------+");
            foreach (var period in periodAnalysis)
            {
                logger.Log($"| {period.Period,-14} | {period.NumberOfTrades,6} | {period.WinRate,7:P2} | {period.AverageProfitLoss,13:C} | {period.TotalProfitLoss,13:C} | {period.SharpeRatio,8:F2} |");
            }
            logger.Log("+----------------+--------+----------+---------------+---------------+----------+");

            var dailyStats = new Dictionary<DateTime, DailyStats>();
            foreach (var trade in result.TradeLog)
            {
                TimeZoneInfo easternZone;
                try { easternZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"); }
                catch { easternZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York"); }
                var entryEt = TimeZoneInfo.ConvertTimeFromUtc(trade.EntryTime, easternZone).Date;
                if (!dailyStats.ContainsKey(entryEt))
                    dailyStats[entryEt] = new DailyStats();
                dailyStats[entryEt].TotalTrades++;
                dailyStats[entryEt].TotalPnl += trade.ProfitLoss;
                if (trade.ProfitLoss > 0) dailyStats[entryEt].WinningTrades++;
            }

            var last30TradingDays = dailyStats.OrderByDescending(kvp => kvp.Key).Take(30).OrderBy(kvp => kvp.Key).ToList();
            logger.Log($"\n--- Last 30 Trading Days ({dataset}) ---");
            logger.Log("+------------+-----------+---------------+---------------+");
            logger.Log("| Date       | Day       | Trades (W/T)  | Daily P/L     |");
            logger.Log("+------------+-----------+---------------+---------------+");
            foreach (var day in last30TradingDays)
            {
                var stats = day.Value;
                string tradeStats = $"{stats.WinningTrades}/{stats.TotalTrades}";
                logger.Log($"| {day.Key:yyyy-MM-dd} | {day.Key.DayOfWeek,-9} | {tradeStats,13} | {stats.TotalPnl,13:C} |");
            }
            logger.Log("+------------+-----------+---------------+---------------+");

            var csvLines = new List<string> { "EntryTime,Signal,EntryPrice,ExitPrice,ProfitLoss" };
            csvLines.AddRange(result.TradeLog.Select(t => $"{t.EntryTime:yyyy-MM-dd HH:mm:ss},{t.Signal},{t.EntryPrice},{t.ExitPrice},{t.ProfitLoss}"));
            File.WriteAllLines(backtestLogPath, csvLines);
            logger.Log($"Results saved to: {backtestLogPath}");
        }
    }
}
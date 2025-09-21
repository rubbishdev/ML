using DataModels;
using MachineLearningProcessor;
using Microsoft.Extensions.Configuration;
using Microsoft.ML;
using PolygonService;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MLBackTest
{
    class Program
    {
        // A helper class to store daily performance statistics
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
            var endDate = config.GetSection("TradingConfig")["BackTestEndDate"];
            var timeFrame = config.GetSection("TradingConfig")["TimeFrame"];
            var timeFrameMultiplier = int.Parse(config.GetSection("TradingConfig")["TimeFrameMultiplier"]);
            var allowShortSelling = bool.Parse(config.GetSection("TradingConfig")["AllowShortSelling"]);
            var startingCapital = decimal.Parse(config.GetSection("BacktestConfig")["StartingCapital"]);
            var takeProfitPercentage = decimal.Parse(config.GetSection("RiskManagement")["TakeProfitPercentage"]);
            var stopLossPercentage = decimal.Parse(config.GetSection("RiskManagement")["StopLossPercentage"]);

            // --- PATHS ---
            string solutionRoot = Directory.GetParent(AppContext.BaseDirectory).Parent.Parent.Parent.Parent.FullName;
            var processorProjectRoot = Path.Combine(solutionRoot, "MachineLearningProcessor");
            var modelIdentifierStartDate = config.GetSection("TradingConfig")["StartDate"];
            var modelIdentifierEndDate = config.GetSection("TradingConfig")["EndDate"];
            var dataFileIdentifier = "QQQ_5_minute_20250101_20250909";// $"{ticker}_{timeFrameMultiplier}_{timeFrame}_{modelIdentifierStartDate.Replace("-", "")}_{modelIdentifierEndDate.Replace("-", "")}";
            var modelPath = Path.Combine(processorProjectRoot, $"{dataFileIdentifier}_momentum_model.zip");
            var backtestLogPath = Path.Combine(AppContext.BaseDirectory, $"{ticker}_{timeFrameMultiplier}_{timeFrame}_{startDate.Replace("-", "")}_{endDate.Replace("-", "")}_BacktestResults.csv");

            // --- VALIDATION ---
            if (!File.Exists(modelPath))
            {
                Console.WriteLine($"Model file not found at {modelPath}. Please run MachineLearningProcessor first with an EndDate of {modelIdentifierEndDate}.");
                return;
            }

            // --- SETUP ---
            var mlContext = new MLContext();
            var dataHandler = new DataHandler();
            var simulator = new BacktestSimulator();
            var polygonService = new PolygonApiService(polygonApiKey);
            var featureEngineer = new MomentumFeatureEngineer();

            Console.WriteLine("\nLoading trained model...");
            var model = mlContext.Model.Load(modelPath, out _);

            // --- DATA ACQUISITION & FEATURE ENGINEERING ---
            Console.WriteLine($"Downloading {timeFrameMultiplier} {timeFrame} aggregate data for {ticker} from {startDate} to {endDate}...");
            var rawData = await polygonService.GetAggregates(ticker, startDate, endDate, timeFrame, timeFrameMultiplier);
            if (!rawData.Any())
            {
                Console.WriteLine("Failed to download data for backtest. Exiting.");
                return;
            }
            Console.WriteLine($"Data download complete. Generating features...");
            var backtestData = featureEngineer.GenerateFeaturesAndLabels(rawData);

            Console.WriteLine($"\nStarting simulation on {backtestData.Count} data points with starting capital of {startingCapital:C}...");
            var results = simulator.Run(backtestData, model, mlContext, startingCapital, allowShortSelling, takeProfitPercentage, stopLossPercentage);
            Console.WriteLine("\n--- Backtest Complete ---");

            // --- RESULTS ---
            if (results.TradeLog.Any())
            {
                dataHandler.SaveToCsv(results.TradeLog, backtestLogPath);

                // --- TEARSHEET CALCULATION ---
                var tradeLog = results.TradeLog;
                var endingCapital = results.EndingCapital;
                var totalReturn = (endingCapital - startingCapital) / startingCapital;
                var winningTrades = tradeLog.Where(t => t.ProfitLoss > 0).ToList();
                var losingTrades = tradeLog.Where(t => t.ProfitLoss < 0).ToList();

                var winRate = (double)winningTrades.Count / tradeLog.Count;
                var largestWin = winningTrades.Any() ? winningTrades.Max(t => t.ProfitLoss) : 0;
                var largestLoss = losingTrades.Any() ? losingTrades.Min(t => t.ProfitLoss) : 0;
                var avgTradePl = tradeLog.Average(t => t.ProfitLoss);
                var avgWin = winningTrades.Any() ? winningTrades.Average(t => t.ProfitLoss) : 0;
                var avgLoss = losingTrades.Any() ? losingTrades.Average(t => t.ProfitLoss) : 0;

                var dailyStats = tradeLog
                    .GroupBy(t => t.EntryTime.Date)
                    .ToDictionary(
                        g => g.Key,
                        g => new DailyStats
                        {
                            TotalPnl = g.Sum(t => t.ProfitLoss),
                            TotalTrades = g.Count(),
                            WinningTrades = g.Count(t => t.ProfitLoss > 0)
                        });

                var winningDays = dailyStats.Count(d => d.Value.TotalPnl > 0);
                var losingDays = dailyStats.Count(d => d.Value.TotalPnl < 0);

                decimal peakCapital = startingCapital;
                decimal maxDrawdown = 0;
                decimal currentBalance = startingCapital;
                foreach (var trade in tradeLog)
                {
                    currentBalance += trade.ProfitLoss;
                    peakCapital = Math.Max(peakCapital, currentBalance);
                    var drawdown = peakCapital > 0 ? (peakCapital - currentBalance) / peakCapital : 0;
                    maxDrawdown = Math.Max(maxDrawdown, drawdown);
                }

                // --- TEARSHEET DISPLAY ---
                Console.WriteLine("\n\n=====================================================================================================");
                Console.WriteLine("                                                TEARSHEET");
                Console.WriteLine("=====================================================================================================");

                Console.WriteLine("\n----- PERFORMANCE METRICS -----");
                Console.Write($"Starting Capital: {startingCapital,20:C}\n");
                Console.Write($"Ending Capital:   "); PrintColoredLine(endingCapital, 20, "C");
                Console.Write($"Total Return:     "); PrintColoredLine(totalReturn, 20, "P2");

                Console.WriteLine("\n----- RISK METRICS -----");
                Console.Write($"Max Drawdown:     "); PrintColoredLine(-maxDrawdown, 20, "P2");
                Console.Write($"Largest Win:      "); PrintColoredLine(largestWin, 20, "C");
                Console.Write($"Largest Loss:     "); PrintColoredLine(largestLoss, 20, "C");

                Console.WriteLine("\n----- TRADING METRICS -----");
                Console.WriteLine($"Total Trades:     {tradeLog.Count,20}");
                Console.WriteLine($"Win Rate:         {winRate,20:P2}");
                Console.Write($"Avg. Trade P/L:   "); PrintColoredLine(avgTradePl, 20, "C");
                Console.Write($"Avg. Win:         "); PrintColoredLine(avgWin, 20, "C");
                Console.Write($"Avg. Loss:        "); PrintColoredLine(avgLoss, 20, "C");
                Console.WriteLine($"Winning Days:     {winningDays,20}");
                Console.WriteLine($"Losing Days:      {losingDays,20}");

                // --- NEW: TIME PERIOD ANALYSIS DISPLAY ---
                var timeAnalysis = simulator.GenerateTimePeriodAnalysis(results.TradeLog);
                Console.WriteLine("\n----- TIME PERIOD ANALYSIS -----");
                Console.WriteLine("+----------------+--------+----------+---------------+");
                Console.WriteLine("| Period         | Trades | Win Rate | Avg P/L       |");
                Console.WriteLine("+----------------+--------+----------+---------------+");
                foreach (var analysis in timeAnalysis)
                {
                    Console.Write($"| {analysis.Period,-14} | {analysis.NumberOfTrades,6} | {analysis.WinRate,8:P2} |");
                    PrintColoredValueInTable(analysis.AverageProfitLoss, 15, "C2");
                    Console.WriteLine(" |");
                }
                Console.WriteLine("+----------------+--------+----------+---------------+");


                // --- LAST 30 TRADING DAYS DISPLAY ---
                var last30TradingDays = dailyStats
                    .OrderByDescending(kvp => kvp.Key)
                    .Take(30)
                    .OrderBy(kvp => kvp.Key);

                Console.WriteLine($"\n----- LAST 30 TRADING DAYS P/L -----");
                Console.WriteLine("+------------+-----------+---------------+---------------+");
                Console.WriteLine("|    Date    |    Day    |  Trades (W/T) |   Daily P/L   |");
                Console.WriteLine("+------------+-----------+---------------+---------------+");

                foreach (var day in last30TradingDays)
                {
                    var stats = day.Value;
                    string tradeStats = $"{stats.WinningTrades}/{stats.TotalTrades}";

                    Console.Write($"| {day.Key:yyyy-MM-dd} | {day.Key.DayOfWeek,-9} | {tradeStats,13} |");

                    PrintColoredValueInTable(stats.TotalPnl, 15, "C0");

                    Console.WriteLine(" |");
                }
                Console.WriteLine("+------------+-----------+---------------+---------------+");


                Console.WriteLine($"\n\nResults saved to: {backtestLogPath}");
                Console.WriteLine("=====================================================================================================");
            }
            else
            {
                Console.WriteLine("Backtest finished with no trades executed.");
            }
        }

        static void PrintColoredLine(decimal value, int padding, string format)
        {
            Console.ForegroundColor = value >= 0 ? ConsoleColor.Green : ConsoleColor.Red;
            Console.WriteLine(value.ToString(format).PadLeft(padding));
            Console.ResetColor();
        }

        static void PrintColoredValueInTable(decimal value, int totalWidth, string format)
        {
            string text = value.ToString(format);
            int padding = totalWidth - text.Length - 1; // -1 for the space
            if (padding < 0) padding = 0;

            Console.Write(" "); // Leading space
            Console.ForegroundColor = value >= 0 ? ConsoleColor.Green : ConsoleColor.Red;
            Console.Write(text.PadLeft(totalWidth - 1));
            Console.ResetColor();
        }
    }
}
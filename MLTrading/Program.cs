using AlpacaService;
using DataModels;
using PolygonService;
using Strategies;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;

namespace MLTrading
{
    class Program
    {
        static async Task Main(string[] args)
        {
            // --- CONFIGURATION ---
            var builder = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
            IConfiguration config = builder.Build();

            var polygonApiKey = config["PolygonApiKey"];
            var alpacaApiKey = config["AlpacaApiKey"];
            var alpacaApiSecret = config["AlpacaApiSecret"];
            var isPaper = bool.Parse(config["IsPaperTrading"]);
            var ticker = config.GetSection("TradingConfig")["Ticker"];
            var timeFrame = config.GetSection("TradingConfig")["TimeFrame"];
            var timeFrameMultiplier = int.Parse(config.GetSection("TradingConfig")["TimeFrameMultiplier"]);
            var allowShortSelling = bool.Parse(config.GetSection("TradingConfig")["AllowShortSelling"]);
            var takeProfitPercentage = decimal.Parse(config.GetSection("RiskManagement")["TakeProfitPercentage"]);
            var stopLossPercentage = decimal.Parse(config.GetSection("RiskManagement")["StopLossPercentage"]);
            var strategyConfig = config.GetSection("StrategyConfig").GetChildren().ToDictionary(c => c.Key, c => (object)c.Value);

            // --- PATHS ---
            string solutionRoot = Directory.GetParent(AppContext.BaseDirectory).Parent.Parent.Parent.Parent.FullName;
            var logsPath = Path.Combine(Directory.GetParent(AppContext.BaseDirectory).Parent.Parent.FullName, "Logs");
            Directory.CreateDirectory(logsPath);

            // --- SERVICES ---
            TimeZoneInfo easternZone;
            try { easternZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"); }
            catch { easternZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York"); }

            var logger = new LoggerService(logsPath, easternZone);
            logger.Log("--- Algorithmic Trading Bot ---");

            var polygonService = new PolygonApiService(polygonApiKey, ticker);
            var alpacaService = new AlpacaTradingService(alpacaApiKey, alpacaApiSecret, isPaper);
            var strategy = new GenericTradingStrategy();
            strategy.SetParameters(strategyConfig);
            var tradingEngine = new TradingEngine(polygonService, alpacaService, strategy, ticker, timeFrame, timeFrameMultiplier, logger, allowShortSelling, takeProfitPercentage, stopLossPercentage);

            logger.Log("Starting trading engine. Press Ctrl+C to stop.");

            // --- MAIN LOOP ---
            var cancellationTokenSource = new CancellationTokenSource();
            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                cancellationTokenSource.Cancel();
                logger.Log("Stopping...");
            };

            await tradingEngine.Run(cancellationTokenSource.Token);
            logger.Log("Trading bot has stopped.");
            logger.ConsolidateLogsForDay();
        }
    }
}
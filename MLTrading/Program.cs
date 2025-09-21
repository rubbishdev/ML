using AlpacaService;
using DataModels;
using MachineLearningProcessor;
using Microsoft.Extensions.Configuration;
using Microsoft.ML;
using PolygonService;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

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

            // Load all configs
            var polygonApiKey = config["PolygonApiKey"];
            var alpacaApiKey = config["AlpacaApiKey"];
            var alpacaApiSecret = config["AlpacaApiSecret"];
            var isPaper = bool.Parse(config["IsPaperTrading"]);
            var ticker = config.GetSection("TradingConfig")["Ticker"];
            var startDate = config.GetSection("TradingConfig")["StartDate"];
            var endDate = config.GetSection("TradingConfig")["EndDate"];
            var timeFrame = config.GetSection("TradingConfig")["TimeFrame"];
            var timeFrameMultiplier = int.Parse(config.GetSection("TradingConfig")["TimeFrameMultiplier"]);
            var allowShortSelling = bool.Parse(config.GetSection("TradingConfig")["AllowShortSelling"]);
            var takeProfitPercentage = decimal.Parse(config.GetSection("RiskManagement")["TakeProfitPercentage"]);
            var stopLossPercentage = decimal.Parse(config.GetSection("RiskManagement")["StopLossPercentage"]);

            // --- PATHS ---
            string solutionRoot = Directory.GetParent(AppContext.BaseDirectory).Parent.Parent.Parent.Parent.FullName;
            var processorProjectRoot = Path.Combine(solutionRoot, "MachineLearningProcessor");
            var logsPath = Path.Combine(Directory.GetParent(AppContext.BaseDirectory).Parent.Parent.FullName, "Logs");

            var fileNameSafeStartDate = startDate.Replace("-", "");
            var fileNameSafeEndDate = endDate.Replace("-", "");
            var dataFileIdentifier = $"{ticker}_{timeFrameMultiplier}_{timeFrame}_{fileNameSafeStartDate}_{fileNameSafeEndDate}";
            var modelPath = Path.Combine(processorProjectRoot, $"{dataFileIdentifier}_momentum_model.zip");

            if (!File.Exists(modelPath))
            {
                Console.WriteLine($"Model file not found at {modelPath}. Please run the MachineLearningProcessor with the current settings first.");
                return;
            }

            // --- SERVICES ---
            TimeZoneInfo easternZone;
            try { easternZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"); }
            catch { easternZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York"); }

            var logger = new LoggerService(logsPath, easternZone);
            logger.Log("--- Algorithmic Trading Bot ---");

            var polygonService = new PolygonApiService(polygonApiKey);
            var alpacaService = new AlpacaTradingService(alpacaApiKey, alpacaApiSecret, isPaper);
            var featureEngineer = new MomentumFeatureEngineer();
            var mlContext = new MLContext();
            var model = mlContext.Model.Load(modelPath, out _);

            var tradingEngine = new TradingEngine(polygonService, alpacaService, featureEngineer, model, mlContext, ticker, timeFrame, timeFrameMultiplier, logger, allowShortSelling, takeProfitPercentage, stopLossPercentage);

            logger.Log("Starting trading engine. Press Ctrl+C to stop.");

            // --- MAIN LOOP ---
            var cancellationTokenSource = new CancellationTokenSource();
            Console.CancelKeyPress += (s, e) => {
                e.Cancel = true;
                cancellationTokenSource.Cancel();
                logger.Log("Stopping...");
            };

            await tradingEngine.Run(cancellationTokenSource.Token);

            logger.Log("Trading bot has stopped.");
        }
    }
}


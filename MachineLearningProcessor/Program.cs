using DataModels;
using Microsoft.Extensions.Configuration;
using Microsoft.ML;
using PolygonService;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace MachineLearningProcessor
{
    class Program
    {
        static async Task Main(string[] args)
        {
            Console.WriteLine("--- Algorithmic Trading ML Model Trainer ---");

            // --- CONFIGURATION ---
            var builder = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);

            IConfiguration config = builder.Build();

            var polygonApiKey = config["PolygonApiKey"];
            var ticker = config.GetSection("TradingConfig")["Ticker"];
            var startDate = config.GetSection("TradingConfig")["StartDate"];
            var endDate = config.GetSection("TradingConfig")["EndDate"];
            var timeFrame = config.GetSection("TradingConfig")["TimeFrame"];
            var timeFrameMultiplier = int.Parse(config.GetSection("TradingConfig")["TimeFrameMultiplier"]);

            // --- PATHS ---
            string solutionRoot = Directory.GetParent(AppContext.BaseDirectory).Parent.Parent.Parent.Parent.FullName;
            string projectRoot = Path.Combine(solutionRoot, "MachineLearningProcessor");
            var dataPath = Path.Combine(projectRoot, "Data");


            var fileNameSafeStartDate = startDate.Replace("-", "");
            var fileNameSafeEndDate = endDate.Replace("-", "");
            var dataFileIdentifier = $"{ticker}_{timeFrameMultiplier}_{timeFrame}_{fileNameSafeStartDate}_{fileNameSafeEndDate}";

            var rawDataCsvPath = Path.Combine(dataPath, $"{dataFileIdentifier}_raw_data.csv");
            var featureDataCsvPath = Path.Combine(dataPath, $"{dataFileIdentifier}_feature_data.csv");
            var modelPath = Path.Combine(projectRoot, $"{dataFileIdentifier}_momentum_model.zip");

            Directory.CreateDirectory(dataPath);

            // --- SERVICES ---
            var polygonService = new PolygonApiService(polygonApiKey);
            var dataHandler = new DataHandler();
            var featureEngineer = new MomentumFeatureEngineer();
            var mlContext = new MLContext(seed: 0);

            // STEP 1: Data Acquisition
            Console.WriteLine("\nStarting Step 1: Data Acquisition...");
            if (!File.Exists(rawDataCsvPath))
            {
                Console.WriteLine($"Data file not found. Downloading {timeFrameMultiplier} {timeFrame} aggregate data for {ticker} from {startDate} to {endDate}...");
                var bars = await polygonService.GetAggregates(ticker, startDate, endDate, timeFrame, timeFrameMultiplier);
                if (bars.Any())
                {
                    dataHandler.SaveToCsv(bars, rawDataCsvPath);
                    Console.WriteLine($"Successfully downloaded and saved {bars.Count} data points to {rawDataCsvPath}");
                }
                else
                {
                    Console.WriteLine("Failed to download data. Please check your API key and date range. Exiting.");
                    return;
                }
            }
            else
            {
                Console.WriteLine($"Raw data file found at {rawDataCsvPath}. Skipping download.");
            }

            // STEP 2: Feature Engineering
            Console.WriteLine("\nStarting Step 2: Feature Engineering...");
            var rawData = dataHandler.LoadFromCsv<MarketBar>(rawDataCsvPath);
            var featuredData = featureEngineer.GenerateFeaturesAndLabels(rawData);
            dataHandler.SaveToCsv(featuredData, featureDataCsvPath);
            Console.WriteLine($"Feature engineering complete. Saved {featuredData.Count} data points to {featureDataCsvPath}");

            if (!featuredData.Any())
            {
                Console.WriteLine("No feature data was generated. Cannot train model. Exiting.");
                return;
            }

            // --- EXPERIMENT: PERFECT 1:1:1 BALANCING ---
            // The goal is to create a training set with an equal number of each class.
            var random = new Random();
            var buys = featuredData.Where(d => d.Label == "Buy").ToList();
            var sells = featuredData.Where(d => d.Label == "Sell").ToList();
            var holds = featuredData.Where(d => d.Label == "Hold").ToList();

            // Find the count of the smallest class
            var minCount = Math.Min(buys.Count, Math.Min(sells.Count, holds.Count));

            // If any class has zero samples, we can't proceed with this method.
            if (minCount == 0)
            {
                Console.WriteLine("One of the classes (Buy, Sell, or Hold) has zero samples. Cannot create a balanced dataset. Exiting.");
                return;
            }

            // Take an equal number of random samples from each class
            var undersampledBuys = buys.OrderBy(x => random.Next()).Take(minCount).ToList();
            var undersampledSells = sells.OrderBy(x => random.Next()).Take(minCount).ToList();
            var undersampledHolds = holds.OrderBy(x => random.Next()).Take(minCount).ToList();

            var balancedFeaturedData = new List<ModelInput>();
            balancedFeaturedData.AddRange(undersampledBuys);
            balancedFeaturedData.AddRange(undersampledSells);
            balancedFeaturedData.AddRange(undersampledHolds);

            // Shuffle the balanced dataset
            var shuffledBalancedData = balancedFeaturedData.OrderBy(x => random.Next()).ToList();

            Console.WriteLine($"\nOriginal data counts: Holds={holds.Count}, Buys={buys.Count}, Sells={sells.Count}");
            Console.WriteLine($"Perfectly balanced 1:1:1 training data: {shuffledBalancedData.Count} total rows. ({minCount} of each class)");

            // STEP 3: Model Selection and Training
            Console.WriteLine("\nStarting Step 3: Model Selection and Training...");
            // IMPORTANT: We now train on the balanced data, but test on the original, imbalanced data
            // This gives a true picture of how the model will perform in the real world.
            IDataView balancedDataView = mlContext.Data.LoadFromEnumerable(shuffledBalancedData);
            IDataView originalDataView = mlContext.Data.LoadFromEnumerable(featuredData);

            // We only need a test set from the original data
            var dataSplit = mlContext.Data.TrainTestSplit(originalDataView, testFraction: 0.2);
            IDataView testData = dataSplit.TestSet;

            var pipeline = ModelTrainer.BuildTrainingPipeline(mlContext, balancedDataView);

            Console.WriteLine("Training the model on perfectly balanced data...");
            var model = pipeline.Fit(balancedDataView);
            Console.WriteLine("Model training complete.");

            Console.WriteLine("Evaluating model performance on original (unseen) data...");
            var predictions = model.Transform(testData);
            var metrics = mlContext.MulticlassClassification.Evaluate(predictions, "Label");

            Console.WriteLine($"*************************************************");
            Console.WriteLine($"* MicroAccuracy:    {metrics.MicroAccuracy:P2}");
            Console.WriteLine($"* MacroAccuracy:    {metrics.MacroAccuracy:P2}");
            Console.WriteLine($"* LogLoss:          {metrics.LogLoss:0.##}");
            Console.WriteLine($"*************************************************");
            Console.WriteLine(metrics.ConfusionMatrix.GetFormattedConfusionTable());

            Console.WriteLine("Saving the trained model...");
            mlContext.Model.Save(model, originalDataView.Schema, modelPath);
            Console.WriteLine($"Model saved to {modelPath}");

            Console.WriteLine("\n--- Process Complete ---");
        }
    }
}


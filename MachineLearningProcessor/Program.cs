// Updated MachineLearningProcessor/Program.cs (with dynamic 20% holdout)
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
            // Dynamically set endDate to end of the year (Dec 31 of the current year)
            int currentYear = DateTime.UtcNow.Year;
            var endDate = $"{currentYear}-12-31";

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
            var polygonService = new PolygonApiService(polygonApiKey, ticker);
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
            List<ModelInput> featuredData;
            if (!File.Exists(featureDataCsvPath))
            {
                Console.WriteLine("Generating features and labels...");
                var rawData = dataHandler.LoadFromCsv<MarketBar>(rawDataCsvPath);
                featuredData = featureEngineer.GenerateFeaturesAndLabels(rawData);
                dataHandler.SaveToCsv(featuredData, featureDataCsvPath);
                Console.WriteLine($"Successfully generated and saved {featuredData.Count} featured data points to {featureDataCsvPath}");
            }
            else
            {
                Console.WriteLine($"Featured data file found at {featureDataCsvPath}. Skipping feature engineering.");
                featuredData = dataHandler.LoadFromCsv<ModelInput>(featureDataCsvPath);
            }

            // Sort by timestamp for chronological splitting
            featuredData = featuredData.OrderBy(d => d.Timestamp).ToList();

            // Dynamically hold out the last 20% for OOS (no matter when run)
            int totalSamples = featuredData.Count;
            int trainSize = (int)(totalSamples * 0.8);
            var trainFeaturedData = featuredData.Take(trainSize).ToList();
            var oosFeaturedData = featuredData.Skip(trainSize).ToList();

            Console.WriteLine($"\nTotal data: {totalSamples} samples.");
            Console.WriteLine($"Training data: {trainFeaturedData.Count} samples (first 80%).");
            Console.WriteLine($"OOS holdout: {oosFeaturedData.Count} samples (last 20%).");

            // Balance the training dataset using undersampling (only on trainFeaturedData)
            var random = new Random(0); // For reproducibility
            var buys = trainFeaturedData.Where(d => d.Label == "Buy").ToList();
            var sells = trainFeaturedData.Where(d => d.Label == "Sell").ToList();
            var holds = trainFeaturedData.Where(d => d.Label == "Hold").ToList();

            // Find the count of the smallest class in training data
            var minCount = Math.Min(buys.Count, Math.Min(sells.Count, holds.Count));

            // If any class has zero samples, we can't proceed with this method.
            if (minCount == 0)
            {
                Console.WriteLine("One of the classes (Buy, Sell, or Hold) has zero samples in training data. Cannot create a balanced dataset. Exiting.");
                return;
            }

            // Take an equal number of random samples from each class
            var undersampledBuys = buys.OrderBy(x => random.Next()).Take(minCount).ToList();
            var undersampledSells = sells.OrderBy(x => random.Next()).Take(minCount).ToList();
            var undersampledHolds = holds.OrderBy(x => random.Next()).Take(minCount).ToList();

            var balancedTrainData = undersampledBuys.Concat(undersampledSells).Concat(undersampledHolds).OrderBy(x => x.Timestamp).ToList(); // Sort by time after balancing

            Console.WriteLine($"\nTraining data counts: Holds={holds.Count}, Buys={buys.Count}, Sells={sells.Count}");
            Console.WriteLine($"Perfectly balanced 1:1:1 training data: {balancedTrainData.Count} total rows. ({minCount} of each class)");

            // STEP 3: Hyperparameter Tuning with Time-Series Cross-Validation (on balanced training data)
            Console.WriteLine("\nStarting Step 3: Hyperparameter Tuning with Time-Series CV...");

            // Define grid search parameters for LightGBM
            var numLeavesOptions = new[] { 30, 50, 100 };
            var numIterationsOptions = new[] { 100, 200, 500 };

            ITransformer bestModel = null;
            double bestLogLoss = double.MaxValue;
            int bestNumLeaves = 0;
            int bestNumIterations = 0;

            // Time-series CV: Split into 5 folds chronologically (80% train, 20% val per fold)
            int numFolds = 5;
            int foldSize = balancedTrainData.Count / numFolds;

            for (int leavesIdx = 0; leavesIdx < numLeavesOptions.Length; leavesIdx++)
            {
                int numLeaves = numLeavesOptions[leavesIdx];
                for (int itersIdx = 0; itersIdx < numIterationsOptions.Length; itersIdx++)
                {
                    int numIterations = numIterationsOptions[itersIdx];
                    double avgLogLoss = 0;

                    for (int fold = 0; fold < numFolds; fold++)
                    {
                        // Chronological split: Earlier data for train, later for val
                        int trainEnd = (fold + 4) * foldSize; // 80% train
                        var trainList = balancedTrainData.Take(trainEnd).ToList();
                        var valList = balancedTrainData.Skip(trainEnd).Take(foldSize).ToList();

                        if (trainList.Count == 0 || valList.Count == 0) continue;

                        IDataView trainDataView = mlContext.Data.LoadFromEnumerable(trainList);
                        IDataView valDataView = mlContext.Data.LoadFromEnumerable(valList);

                        var pipeline = ModelTrainer.BuildTrainingPipeline(mlContext, trainDataView, numLeaves, numIterations);

                        var model = pipeline.Fit(trainDataView);
                        var predictions = model.Transform(valDataView);
                        var metrics = mlContext.MulticlassClassification.Evaluate(predictions, "Label");

                        avgLogLoss += metrics.LogLoss;
                    }

                    avgLogLoss /= numFolds;
                    Console.WriteLine($"Params: Leaves={numLeaves}, Iterations={numIterations} | Avg LogLoss: {avgLogLoss:0.##}");

                    if (avgLogLoss < bestLogLoss)
                    {
                        bestLogLoss = avgLogLoss;
                        bestNumLeaves = numLeaves;
                        bestNumIterations = numIterations;
                    }
                }
            }

            Console.WriteLine($"\nBest Params: Leaves={bestNumLeaves}, Iterations={bestNumIterations} | Best Avg LogLoss: {bestLogLoss:0.##}");

            // Train final model on all balanced training data with best params
            IDataView balancedTrainDataView = mlContext.Data.LoadFromEnumerable(balancedTrainData);
            var finalPipeline = ModelTrainer.BuildTrainingPipeline(mlContext, balancedTrainDataView, bestNumLeaves, bestNumIterations);
            bestModel = finalPipeline.Fit(balancedTrainDataView);

            // Evaluate on balanced holdout (last 20% of training data - internal val)
            int trainHoldoutStart = (int)(balancedTrainData.Count * 0.8);
            var balancedTestList = balancedTrainData.Skip(trainHoldoutStart).ToList();
            IDataView balancedTestData = mlContext.Data.LoadFromEnumerable(balancedTestList);
            var balancedPredictions = bestModel.Transform(balancedTestData);
            var balancedMetrics = mlContext.MulticlassClassification.Evaluate(balancedPredictions, "Label");

            Console.WriteLine("\nEvaluating model performance on balanced (unseen internal) data...");
            Console.WriteLine($"* MicroAccuracy:    {balancedMetrics.MicroAccuracy:P2}");
            Console.WriteLine($"* MacroAccuracy:    {balancedMetrics.MacroAccuracy:P2}");
            Console.WriteLine($"* LogLoss:          {balancedMetrics.LogLoss:0.##}");
            Console.WriteLine(balancedMetrics.ConfusionMatrix.GetFormattedConfusionTable());

            // Evaluate on true OOS (held-out last 20% of full data)
            IDataView oosDataView = mlContext.Data.LoadFromEnumerable(oosFeaturedData);
            var oosPredictions = bestModel.Transform(oosDataView);
            var oosMetrics = mlContext.MulticlassClassification.Evaluate(oosPredictions, "Label");

            Console.WriteLine("\nEvaluation on true OOS (held-out imbalanced) test set:");
            Console.WriteLine($"* MicroAccuracy:    {oosMetrics.MicroAccuracy:P2}");
            Console.WriteLine($"* MacroAccuracy:    {oosMetrics.MacroAccuracy:P2}");
            Console.WriteLine($"* LogLoss:          {oosMetrics.LogLoss:0.##}");
            Console.WriteLine(oosMetrics.ConfusionMatrix.GetFormattedConfusionTable());

            Console.WriteLine("Saving the trained model...");
            mlContext.Model.Save(bestModel, balancedTrainDataView.Schema, modelPath);
            Console.WriteLine($"Model saved to {modelPath}");

            Console.WriteLine("\n--- Process Complete ---");
        }
    }
}
// Updated MachineLearningProcessor/ModelTrainer.cs (with params for tuning)
using DataModels;
using Microsoft.ML;
using Microsoft.ML.Trainers.LightGbm;

namespace MachineLearningProcessor
{
    /// <summary>
    /// Contains the logic for building the ML.NET training pipeline.
    /// </summary>
    public static class ModelTrainer
    {
        public static IEstimator<ITransformer> BuildTrainingPipeline(MLContext mlContext, IDataView trainData, int numLeaves = 50, int numIterations = 200)
        {
            // Define the training pipeline
            var pipeline = mlContext.Transforms.Conversion.MapValueToKey("Label")
                // Concatenate all feature columns into a single "Features" column
                .Append(mlContext.Transforms.Concatenate("Features",
                    nameof(ModelInput.RSI),
                    nameof(ModelInput.StochasticOscillator),
                    nameof(ModelInput.MACD),
                    nameof(ModelInput.PriceChangePercentage),
                    nameof(ModelInput.VolumeSpike),
                    nameof(ModelInput.VwapCloseDifference),
                    nameof(ModelInput.TransactionSpike),
                    nameof(ModelInput.PriceSmaDifference),
                    nameof(ModelInput.TimeOfDay),
                    nameof(ModelInput.ATR),
                    nameof(ModelInput.BollingerPercentB), // New feature
                    nameof(ModelInput.RSI_Lag1) // New lagged feature
                ))
                // Normalize features to prevent any from dominating (e.g., ATR or VolumeSpike)
                .Append(mlContext.Transforms.NormalizeMinMax("Features"))
                // Use LightGBM with tunable parameters
                .Append(mlContext.MulticlassClassification.Trainers.LightGbm(new LightGbmMulticlassTrainer.Options
                {
                    NumberOfLeaves = numLeaves,
                    NumberOfIterations = numIterations,
                    MinimumExampleCountPerLeaf = 20,
                    LearningRate = 0.1,
                    LabelColumnName = "Label",
                    FeatureColumnName = "Features",
                    Booster = new GradientBooster.Options { SubsampleFraction = 0.8, FeatureFraction = 0.8 },
                    MaximumBinCountPerFeature = 256
                }))
                // Convert the predicted label back to its original value (e.g., "Buy", "Sell", "Hold")
                .Append(mlContext.Transforms.Conversion.MapKeyToValue("PredictedLabel"));

            return pipeline;
        }
    }
}
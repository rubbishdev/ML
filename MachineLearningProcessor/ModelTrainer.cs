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
        public static IEstimator<ITransformer> BuildTrainingPipeline(MLContext mlContext, IDataView trainData)
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
                    nameof(ModelInput.TimeOfDay)
                ))
                // EXPERIMENT: Switching to a more powerful, tree-based algorithm (LightGBM)
                .Append(mlContext.MulticlassClassification.Trainers.LightGbm(new LightGbmMulticlassTrainer.Options
                {
                    NumberOfLeaves = 50,
                    NumberOfIterations = 200,
                    MinimumExampleCountPerLeaf = 20,
                    LearningRate = 0.1,
                    LabelColumnName = "Label",
                    FeatureColumnName = "Features"
                }))
                // Convert the predicted label back to its original value (e.g., "Buy", "Sell", "Hold")
                .Append(mlContext.Transforms.Conversion.MapKeyToValue("PredictedLabel"));

            return pipeline;
        }
    }
}


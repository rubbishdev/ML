// Updated DataModels/ModelInput.cs (with new features)
using Microsoft.ML.Data;
using System;

namespace DataModels
{
    /// <summary>
    /// Represents the input data for the machine learning model.
    /// This includes the raw market data plus the engineered features.
    /// </summary>
    public class ModelInput
    {
        // Raw Data - Note: Changed from decimal to float for ML.NET compatibility
        [LoadColumn(0)] public float Open { get; set; }
        [LoadColumn(1)] public float High { get; set; }
        [LoadColumn(2)] public float Low { get; set; }
        [LoadColumn(3)] public float Close { get; set; }
        [LoadColumn(4)] public float Volume { get; set; }
        [LoadColumn(5)] public long Timestamp { get; set; }
        // Engineered Features
        [LoadColumn(6)] public float RSI { get; set; }
        [LoadColumn(7)] public float StochasticOscillator { get; set; }
        [LoadColumn(8)] public float MACD { get; set; }
        [LoadColumn(9)] public float PriceChangePercentage { get; set; }
        [LoadColumn(10)] public float VolumeSpike { get; set; }
        [LoadColumn(11)] public float VwapCloseDifference { get; set; }
        [LoadColumn(12)] public float TransactionSpike { get; set; }
        [LoadColumn(13)] public float PriceSmaDifference { get; set; }
        [LoadColumn(14)] public float TimeOfDay { get; set; }
        [LoadColumn(15)] public float ATR { get; set; }
        [LoadColumn(16)] public float BollingerPercentB { get; set; } // New
        [LoadColumn(17)] public float RSI_Lag1 { get; set; } // New
        // The value we want to predict
        [LoadColumn(18)] public string Label { get; set; }
    }

    /// <summary>
    /// Represents the output of the trained model, i.e., the prediction.
    /// </summary>
    public class ModelOutput
    {
        [ColumnName("PredictedLabel")]
        public string PredictedLabel { get; set; }
        [ColumnName("Score")]
        public float[] Score { get; set; }
    }

    /// <summary>
    /// Represents a single trade executed during a backtest simulation.
    /// </summary>
    public class TradeLog
    {
        public DateTime EntryTime { get; set; }
        public string Signal { get; set; } // Buy or Sell
        public decimal EntryPrice { get; set; }
        public decimal ExitPrice { get; set; }
        public decimal ProfitLoss { get; set; }
    }
}
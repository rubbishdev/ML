using Microsoft.ML.Data;

namespace DataModels
{
    /// <summary>
    /// Extended ModelInput with a Weight column for sample weighting.
    /// </summary>
    public class WeightedModelInput : ModelInput
    {
        public float Weight { get; set; }
    }
}
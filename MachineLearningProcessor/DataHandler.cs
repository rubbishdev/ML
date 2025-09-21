using CsvHelper;
using DataModels;
using System.Collections.Generic;
using System.Formats.Asn1;
using System.Globalization;
using System.IO;
using System.Linq;

namespace MachineLearningProcessor
{
    /// <summary>
    /// Handles reading and writing data to local CSV files for caching.
    /// </summary>
    public class DataHandler
    {
        /// <summary>
        /// Saves a list of data records to a CSV file.
        /// </summary>
        public void SaveToCsv<T>(List<T> records, string filePath)
        {
            using (var writer = new StreamWriter(filePath))
            using (var csv = new CsvWriter(writer, CultureInfo.InvariantCulture))
            {
                csv.WriteRecords(records);
            }
        }

        /// <summary>
        /// Loads data records from a CSV file.
        /// </summary>
        public List<T> LoadFromCsv<T>(string filePath)
        {
            using (var reader = new StreamReader(filePath))
            using (var csv = new CsvReader(reader, CultureInfo.InvariantCulture))
            {
                return csv.GetRecords<T>().ToList();
            }
        }
    }
}

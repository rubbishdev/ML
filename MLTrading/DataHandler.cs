using CsvHelper;
using DataModels;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace MLTrading
{
    public class DataHandler
    {
        public void SaveToCsv<T>(List<T> records, string filePath)
        {
            using (var writer = new StreamWriter(filePath))
            using (var csv = new CsvWriter(writer, CultureInfo.InvariantCulture))
            {
                csv.WriteRecords(records);
            }
        }

        public List<T> LoadFromCsv<T>(string filePath)
        {
            if (!File.Exists(filePath)) return new List<T>();
            using (var reader = new StreamReader(filePath))
            using (var csv = new CsvReader(reader, CultureInfo.InvariantCulture))
            {
                return csv.GetRecords<T>().ToList();
            }
        }

        public void SaveStrategyParameters(Dictionary<string, object> parameters, string filePath)
        {
            var lines = parameters.Select(p => $"{p.Key},{p.Value}");
            File.WriteAllLines(filePath, lines);
        }

        public Dictionary<string, object> LoadStrategyParameters(string filePath)
        {
            if (!File.Exists(filePath)) return new Dictionary<string, object>();
            var result = new Dictionary<string, object>();
            var lines = File.ReadAllLines(filePath);
            foreach (var line in lines)
            {
                var parts = line.Split(',');
                if (parts.Length == 2)
                {
                    if (int.TryParse(parts[1], out int intValue))
                        result[parts[0]] = intValue;
                    else if (decimal.TryParse(parts[1], out decimal decValue))
                        result[parts[0]] = decValue;
                    else if (bool.TryParse(parts[1], out bool boolValue))
                        result[parts[0]] = boolValue;
                    else if (double.TryParse(parts[1], out double doubleValue))
                        result[parts[0]] = doubleValue;
                    else
                        result[parts[0]] = parts[1];
                }
            }
            return result;
        }
    }
}
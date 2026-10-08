namespace ProfControl
{
    internal static class DistanceExporter
    {
        internal const string Api = "ProfControl.Items.MapAnalysisInfo.Instance.GetKnownDistance(from.CurrentIdx,to.CurrentIdx)";
        internal static long Export(string folder, SimulationSnapshot data, out long failures)
        {
            SimulationDataExporter.Log("Searching distance API...");
            SimulationDataExporter.Log("Distance API: " + Api);
            // API.GetDistance(IStation,IStation) calls this unless Manhattan mode is enabled.
            // Direct call preserves the global mode and always uses the map graph/cache.
            var graph = MapAnalysisInfo.Instance;
            failures = 0;
            long pairs = 0;
            using var writer = SimulationCsv.Open(folder, "distance_matrix.csv");
            writer.WriteLine("FromStation,ToStation,Distance");
            foreach (var from in data.Stations)
                foreach (var to in data.Stations)
                {
                    double distance = double.NaN;
                    try
                    {
                        if (from == to) distance = 0;
                        else
                        {
                            // Prevent stale/uninitialized CurrentIdx from silently addressing another station.
                            if (from.CurrentIdx < 0 || to.CurrentIdx < 0 ||
                                graph[from.CurrentIdx] != from || graph[to.CurrentIdx] != to)
                                throw new InvalidOperationException("Map station indexes are not initialized consistently.");
                            distance = graph.GetKnownDistance(from.CurrentIdx, to.CurrentIdx);
                        }
                        if (double.IsInfinity(distance) || distance == double.MaxValue || distance < 0) distance = double.NaN;
                    }
                    catch (Exception ex) { SimulationDataExporter.Log("Warning: distance " + from.Mark + " -> " + to.Mark + ": " + ex.Message); }
                    if (double.IsNaN(distance))
                    {
                        failures++;
                        SimulationDataExporter.Log("Warning: unavailable distance " + from.Mark + " -> " + to.Mark + "; NaN exported.");
                    }
                    SimulationCsv.Row(writer, from.Mark, to.Mark, distance.ToString("R", SimulationCsv.Culture));
                    pairs++;
                }
            return pairs;
        }
    }
}

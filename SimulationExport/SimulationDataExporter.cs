namespace ProfControl
{
    public static class SimulationDataExporter
    {
        internal static void Log(string message)
        {
            try { Tools.trace("[SimulationExport] " + message); } catch { /* Logging must not interrupt the host. */ }
        }
        public static void ExportAll()
        {
            try
            {
                Log("Start");
                var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                if (string.IsNullOrWhiteSpace(desktop)) throw new InvalidOperationException("Windows DesktopDirectory is unavailable.");
                var folder = Path.Combine(desktop, "AGV_Simulation");
                Directory.CreateDirectory(folder);
                Log("Folder: " + folder);
                var data = SimulationSnapshot.Read();
                Log("Stations: " + data.Stations.Count);
                Log("Pockets: " + data.Pockets.Count);
                StationExporter.Export(folder, data);
                LaneExporter.Export(folder, data);
                var pairs = DistanceExporter.Export(folder, data, out var failures);
                Log("Distance pairs: " + pairs);
                using (var writer = SimulationCsv.Open(folder, "metadata.txt"))
                {
                    writer.WriteLine("ExportTime=" + DateTimeOffset.Now.ToString("O", SimulationCsv.Culture));
                    writer.WriteLine("StationCount=" + data.Stations.Count.ToString(SimulationCsv.Culture));
                    writer.WriteLine("PocketCount=" + data.Pockets.Count.ToString(SimulationCsv.Culture));
                    writer.WriteLine("DistancePairCount=" + pairs.ToString(SimulationCsv.Culture));
                    writer.WriteLine("DistanceFailureCount=" + failures.ToString(SimulationCsv.Culture));
                    writer.WriteLine("DistanceUnit=SystemScalePixel");
                    writer.WriteLine("DistanceType=NavigationShortestPath");
                    writer.WriteLine("DistanceApi=" + DistanceExporter.Api);
                    writer.WriteLine("DistanceSemantics=ProfControl navigation cache or Dijkstra over directed map paths; not vehicle-specific GoTo feasibility");
                    writer.WriteLine("DistancePrecision=MapAnalysisInfo.GetNearPointDistance casts each PathLength to integer");
                    writer.WriteLine("DistanceFailurePolicy=NaN; includes exceptions, unreachable, invalid indexes and double.MaxValue");
                    writer.WriteLine("MetersConversion=Unconfirmed; do not divide these pixel distances by speed in meters/second");
                    writer.WriteLine("OrderStationsDirection=DeepToEntrance");
                    writer.WriteLine("OrderStationsDirectionSource=ProfControl.xml: IPocketLines.OrderStations");
                    writer.WriteLine("SequenceBase=0");
                    writer.WriteLine("PocketEntranceSource=入口 subcategory membership only; pocket.Gate is not used");
                    writer.WriteLine("PocketKey=First OrderStations StationMark; duplicate first marks get #2,#3 suffixes");
                    writer.WriteLine("PocketNameSource=Complete two-digit subcategory matching one physical pocket; ambiguous names blank");
                    writer.WriteLine("StationRows=One row per station/pocket membership; unassigned stations have one row");
                    writer.WriteLine("CoordinatesApi=IStation.View.KeyPoint; system pixel coordinates, not meters");
                    writer.WriteLine("NeighborApiAvailable=True");
                    writer.WriteLine("NeighborApi=MapAnalysisInfo.Instance.GetNearPointDistance(null,station.CurrentIdx)");
                    writer.WriteLine("NeighborApiMeaning=Dictionary of outgoing adjacent station CurrentIdx to integer PathLength; respects path direction and station enablement");
                    writer.WriteLine("ExportStatus=" + (failures == 0 ? "Complete" : "CompletedWithWarnings"));
                }
                Log("Finished");
            }
            catch (Exception ex) { Log("Failed: " + ex); }
        }
    }
}

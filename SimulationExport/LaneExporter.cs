namespace ProfControl
{
    internal static class LaneExporter
    {
        internal static void Export(string folder, SimulationSnapshot data)
        {
            using var writer = SimulationCsv.Open(folder, "lanes.csv");
            writer.WriteLine("PocketKey,PocketName,Sequence,StationMark,StationType");
            foreach (var pocket in data.Pockets)
                for (int i = 0; i < pocket.Stations.Count; i++)
                {
                    var station = pocket.Stations[i];
                    var type = pocket.IsEntrance(station, data) ? "Entrance" : data.CargoSlots.Contains(station.Mark) ? "Slot" : "Unknown";
                    SimulationCsv.Row(writer, pocket.Key, pocket.Name, i, station.Mark, type);
                }
        }
    }
}

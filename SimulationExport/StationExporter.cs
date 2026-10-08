namespace ProfControl
{
    internal static class StationExporter
    {
        internal static void Export(string folder, SimulationSnapshot data)
        {
            using var writer = SimulationCsv.Open(folder, "stations.csv");
            writer.WriteLine("StationMark,PocketKey,PocketName,OrderInPocket,IsPocketEntrance,IsCargoSlot,X,Y");
            foreach (var station in data.Stations)
            {
                object x = null, y = null;
                try { if (station.View != null) { x = station.View.KeyPoint.X; y = station.View.KeyPoint.Y; } }
                catch (Exception ex) { SimulationDataExporter.Log("Warning: coordinates " + station.Mark + ": " + ex.Message); }
                var found = false;
                foreach (var pocket in data.Pockets)
                    for (int i = 0; i < pocket.Stations.Count; i++)
                        if (pocket.Stations[i].Mark == station.Mark)
                        {
                            found = true;
                            SimulationCsv.Row(writer, station.Mark, pocket.Key, pocket.Name, i,
                                pocket.IsEntrance(station, data) ? 1 : 0, data.CargoSlots.Contains(station.Mark) ? 1 : 0, x, y);
                        }
                if (!found) SimulationCsv.Row(writer, station.Mark, null, null, null,
                    data.Entrances.Contains(station.Mark) ? 1 : 0, data.CargoSlots.Contains(station.Mark) ? 1 : 0, x, y);
            }
        }
    }
}

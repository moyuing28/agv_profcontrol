using System.Globalization;
namespace ProfControl
{
    internal sealed class SimulationSnapshot
    {
        internal List<IStation> Stations = new List<IStation>();
        internal List<SimulationPocket> Pockets = new List<SimulationPocket>();
        internal HashSet<string> CargoSlots = new HashSet<string>();
        internal HashSet<string> Entrances = new HashSet<string>();
        internal static SimulationSnapshot Read()
        {
            var data = new SimulationSnapshot();
            data.Stations = API.Stations.OfType<IStation>().ToList();
            if (data.Stations.Count == 0) throw new InvalidOperationException("Map has no stations; export aborted.");
            if (data.Stations.Any(s => string.IsNullOrEmpty(s.Mark)) ||
                data.Stations.GroupBy(s => s.Mark).Any(g => g.Count() > 1))
                throw new InvalidOperationException("StationMark must be nonempty and globally unique for this CSV schema.");
            var garage = SubCategoryCollection.Instance["车库位"] as ISubCategory;
            var entrances = SubCategoryCollection.Instance["入口"] as ISubCategory;
            if (entrances != null) data.Entrances.UnionWith(entrances.Children.OfType<IStation>().Select(s => s.Mark));
            if (garage != null) data.CargoSlots.UnionWith(garage.Children.OfType<IStation>()
                .Select(s => s.Mark).Where(m => !data.Entrances.Contains(m)));
            var seen = new HashSet<IPocketLines>();
            var keys = new HashSet<string>();
            foreach (var station in data.Stations)
            {
                try
                {
                    foreach (var pocket in station.BelongPocketLines)
                    {
                        if (!seen.Add(pocket)) continue;
                        try
                        {
                            var ordered = pocket.OrderStations.ToList();
                            if (ordered.Count == 0 || ordered.Any(s => s == null))
                                throw new InvalidOperationException("Pocket has empty or null OrderStations.");
                            var first = ordered[0].Mark;
                            if (string.IsNullOrEmpty(first)) throw new InvalidOperationException("Pocket first station has no Mark.");
                            var key = first;
                            for (int suffix = 2; !keys.Add(key); suffix++) key = first + "#" + suffix;
                            if (key != first) SimulationDataExporter.Log("Warning: duplicate pocket first Mark " + first + "; export key=" + key);
                            var row = new SimulationPocket { Key = key, Stations = ordered };
                            var slots = ordered.Where(s => data.CargoSlots.Contains(s.Mark)).ToList();
                            var names = new List<string>();
                            foreach (var category in SubCategoryCollection.Instance.SubCategorys)
                            {
                                // Same complete-single-physical-pocket rule as MapConfig; read-only and no cache binding.
                                if (!Regex.IsMatch(category.Name ?? "", @"^[0-9]{2}$")) continue;
                                var children = category.Children.OfType<IStation>().Where(s => data.CargoSlots.Contains(s.Mark)).ToList();
                                if (children.Count > 0 && slots.Count > 0 &&
                                    children.SelectMany(s => s.BelongPocketLines).Distinct().SequenceEqual(new[] { pocket }) &&
                                    new HashSet<string>(children.Select(s => s.Mark)).SetEquals(slots.Select(s => s.Mark)))
                                    names.Add(category.Name);
                            }
                            if (names.Count == 1) row.Name = names[0];
                            else if (names.Count > 1) SimulationDataExporter.Log("Warning: pocket " + key + " has multiple logical names; PocketName left blank.");
                            data.Pockets.Add(row);
                        }
                        catch (Exception ex) { SimulationDataExporter.Log("Warning: pocket at " + station.Mark + ": " + ex.Message); }
                    }
                }
                catch (Exception ex) { SimulationDataExporter.Log("Warning: station " + station.Mark + ": " + ex.Message); }
            }
            return data;
        }
    }
    internal sealed class SimulationPocket
    {
        internal string Key, Name;
        internal List<IStation> Stations;
        internal bool IsEntrance(IStation station, SimulationSnapshot snapshot) =>
            snapshot.Entrances.Contains(station.Mark);
    }
    internal static class SimulationCsv
    {
        internal static readonly CultureInfo Culture = CultureInfo.InvariantCulture;
        internal static StreamWriter Open(string folder, string name) => new StreamWriter(Path.Combine(folder, name), false, new UTF8Encoding(false));
        internal static string Cell(object value)
        {
            string text = value is IFormattable number ? number.ToString(null, Culture) : value?.ToString() ?? "";
            return text.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;
        }
        internal static void Row(StreamWriter writer, params object[] cells) => writer.WriteLine(string.Join(",", cells.Select(Cell)));
    }
}

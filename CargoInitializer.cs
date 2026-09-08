namespace ProfControl
{
    public partial class SampleProject
    {

        void GenerateCargo(List<IStation> targetStations)
        {
            API.RandomGenerateCargoInfo(4);
            API.RandomGenerateContainerInfo(1, true, false, false);

            var validStations = targetStations
                .Where(s => s.CargoAreas != null && s.CargoAreas.Count > 0)
                .ToList();

            if (validStations.Count == 0) return;

            API.ClearCargos(validStations.SelectMany(s => s.CargoAreas).Distinct().ToList());

            foreach (var sta in validStations)
            {
                if (!sta.IsEmpty()) continue;

                var area = sta.CargoAreas.First();
                var container = area.ContainerPlace("托盘_0");
                if (container != null)
                {
                    container.CargoPlace("货物_1", 1);
                }
            }
            Tools.trace($"GenerateCargo");
        }

        void ClearCargo(List<IStation> dropStations)
        {
            var validStations = dropStations
                .Where(s => s.CargoAreas != null && s.CargoAreas.Count > 0)
                .ToList();

            if (validStations.Count == 0) return;

            API.ClearCargos(validStations.SelectMany(s => s.CargoAreas).Distinct().ToList());
        }

    }
}

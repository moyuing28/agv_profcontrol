namespace ProfControl
{
    public partial class SampleProject
    {
        /// <summary>
        /// 获取前端可选站点。
        /// hasCargo=true 返回有货取货站点；hasCargo=false 返回每条巷道最深的可放空站点。
        /// OrderStations 按现有业务逻辑视为“巷道深处 -> 巷道口”。
        /// </summary>
        public List<SelectableStationInfo> GetSelectableStations(bool hasCargo)
        {
            var result = new List<SelectableStationInfo>();
            var garageSub = SubCategoryCollection.Instance[GarageArea] as ISubCategory;
            if (garageSub == null)
            {
                Tools.trace("GetSelectableStations: 找不到车库位分类");
                return result;
            }

            var entranceSub = SubCategoryCollection.Instance["入口"] as ISubCategory;
            var entranceMarks = entranceSub == null
                ? new HashSet<string>()
                : new HashSet<string>(entranceSub.Children.Cast<IStation>().Select(s => s.Mark));

            var garageMarks = new HashSet<string>(garageSub.Children
                .Cast<IStation>()
                .Select(s => s.Mark));

            foreach (var pocketName in 巷道子分类名称)
            {
                var pocketSub = SubCategoryCollection.Instance[pocketName] as ISubCategory;
                if (pocketSub == null)
                {
                    Tools.trace($"GetSelectableStations: 找不到巷道子分类 {pocketName}");
                    continue;
                }

                var pocketMarks = new HashSet<string>(pocketSub.Children
                    .Cast<IStation>()
                    .Select(s => s.Mark));

                var pockets = pocketSub.Children
                    .Cast<IStation>()
                    .SelectMany(s => s.BelongPocketLines)
                    .Distinct()
                    .ToList();

                foreach (var pocket in pockets)
                {
                    var stations = pocket.OrderStations
                        .Where(s => garageMarks.Contains(s.Mark) &&
                                    pocketMarks.Contains(s.Mark) &&
                                    !entranceMarks.Contains(s.Mark))
                        .ToList();

                    if (hasCargo)
                    {
                        foreach (var station in stations.Where(s => !s.IsEmpty()))
                        {
                            result.Add(new SelectableStationInfo
                            {
                                StationMark = station.Mark,
                                PocketName = pocketName,
                                HasCargo = true
                            });
                        }

                        continue;
                    }

                    // hasCargo=false 时，每条巷道只返回一个卸货点。
                    var deepestEmptyStation = stations.FirstOrDefault(s => s.IsEmpty());
                    if (deepestEmptyStation != null)
                    {
                        result.Add(new SelectableStationInfo
                        {
                            StationMark = deepestEmptyStation.Mark,
                            PocketName = pocketName,
                            HasCargo = false
                        });
                    }
                }
            }

            return result;
        }
    }
}

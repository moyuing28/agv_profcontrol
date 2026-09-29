namespace ProfControl
{
    public partial class SampleProject
    {
        // 启动时构建，调度期间只读；不保存货物、车辆、锁或容量等动态状态。
        class CategoryConfig
        {
            public List<IStation> Children;
            public HashSet<string> Marks;
        }

        class CargoPocketConfig
        {
            public string Key;
            public string Entrance;
            public string Error;
            public List<IStation> Stations;
            public List<IStation> Slots;
        }

        readonly Dictionary<string, CategoryConfig> mapCategories = new Dictionary<string, CategoryConfig>();
        readonly Dictionary<string, CargoPocketConfig> cargoPockets = new Dictionary<string, CargoPocketConfig>();
        readonly Dictionary<string, string> cargoPocketKeysByName = new Dictionary<string, string>();
        readonly Dictionary<string, string> cargoPocketErrorsByName = new Dictionary<string, string>();
        readonly Dictionary<string, string> stationCargoAreas = new Dictionary<string, string>();
        readonly Dictionary<string, string> stationCargoAreaErrors = new Dictionary<string, string>();
        readonly Dictionary<string, string> stationPocketKeys = new Dictionary<string, string>();
        readonly HashSet<string> emptyCategoryMarks = new HashSet<string>();
        bool mapConfigurationLoaded;

        CategoryConfig GetMapCategory(string name)
        {
            return name != null && mapCategories.TryGetValue(name, out var category) ? category : null;
        }

        HashSet<string> CategoryMarks(string name)
        {
            return GetMapCategory(name)?.Marks ?? emptyCategoryMarks;
        }

        CargoPocketConfig GetCargoPocket(string key)
        {
            return key != null && cargoPockets.TryGetValue(key, out var pocket) ? pocket : null;
        }

        string GetStationPocketKey(string mark)
        {
            return mark != null && stationPocketKeys.TryGetValue(mark, out var key) ? key : null;
        }

        void LoadMapConfiguration()
        {
            if (mapConfigurationLoaded) return;
            var errors = new List<string>();
            var names = new[] { GarageArea, "入口", "agv停放点" }
                .Concat(GarageDirectionAreas).Concat(巷道子分类名称).Distinct();
            foreach (var name in names)
            {
                var category = SubCategoryCollection.Instance[name] as ISubCategory;
                if (category == null)
                {
                    errors.Add($"找不到子分类 {name}");
                    continue;
                }
                var children = category.Children.OfType<IStation>().ToList();
                mapCategories.Add(name, new CategoryConfig
                {
                    Children = children,
                    Marks = new HashSet<string>(children.Select(s => s.Mark))
                });
            }

            var garage = GetMapCategory(GarageArea);
            var gates = CategoryMarks("入口");
            if (garage != null)
            {
                foreach (var station in garage.Children)
                    stationPocketKeys[station.Mark] = station.BelongPocketLines.FirstOrDefault()?.OrderStations.FirstOrDefault()?.Mark;
                foreach (var station in garage.Children.Where(s => !gates.Contains(s.Mark)))
                {
                    var areas = GarageDirectionAreas.Where(a => CategoryMarks(a).Contains(station.Mark)).ToList();
                    if (areas.Count == 1)
                        stationCargoAreas[station.Mark] = areas[0];
                    else
                    {
                        var error = areas.Count == 0 ? "未配置车库位朝向子分类" :
                            $"匹配多个朝向子分类（{string.Join("、", areas)}），请只配置一个朝向";
                        stationCargoAreaErrors[station.Mark] = error;
                        errors.Add($"站点 {station.Mark}：{error}");
                    }
                }

                foreach (var pocket in garage.Children.SelectMany(s => s.BelongPocketLines).Distinct())
                {
                    var stations = pocket.OrderStations.ToList();
                    var key = stations.FirstOrDefault()?.Mark;
                    if (string.IsNullOrEmpty(key))
                    {
                        errors.Add("货物巷道没有站点");
                        continue;
                    }
                    if (cargoPockets.TryGetValue(key, out var existing))
                    {
                        existing.Error = $"多条物理巷道使用同一个 Key {key}";
                        errors.Add(existing.Error);
                        continue;
                    }
                    var entrances = stations.Where(s => gates.Contains(s.Mark)).Select(s => s.Mark).Distinct().ToList();
                    var config = new CargoPocketConfig
                    {
                        Key = key,
                        Stations = stations,
                        Slots = stations.Where(s => garage.Marks.Contains(s.Mark) && !gates.Contains(s.Mark)).ToList(),
                        Entrance = entrances.Count == 1 ? entrances[0] : null,
                        Error = entrances.Count == 1 ? null : $"巷道 {key} 入口不唯一（数量={entrances.Count}）"
                    };
                    if (config.Slots.Count == 0) config.Error = $"巷道 {key} 没有车库货位";
                    if (config.Slots.Select(s => s.Mark).Distinct().Count() != config.Slots.Count)
                        config.Error = $"巷道 {key} 包含重复货位";
                    cargoPockets.Add(key, config);
                    if (config.Error != null) errors.Add(config.Error);
                }
            }

            foreach (var name in 巷道子分类名称.Distinct())
            {
                if (ValidateCargoPocketCategory(name, out var key, out var error))
                {
                    var pocket = GetCargoPocket(key);
                    if (pocket != null && pocket.Error == null)
                    {
                        cargoPocketKeysByName.Add(name, key);
                        continue;
                    }
                    error = pocket?.Error ?? $"找不到巷道 {name} 的地图配置";
                }
                cargoPocketErrorsByName.Add(name, error);
                errors.Add(error);
            }
            mapConfigurationLoaded = true;
            foreach (var error in errors.Distinct()) Tools.trace($"[地图配置] {error}");
            Tools.trace($"[地图配置] 已加载，有效货物巷道分类 {cargoPocketKeysByName.Count} 个，朝向货位 {stationCargoAreas.Count} 个");
        }

        bool TryResolveCargoPocket(string name, out string key, out string error)
        {
            key = null;
            error = null;
            if (!mapConfigurationLoaded) error = "地图配置尚未初始化";
            else if (string.IsNullOrWhiteSpace(name)) error = "巷道子分类名称不能为空";
            else if (cargoPocketKeysByName.TryGetValue(name, out key)) return true;
            else if (!cargoPocketErrorsByName.TryGetValue(name, out error))
                error = $"巷道子分类 {name} 未配置在巷道子分类名称中";
            return false;
        }

        // 仅初始化时验证分类完整性，业务调用直接读取验证结果。
        bool ValidateCargoPocketCategory(string name, out string key, out string error)
        {
            key = null;
            error = null;
            var category = GetMapCategory(name);
            var garage = GetMapCategory(GarageArea);
            var entrances = GetMapCategory("入口");
            if (category == null || garage == null || entrances == null)
            {
                error = $"找不到巷道子分类 {name}，或缺少车库位、入口分类";
                return false;
            }
            var stations = category.Children.Where(s => garage.Marks.Contains(s.Mark) && !entrances.Marks.Contains(s.Mark)).ToList();
            var pockets = stations.SelectMany(s => s.BelongPocketLines).Distinct().ToList();
            if (stations.Count == 0 || pockets.Count != 1)
                error = $"子分类 {name} 必须对应一条明确的货物巷道";
            else if (pockets[0].OrderStations.Any(s => garage.Marks.Contains(s.Mark) && !entrances.Marks.Contains(s.Mark) && !category.Marks.Contains(s.Mark)))
                error = $"子分类 {name} 未包含该巷道的全部车库货位，请检查分类配置";
            else
            {
                key = pockets[0].OrderStations.FirstOrDefault()?.Mark;
                if (string.IsNullOrEmpty(key)) error = $"子分类 {name} 对应的巷道没有站点";
            }
            return error == null;
        }
    }
}

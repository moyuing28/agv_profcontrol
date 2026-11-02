namespace ProfControl
{
    public partial class SampleProject
    {
        //读取 AGV 电量（百分比）；读不到返回 -1，前端显示为「—」
        double GetBattery(dynamic agv)
        {
            try { return Convert.ToDouble(agv.Battery); }
            catch { return -1; }
        }

        //解析巷道名称入参：优先数组字段（如 sourcePocketNames），为空时回退单值字段（sourcePocketName）。
        // 兼容前端单选版本的历史请求体，避免改一次前端就要求后端同步发版。
        List<string> ReadPocketNames(JObject jobj, string arrayField, string singleField)
        {
            var names = new List<string>();
            if (jobj == null) return names;

            if (jobj[arrayField] is JArray arr)
            {
                foreach (var item in arr)
                {
                    var v = item?.ToString().Trim();
                    if (!string.IsNullOrWhiteSpace(v) && !names.Contains(v)) names.Add(v);
                }
                if (names.Count > 0) return names;
            }

            var single = jobj[arrayField]?.ToString() ?? jobj[singleField]?.ToString();
            if (!string.IsNullOrWhiteSpace(single)) names.Add(single.Trim());
            return names;
        }

        //字符串列表 → JArray；null 时返回空数组
        JArray ToJArray(IEnumerable<string> values)
        {
            var arr = new JArray();
            if (values == null) return arr;
            foreach (var v in values) arr.Add(v);
            return arr;
        }

        //站点 Mark -> 巷道子分类名称；用于货物管理页面显示「所属巷道」。
        // 与 /api/pockets 同一套口径：车库位分类 ∩ 该巷道分类，并排除入口站。
        Dictionary<string, string> GetStationPocketMap()
        {
            var map = new Dictionary<string, string>();
            var garageSub = SubCategoryCollection.Instance[GarageArea] as ISubCategory;
            var entranceSub = SubCategoryCollection.Instance["入口"] as ISubCategory;
            var garageMarks = garageSub == null
                ? new HashSet<string>()
                : new HashSet<string>(garageSub.Children.Cast<IStation>().Select(s => s.Mark));
            var entranceMarks = entranceSub == null
                ? new HashSet<string>()
                : new HashSet<string>(entranceSub.Children.Cast<IStation>().Select(s => s.Mark));

            foreach (var pocketName in 巷道子分类名称)
            {
                var pocketSub = SubCategoryCollection.Instance[pocketName] as ISubCategory;
                if (pocketSub == null) continue;

                var pocketMarks = new HashSet<string>(pocketSub.Children.Cast<IStation>().Select(s => s.Mark));
                var pockets = pocketSub.Children.Cast<IStation>()
                    .SelectMany(s => s.BelongPocketLines).Distinct().ToList();

                foreach (var pocket in pockets)
                {
                    foreach (var station in pocket.OrderStations)
                    {
                        if (!garageMarks.Contains(station.Mark)) continue;
                        if (!pocketMarks.Contains(station.Mark)) continue;
                        if (entranceMarks.Contains(station.Mark)) continue;
                        if (!map.ContainsKey(station.Mark)) map[station.Mark] = pocketName;
                    }
                }
            }
            return map;
        }

        //暴露API接口
        void StartHttpApi(){
            API.Http("http://*:8088", app =>
            {
                app.SupportCORS();  // 允许前端跨域访问

                app.MapGet("/api/agvs", async (HttpContext ctx) =>
                {
                    var arr = new JArray();
                    foreach (var agv in API.Agvs)
                    {
                        var faults = GetAgvFaultsJson(agv, agv.Name);
                        arr.Add(new JObject
                        {
                            ["Name"] = agv.Name,
                            ["状态"] = agv.GetValue<string>("状态"),
                            ["任务类型"] = agv.GetValue<string>("任务类型"),
                            ["当前站点"] = agv.API.On,
                            ["空闲"] = agv.IsFree(),
                            ["电量"] = GetBattery(agv),
                            ["故障数"] = faults.Count,
                            ["故障"] = faults
                        });
                    }
                    return Results.Text(arr.ToString());
                });

                //AGV 故障总览（含故障字典），供前端下拉/展示
                app.MapGet("/api/agvFaults", async (HttpContext ctx) =>
                {
                    var agvs = new JArray();
                    foreach (var agv in API.Agvs)
                    {
                        var faults = GetAgvFaultsJson(agv, agv.Name);
                        agvs.Add(new JObject
                        {
                            ["Name"] = agv.Name,
                            ["故障数"] = faults.Count,
                            ["故障"] = faults
                        });
                    }

                    var catalog = new JArray();
                    foreach (var def in AgvFaultCatalog.Values)
                    {
                        catalog.Add(new JObject { ["code"] = def.Code, ["name"] = def.Name, ["level"] = def.Level });
                    }
                    return Results.Text(
                        new JObject { ["agvs"] = agvs, ["catalog"] = catalog }.ToString(),
                        API.ContentTypeJson);
                });

                //上报 AGV 故障：{ agvName, faults: ["0x7001","自定义故障名"], replace: true|false }
                // faults 元素也支持对象形式 { code, name, level }（未知故障按 name 兜底，level 默认 warning）
                app.MapPost("/api/agvFaults", async (HttpContext context) =>
                {
                    JObject jobj = null;
                    try { jobj = await context.AsJObject(); }
                    catch { }

                    string agvName = jobj?["agvName"]?.ToString();
                    if (string.IsNullOrWhiteSpace(agvName))
                    {
                        context.Response.StatusCode = 400;
                        return Results.Text(
                            new JObject { ["message"] = "agvName 不能为空" }.ToString(),
                            API.ContentTypeJson);
                    }

                    var codes = new List<string>();
                    if (jobj["faults"] is JArray faultsArr)
                    {
                        foreach (var item in faultsArr)
                        {
                            if (item is JObject fo)
                            {
                                var code = fo["code"]?.ToString();
                                var name = fo["name"]?.ToString();
                                var level = fo["level"]?.ToString();
                                if (!string.IsNullOrWhiteSpace(code)) codes.Add(code);
                                else if (!string.IsNullOrWhiteSpace(name))
                                {
                                    // 自定义故障：登记进字典再上报，保证名称与等级生效（未知故障默认 warning）
                                    var def = ResolveAgvFault(name);
                                    if (def != null && string.IsNullOrEmpty(def.Code) && !string.IsNullOrWhiteSpace(level))
                                    {
                                        def.Level = level.Trim();
                                        if (!AgvFaultCatalog.ContainsKey(name.Trim())) AgvFaultCatalog[name.Trim()] = def;
                                    }
                                    codes.Add(name);
                                }
                            }
                            else if (item?.ToString() is string s && !string.IsNullOrWhiteSpace(s))
                            {
                                codes.Add(s.Trim());
                            }
                        }
                    }

                    bool replace = jobj["replace"]?.ToString() != "false";
                    ReportAgvFaults(agvName.Trim(), codes, replace);
                    Tools.trace($"agvFaults: 上报 {agvName} 故障 {codes.Count} 条 (replace={replace})");

                    var agv = API.Agvs.FirstOrDefault(a => a.Name == agvName.Trim());
                    return Results.Text(
                        new JObject
                        {
                            ["message"] = "ok",
                            ["agvName"] = agvName.Trim(),
                            ["faults"] = agv != null ? GetAgvFaultsJson(agv, agv.Name) : new JArray(),
                        }.ToString(),
                        API.ContentTypeJson);
                });

                //清除 AGV 故障：{ agvName, code? }（code 为空清除该车全部上报故障）
                app.MapPost("/api/agvFaults/clear", async (HttpContext context) =>
                {
                    JObject jobj = null;
                    try { jobj = await context.AsJObject(); }
                    catch { }

                    string agvName = jobj?["agvName"]?.ToString();
                    if (string.IsNullOrWhiteSpace(agvName))
                    {
                        context.Response.StatusCode = 400;
                        return Results.Text(
                            new JObject { ["message"] = "agvName 不能为空" }.ToString(),
                            API.ContentTypeJson);
                    }
                    ClearAgvFaults(agvName.Trim(), jobj["code"]?.ToString());
                    Tools.trace($"agvFaults: 清除 {agvName} 故障 {jobj["code"]?.ToString() ?? "(全部)"}");
                    return Results.Text(new JObject { ["message"] = "ok" }.ToString(), API.ContentTypeJson);
                });

                //巷道子分类（巷道）列表，供前端下拉选择：货位数 / 有货数 / 首站
                app.MapGet("/api/pockets", async (HttpContext ctx) =>
                {
                    var arr = new JArray();
                    var garageSub = SubCategoryCollection.Instance[GarageArea] as ISubCategory;
                    var entranceSub = SubCategoryCollection.Instance["入口"] as ISubCategory;
                    var garageMarks = garageSub == null
                        ? new HashSet<string>()
                        : new HashSet<string>(garageSub.Children.Cast<IStation>().Select(s => s.Mark));
                    var entranceMarks = entranceSub == null
                        ? new HashSet<string>()
                        : new HashSet<string>(entranceSub.Children.Cast<IStation>().Select(s => s.Mark));

                    //同一个子分类下可能有多条物理巷道，这里按子分类名称聚合，
                    // 保证返回的巷道名称唯一，前端可以直接用它做勾选列表的 key。
                    foreach (var pocketName in 巷道子分类名称)
                    {
                        var pocketSub = SubCategoryCollection.Instance[pocketName] as ISubCategory;
                        if (pocketSub == null) continue;

                        var pocketMarks = new HashSet<string>(pocketSub.Children.Cast<IStation>().Select(s => s.Mark));
                        var pockets = pocketSub.Children.Cast<IStation>()
                            .SelectMany(s => s.BelongPocketLines).Distinct().ToList();

                        var slotCount = 0;
                        var cargoCount = 0;
                        string firstMark = "";

                        foreach (var pocket in pockets)
                        {
                            var stations = pocket.OrderStations
                                .Where(s => garageMarks.Contains(s.Mark) &&
                                            pocketMarks.Contains(s.Mark) &&
                                            !entranceMarks.Contains(s.Mark))
                                .ToList();
                            if (stations.Count == 0) continue;

                            slotCount += stations.Count;
                            cargoCount += stations.Count(s => !s.IsEmpty());
                            if (string.IsNullOrEmpty(firstMark))
                                firstMark = stations.FirstOrDefault()?.Mark ?? "";
                        }

                        arr.Add(new JObject
                        {
                            ["name"] = pocketName,
                            ["lineCount"] = pockets.Count,
                            ["slotCount"] = slotCount,
                            ["cargoCount"] = cargoCount,
                            ["firstMark"] = firstMark,
                            ["available"] = slotCount > 0,
                        });
                    }
                    return Results.Text(arr.ToString(), API.ContentTypeJson);
                });

                //巷道搬运【批次】：前端勾选多个源巷道 + 多个目标巷道，一次请求创建一个搬运批次。
                // 入参（推荐，数组）：{ "sourcePocketNames": ["01","02"], "targetPocketNames": ["03","04"] }
                // 兼容旧入参（单值）：{ "sourcePocketName": "01", "targetPocketName": "03" }
                // 传的是巷道子分类名称，不是站点 Mark。
                // 约束：CreatePocketMoveBatch 内部会校验 HasOpenCargoBatch，
                //       同一时间只允许存在一个进行中的批次，重复创建会返回 400 并带上原因。
                // 行为：创建只建立内存清单，派车与卸货位分配由调度循环在下一次 tick 完成。
                app.MapPost("/api/createPocketMoveBatch", async (HttpContext context) =>
                {
                    JObject jobj = null;
                    try { jobj = await context.AsJObject(); }
                    catch { }

                    var sourceNames = ReadPocketNames(jobj, "sourcePocketNames", "sourcePocketName");
                    var targetNames = ReadPocketNames(jobj, "targetPocketNames", "targetPocketName");

                    if (sourceNames.Count == 0 || targetNames.Count == 0)
                    {
                        context.Response.StatusCode = 400;
                        return Results.Text(
                            new JObject { ["message"] = "请至少勾选一个源巷道和一个目标巷道" }.ToString(),
                            API.ContentTypeJson);
                    }

                    Tools.trace($"createPocketMoveBatch: [{string.Join(",", sourceNames)}] -> [{string.Join(",", targetNames)}]");

                    var result = CreatePocketMoveBatch(sourceNames, targetNames);
                    if (result == null || !result.Success)
                    {
                        context.Response.StatusCode = 400;
                        return Results.Text(
                            new JObject
                            {
                                ["message"] = (result == null || string.IsNullOrWhiteSpace(result.Error))
                                    ? "巷道搬运批次创建失败"
                                    : result.Error
                            }.ToString(),
                            API.ContentTypeJson);
                    }

                    var batch = result.Batch;
                    return Results.Text(new JObject
                    {
                        ["success"] = true,
                        ["batchId"] = batch?.BatchId ?? "",
                        ["moveType"] = batch == null ? "" : batch.MoveType.ToString(),
                        ["sourcePocketNames"] = ToJArray(batch?.SourcePocketNames),
                        ["targetPocketNames"] = ToJArray(batch?.TargetPocketNames),
                        ["totalCount"] = batch?.TotalCount ?? 0,
                        ["status"] = batch == null ? "" : batch.Status.ToString(),
                        ["statusText"] = "等待调度",
                        ["message"] = $"批次创建成功，共 {batch?.TotalCount ?? 0} 件待搬运",
                    }.ToString(), API.ContentTypeJson);
                });

                //搬运批次进度列表（最新的在前）
                app.MapGet("/api/batches", async (HttpContext ctx) =>
                {
                    var arr = new JArray();
                    foreach (var b in GetBatches())
                    {
                        arr.Add(new JObject
                        {
                            ["BatchId"] = b.BatchId,
                            ["MoveType"] = b.MoveType.ToString(),
                            ["SourceScopeId"] = b.SourceScopeId,
                            ["TargetScopeId"] = b.TargetScopeId,
                            ["SourcePocketNames"] = ToJArray(b.SourcePocketNames),
                            ["TargetPocketNames"] = ToJArray(b.TargetPocketNames),
                            ["Status"] = b.Status.ToString(),
                            ["StatusText"] = b.StatusText,
                            ["TotalCount"] = b.TotalCount,
                            ["WaitingCount"] = b.WaitingCount,
                            ["ExecutingCount"] = b.ExecutingCount,
                            ["CompletedCount"] = b.CompletedCount,
                            ["NeedsReviewCount"] = b.NeedsReviewCount,
                            ["CreatedTime"] = b.CreatedTime.ToString("yyyy-MM-dd HH:mm:ss"),
                            ["CompletedTime"] = b.CompletedTime?.ToString("yyyy-MM-dd HH:mm:ss"),
                        });
                    }
                    return Results.Text(arr.ToString(), API.ContentTypeJson);
                });

                //批次明细：GET /api/batchDetail?batchId=xxx
                // 返回该批次全部货物明细及每一步的执行状态；编号为空或不存在时 404。
                app.MapGet("/api/batchDetail", async (HttpContext ctx) =>
                {
                    var batchId = ctx.Request.Query["batchId"].ToString();
                    var detail = GetBatchDetail(batchId);
                    if (detail == null)
                    {
                        ctx.Response.StatusCode = 404;
                        return Results.Text(
                            new JObject { ["message"] = "批次不存在或编号为空" }.ToString(),
                            API.ContentTypeJson);
                    }
                    return Results.Text(JObject.FromObject(detail).ToString(), API.ContentTypeJson);
                });

                //任务列表：前端表格数据源（内存任务池 TaskInfos 全量快照）。
                // 字段名与前端 mapTaskRow 一一对应，避免前端再做字段翻译：
                //   Id / FromMark / ToMark / TaskType / Status(英文枚举) / StatusText(中文阶段)
                //   RequestedAgv / AssignedAgv / BatchId / ItemId / LockPick / LockDrop
                //   Priority / CreatedAt
                // 说明：后端 TaskInfo 目前没有优先级和创建时间字段，Priority 固定返回 null，
                //       CreatedAt 取 StateTime（当前状态的进入时间）。
                app.MapGet("/api/tasks", async (HttpContext ctx) =>
                {
                    var arr = new JArray();
                    foreach (var t in TaskInfos)
                    {
                        arr.Add(new JObject
                        {
                            ["Id"] = t.TaskId,
                            ["FromMark"] = t.FromMark,
                            ["ToMark"] = t.ToMark,
                            ["TaskType"] = t.TaskType.ToString(),
                            ["Status"] = t.Status.ToString(),
                            ["StatusText"] = CargoStageText(t.Status),
                            ["RequestedAgv"] = t.RequestedAgv,
                            ["AssignedAgv"] = t.WorkAgv?.Name,
                            ["BatchId"] = t.CargoBatchId,
                            ["ItemId"] = t.CargoItemId,
                            ["LockPick"] = t.LockPickPocketKey,
                            ["LockDrop"] = t.LockDropPocketKey,
                            ["Priority"] = JValue.CreateNull(),
                            ["CreatedAt"] = t.StateTime.ToString("yyyy-MM-dd HH:mm:ss"),
                        });
                    }
                    return Results.Text(arr.ToString(), API.ContentTypeJson);
                });
                //创建移库任务
                app.MapPost("/api/createStationMoveTask", async (HttpContext context) =>
                {
                	JObject jobj = null;
                    try { jobj = await context.AsJObject(); }
                    catch { }

                    string fromMark = jobj?["fromMark"]?.ToString();
                    string toMark = jobj?["toMark"]?.ToString();
                    string agvName = jobj?["agvName"]?.ToString();
             
                	if (string.IsNullOrWhiteSpace(fromMark) || string.IsNullOrWhiteSpace(toMark))
                    {
                        context.Response.StatusCode = 400;
                        return Results.Text(
                            new JObject { ["message"] = "起始站点和目标站点均不能为空" }.ToString(),
                            API.ContentTypeJson);
                    }

                    Tools.trace($"createStationMoveTask: 收到创建移库任务请求 {fromMark} -> {toMark}");

                    bool ok = CreateStationMoveTask(fromMark, toMark, string.IsNullOrWhiteSpace(agvName)?"00":agvName); // "00"=自动分配，写死AGV名则找不到该AGV会失败
                    if (!ok)
                    {
                        context.Response.StatusCode = 400;
                        return Results.Text(
                            new JObject { ["message"] = "任务创建失败（站点不存在/巷道锁定等），详见运行日志" }.ToString(),
                            API.ContentTypeJson);
                    }
                   
                    return Results.Text("创建移库任务成功！");
                });
                
                //创建移动任务
                app.MapPost("/api/createMoveTask", async (HttpContext context) =>
                {
                	JObject jobj = null;
                    try { jobj = await context.AsJObject(); }
                    catch { }

                    
                    string toMark = jobj?["toMark"]?.ToString();
                    string agvName = jobj?["agvName"]?.ToString();
              
                	if (string.IsNullOrWhiteSpace(toMark))
                    {
                        context.Response.StatusCode = 400;
                        return Results.Text(
                            new JObject { ["message"] = "目标站点不能为空" }.ToString(),
                            API.ContentTypeJson);
                    }

                    Tools.trace($"createMoveTask: 收到创建移动任务请求 {toMark}");

                    bool ok = CreateMoveTask(toMark, string.IsNullOrWhiteSpace(agvName)?"00":agvName); // "00"=自动分配，写死AGV名则找不到该AGV会失败
                    if (!ok)
                    {
                        context.Response.StatusCode = 400;
                        return Results.Text(
                            new JObject { ["message"] = "任务创建失败（站点不存在/巷道锁定等），详见运行日志" }.ToString(),
                            API.ContentTypeJson);
                    }
                    
                    
                    return Results.Text("创建移动任务成功！");
                });

                // ==================== 货物管理（只读查询） ====================
                // 说明：前端一次性拉全量，筛选/分页都在前端做（货件量级小，联动零延迟）。
                // 将来货件过千再改成服务端过滤。

                //货物类型列表：供「货物类型」下拉使用（去重 + 排序）
                app.MapGet("/api/cargoTypes", async (HttpContext ctx) =>
                {
                    var names = new List<string>();
                    try
                    {
                        lock (cargoBatchSync)
                        {
                            foreach (var cargo in API.Cargos)
                            {
                                var n = cargo.Info?.Name;
                                if (!string.IsNullOrWhiteSpace(n) && !names.Contains(n)) names.Add(n);
                            }
                        }
                    }
                    catch (Exception ex) { Tools.trace($"[货物类型] 读取失败：{ex.Message}"); }

                    names.Sort(StringComparer.Ordinal);
                    var arr = new JArray();
                    foreach (var n in names) arr.Add(n);
                    return Results.Text(arr.ToString(), API.ContentTypeJson);
                });

                //货物清单：全量货物 + 归属位置（站点 / AGV）
                // 字段：CargoName 类型 / CargoTag 编号 / Quantity 数量
                //       OwnerType 位置类型（站点|AGV|未知）/ Mark 站点号 / PocketName 所属巷道 / AgvName 携带车辆
                app.MapGet("/api/cargos", async (HttpContext ctx) =>
                {
                    var arr = new JArray();
                    var pocketByMark = GetStationPocketMap();
                    try
                    {
                        lock (cargoBatchSync)
                        {
                            foreach (var cargo in API.Cargos)
                            {
                                var ownerType = "未知";
                                var mark = "";
                                var agvName = "";

                                var owner = API.GetOwner(cargo);
                                if (owner is IStation station) { ownerType = "站点"; mark = station.Mark ?? ""; }
                                else if (owner is IAgv agv) { ownerType = "AGV"; agvName = agv.Name ?? ""; }

                                var pocketName = "";
                                if (!string.IsNullOrEmpty(mark)) pocketByMark.TryGetValue(mark, out pocketName);

                                arr.Add(new JObject
                                {
                                    ["CargoName"] = cargo.Info?.Name ?? "",
                                    ["CargoTag"] = Convert.ToString(cargo.Tag),
                                    ["Quantity"] = cargo.Quanlity,
                                    ["OwnerType"] = ownerType,
                                    ["Mark"] = mark,
                                    ["PocketName"] = pocketName ?? "",
                                    ["AgvName"] = agvName,
                                });
                            }
                        }
                    }
                    catch (Exception ex) { Tools.trace($"[货物清单] 读取失败：{ex.Message}"); }
                    return Results.Text(arr.ToString(), API.ContentTypeJson);
                });

                //站点视图：按巷道逐个货位列出「有货 / 空货位」，有货时带出类型与编号
                // 字段：Mark 货位号 / PocketName 巷道 / Occupied 是否有货 / CargoName / CargoTag / Quantity
                app.MapGet("/api/cargoStations", async (HttpContext ctx) =>
                {
                    var arr = new JArray();
                    var cargoByMark = new Dictionary<string, ICargo>();
                    try
                    {
                        lock (cargoBatchSync)
                        {
                            foreach (var cargo in API.Cargos)
                            {
                                if (API.GetOwner(cargo) is IStation s && !string.IsNullOrEmpty(s.Mark) && !cargoByMark.ContainsKey(s.Mark))
                                    cargoByMark[s.Mark] = cargo;
                            }
                        }
                    }
                    catch (Exception ex) { Tools.trace($"[站点视图] 货物读取失败：{ex.Message}"); }

                    var garageSub = SubCategoryCollection.Instance[GarageArea] as ISubCategory;
                    var entranceSub = SubCategoryCollection.Instance["入口"] as ISubCategory;
                    var garageMarks = garageSub == null
                        ? new HashSet<string>()
                        : new HashSet<string>(garageSub.Children.Cast<IStation>().Select(s => s.Mark));
                    var entranceMarks = entranceSub == null
                        ? new HashSet<string>()
                        : new HashSet<string>(entranceSub.Children.Cast<IStation>().Select(s => s.Mark));

                    var emitted = new HashSet<string>();
                    foreach (var pocketName in 巷道子分类名称)
                    {
                        var pocketSub = SubCategoryCollection.Instance[pocketName] as ISubCategory;
                        if (pocketSub == null) continue;

                        var pocketMarks = new HashSet<string>(pocketSub.Children.Cast<IStation>().Select(s => s.Mark));
                        var pockets = pocketSub.Children.Cast<IStation>()
                            .SelectMany(s => s.BelongPocketLines).Distinct().ToList();

                        foreach (var pocket in pockets)
                        {
                            var stations = pocket.OrderStations
                                .Where(s => garageMarks.Contains(s.Mark) &&
                                            pocketMarks.Contains(s.Mark) &&
                                            !entranceMarks.Contains(s.Mark))
                                .ToList();

                            foreach (var station in stations)
                            {
                                if (!emitted.Add(station.Mark)) continue;   // 同一货位只输出一次

                                ICargo cargo;
                                cargoByMark.TryGetValue(station.Mark, out cargo);
                                var occupied = !station.IsEmpty();

                                arr.Add(new JObject
                                {
                                    ["Mark"] = station.Mark,
                                    ["PocketName"] = pocketName,
                                    ["Occupied"] = occupied,
                                    ["CargoName"] = occupied && cargo?.Info != null ? (cargo.Info.Name ?? "") : "",
                                    ["CargoTag"] = occupied && cargo != null ? Convert.ToString(cargo.Tag) : "",
                                    ["Quantity"] = occupied && cargo != null ? cargo.Quanlity : 0,
                                });
                            }
                        }
                    }
                    return Results.Text(arr.ToString(), API.ContentTypeJson);
                });
                
                

            });
        }
    }
}
                                          
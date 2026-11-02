namespace ProfControl
{
    public partial class SampleProject
    {
        // ====================================================================
        // AGV 故障模块（一台车可同时存在多条故障）
        //
        // 数据来源两条，合并去重后经 GET /api/agvs 的「故障」字段下发：
        //   1) AGV 对象上的布尔属性 —— 通讯层 agv.SetValue("前车电机报警", true/false)
        //      即可生效，无需改接口（别名表见 AgvFaultProperties）；
        //   2) 外部上报 —— POST /api/agvFaults（全量替换 / 增量）、POST /api/agvFaults/clear。
        //
        // 故障等级：fault=严重故障（红）/ warning=告警（黄）/ info=提示（蓝）。
        // 前端契约：故障: [{ code, name, level, since, duration, source }]
        // ====================================================================

        // 故障定义
        class AgvFaultDef
        {
            public string Code = "";      // 故障码，自定义故障为空
            public string Name = "";      // 故障名称
            public string Level = "warning";
        }

        // 单台车当前的一条故障
        class AgvFaultItem
        {
            public string Code = "";
            public string Name = "";
            public string Level = "warning";
            public DateTime Since = DateTime.Now;
            public string Source = "";    // 属性 / 上报
        }

        // 已知故障字典（与 VDA5050/RecvData.cs 的 0x7000~0x7007 对齐）
        readonly Dictionary<string, AgvFaultDef> AgvFaultCatalog = new Dictionary<string, AgvFaultDef>(StringComparer.OrdinalIgnoreCase)
        {
            ["0x7000"] = new AgvFaultDef { Code = "0x7000", Name = "机器人tcp断联", Level = "fault" },
            ["0x7001"] = new AgvFaultDef { Code = "0x7001", Name = "RTK数据断流", Level = "warning" },
            ["0x7002"] = new AgvFaultDef { Code = "0x7002", Name = "RTK未收敛", Level = "warning" },
            ["0x7003"] = new AgvFaultDef { Code = "0x7003", Name = "IO盒数据断流", Level = "warning" },
            ["0x7004"] = new AgvFaultDef { Code = "0x7004", Name = "前车障碍物报警", Level = "warning" },
            ["0x7005"] = new AgvFaultDef { Code = "0x7005", Name = "后车障碍物报警", Level = "warning" },
            ["0x7006"] = new AgvFaultDef { Code = "0x7006", Name = "前车电机报警", Level = "fault" },
            ["0x7007"] = new AgvFaultDef { Code = "0x7007", Name = "后车电机报警", Level = "fault" },
        };

        // AGV 对象上的故障属性名 → 故障码（与 RecvData.cs 写入的属性名保持一致）
        // 通讯层只要 agv.SetValue("前车电机报警", true/false)，看板即可显示
        readonly string[][] AgvFaultProperties = new string[][]
        {
            new string[]{ "机器人tcp断联", "0x7000" },
            new string[]{ "RTK数据断流", "0x7001" },
            new string[]{ "RTK未收敛", "0x7002" },
            new string[]{ "IO盒数据断流", "0x7003" },
            new string[]{ "前车障碍物报警", "0x7004" },
            new string[]{ "后车障碍物报警", "0x7005" },
            new string[]{ "前车电机报警", "0x7006" },
            new string[]{ "后车电机报警", "0x7007" },
        };

        // 外部上报的故障：AGV 名称 -> 故障键 -> 首次上报时间
        readonly object agvFaultLock = new object();
        readonly Dictionary<string, Dictionary<string, DateTime>> agvFaultReported =
            new Dictionary<string, Dictionary<string, DateTime>>();

        // 故障码或名称 → 故障定义；未知输入按「自定义故障」处理（等级 warning）
        AgvFaultDef ResolveAgvFault(string codeOrName)
        {
            var key = (codeOrName ?? "").Trim();
            if (string.IsNullOrEmpty(key)) return null;

            AgvFaultDef def;
            if (AgvFaultCatalog.TryGetValue(key, out def)) return def;

            foreach (var kv in AgvFaultCatalog)
            {
                if (string.Equals(kv.Value.Name, key, StringComparison.OrdinalIgnoreCase)) return kv.Value;
            }
            return new AgvFaultDef { Code = "", Name = key, Level = "warning" };
        }

        // 上报一台车的故障：replace=true 整组替换（下次上报里没有的故障自动清除），
        // false 为增量追加。保留已有故障的首报时间，避免持续时间被重置。
        void ReportAgvFaults(string agvName, IEnumerable<string> codes, bool replace)
        {
            if (string.IsNullOrWhiteSpace(agvName)) return;

            var next = new Dictionary<string, DateTime>();
            foreach (var raw in codes ?? new List<string>())
            {
                var def = ResolveAgvFault(raw);
                if (def == null) continue;
                var key = string.IsNullOrEmpty(def.Code) ? def.Name : def.Code;
                if (!next.ContainsKey(key)) next[key] = DateTime.Now;
            }

            lock (agvFaultLock)
            {
                Dictionary<string, DateTime> cur;
                if (!agvFaultReported.TryGetValue(agvName, out cur))
                {
                    agvFaultReported[agvName] = next;
                    return;
                }
                if (replace)
                {
                    foreach (var kv in next)
                        if (!cur.ContainsKey(kv.Key)) cur[kv.Key] = kv.Value;   // 已有的保留首报时间
                    var gone = cur.Keys.Where(k => !next.ContainsKey(k)).ToList();
                    foreach (var k in gone) cur.Remove(k);
                }
                else
                {
                    foreach (var kv in next)
                        if (!cur.ContainsKey(kv.Key)) cur[kv.Key] = kv.Value;
                }
            }
        }

        // 清除一台车的故障；codeOrName 为空时全清
        void ClearAgvFaults(string agvName, string codeOrName)
        {
            if (string.IsNullOrWhiteSpace(agvName)) return;

            lock (agvFaultLock)
            {
                Dictionary<string, DateTime> cur;
                if (!agvFaultReported.TryGetValue(agvName, out cur)) return;

                if (string.IsNullOrWhiteSpace(codeOrName)) { cur.Clear(); return; }

                var def = ResolveAgvFault(codeOrName);
                if (def == null) return;
                var key = string.IsNullOrEmpty(def.Code) ? def.Name : def.Code;
                cur.Remove(key);
                if (cur.Count == 0) agvFaultReported.Remove(agvName);
            }
        }

        // 读取 AGV 对象上的故障布尔属性（别名任一为 true 即视为该故障存在）
        bool ReadAgvFaultProperty(dynamic agv, string propName)
        {
            try { return agv.GetValue<bool>(propName) == true; }
            catch { return false; }
        }

        // 合并两路来源 → 当前故障列表（按故障键去重，属性来源优先）
        List<AgvFaultItem> GetAgvFaultItems(dynamic agv, string agvName)
        {
            var result = new List<AgvFaultItem>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // ① 属性来源
            foreach (var pair in AgvFaultProperties)
            {
                if (!ReadAgvFaultProperty(agv, pair[0])) continue;
                if (seen.Contains(pair[1])) continue;
                seen.Add(pair[1]);

                var def = ResolveAgvFault(pair[1]);
                if (def == null) continue;
                result.Add(new AgvFaultItem
                {
                    Code = def.Code,
                    Name = def.Name,
                    Level = def.Level,
                    Since = DateTime.Now,   // 属性来源没有时间戳，记为发现时刻
                    Source = "属性",
                });
            }

            // ② 上报来源
            if (!string.IsNullOrWhiteSpace(agvName))
            {
                lock (agvFaultLock)
                {
                    Dictionary<string, DateTime> cur;
                    if (agvFaultReported.TryGetValue(agvName, out cur))
                    {
                        foreach (var kv in cur)
                        {
                            if (seen.Contains(kv.Key)) continue;
                            seen.Add(kv.Key);

                            var def = ResolveAgvFault(kv.Key);
                            if (def == null) continue;
                            result.Add(new AgvFaultItem
                            {
                                Code = def.Code,
                                Name = def.Name,
                                Level = def.Level,
                                Since = kv.Value,
                                Source = "上报",
                            });
                        }
                    }
                }
            }

            // 严重故障排前面
            result.Sort((a, b) =>
            {
                var wa = a.Level == "fault" ? 0 : (a.Level == "warning" ? 1 : 2);
                var wb = b.Level == "fault" ? 0 : (b.Level == "warning" ? 1 : 2);
                return wa != wb ? wa - wb : string.CompareOrdinal(a.Code, b.Code);
            });
            return result;
        }

        // → 前端契约 JSON（GET /api/agvs 的「故障」字段）
        JArray GetAgvFaultsJson(dynamic agv, string agvName)
        {
            var arr = new JArray();
            var now = DateTime.Now;
            foreach (var f in GetAgvFaultItems(agv, agvName))
            {
                arr.Add(new JObject
                {
                    ["code"] = f.Code,
                    ["name"] = f.Name,
                    ["level"] = f.Level,
                    ["since"] = f.Since.ToString("HH:mm:ss"),
                    ["duration"] = (int)(now - f.Since).TotalSeconds,
                    ["source"] = f.Source,
                });
            }
            return arr;
        }
    }
}

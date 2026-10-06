namespace ProfControl
{
    public partial class SampleProject : IAppScript
    {
        #region 参数配置
        const int MaxParallelMoveTask = 4; // 允许前车卸货、后车取货重叠
        const int PickDropDelay = 5;

        const string GarageArea = "车库位";
        readonly string[] GarageDirectionAreas =
        {
            "车库位朝右", "车库位朝上", "车库位朝左", "车库位朝下"
        };
        List<string> 巷道子分类名称 = new List<string>
        {
            "01", "02", "03", "04", "05",
            "06", "07", "08", "09", "10",
            "11", "12", "13", "14", "15",
            "16", "17", "18", "19", "20",
            "21", "22", "23", "24", "25"
        };
        #endregion
        //记录所有任务
        public SmartArray<TaskInfo> TaskInfos { get; set; } = new SmartArray<TaskInfo>();

        // 🔒 巷道锁表（Key = PocketKey，使用巷道首站Mark作为唯一标识）
        Dictionary<string, TaskInfo> PocketLocks = new Dictionary<string, TaskInfo>();
        Dictionary<string, Queue<TaskInfo>> PendingClearanceBatches = new Dictionary<string, Queue<TaskInfo>>();

        #region Init

        public void Init()
        {
            LoadMapConfiguration();

            // 复用已加载的“车库位”分类，清空全部车库货位后再生成测试货物。
            var garageStations = GetMapCategory(GarageArea)?.Children;
            if (garageStations != null)
                ClearCargo(garageStations);
            else
                Tools.trace("[初始化清货] 未找到车库位分类，跳过清货");
            // 初始化货物：类型、编号、站点。
            AddCargo("迈巴赫", "000001", "71");
            AddCargo("迈巴赫", "000002", "72");
            AddCargo("迈巴赫", "000003", "67");
            AddCargo("迈巴赫", "000004", "68");
            AddCargo("迈巴赫", "000005", "58");
            AddCargo("迈巴赫", "000006", "59");
            AddCargo("迈巴赫", "000007", "54");
            AddCargo("迈巴赫", "000008", "55");
            AddCargo("迈巴赫", "000009", "44");
            AddCargo("迈巴赫", "000010", "45");
            AddCargo("迈巴赫", "000011", "40");
            AddCargo("迈巴赫", "000012", "41");
            TraceCargoLocations();

            //用于显示小车执行的任务类型及相关类型下的机器人状态
            API.QuickGProperty<IAgv, string>("状态", Dispatch.PPT.PropertyDetail.PropertyCtrlEnum.TextBox, false);
            API.QuickGProperty<IAgv, string>("任务类型", Dispatch.PPT.PropertyDetail.PropertyCtrlEnum.TextBox, false);

            //批量配置和初始化所有 AGV 的地图显示属性
            API.Agvs.Foreach(agv =>
            {
                //显示AGV的任务状态
                agv.ShowInMap("状态", true);
                agv.ShowInMap("任务类型", true);

                //当AGV无任务时，将属性值清空
                agv.SetValue("状态", "");
                agv.SetValue("任务类型", "");
            });

            InitAgvParking();

            // ===== 留给后续 API 接入的内部函数示例，请保留注释 =====
            // CreateMoveTask("53", "20"); // 空车移动：目标站点 Mark、车辆名称；"00" 自动选车。
            // CreateStationMoveTask("72", "41", "00"); // 点到点搬运：起点 Mark、终点 Mark、车辆名称。
            // var cargoTags = GetCargoTags("迈巴赫"); // 按货物类型查询编号下拉列表，返回 List<string>，例如 "000001"、"000002"；无匹配时为空列表。
            // var mercedesTags = GetCargoTags("梅赛德斯"); // 换一个类型，只返回该类型的非空、去重、排序后的 Tag；包含站点和 AGV 上的货物。
            // var addResult = AddCargo("迈巴赫", "000013", "目标空货位Mark"); // 在对应朝向区域添加一件货物；类型需预先配置，类型 + Tag 不得重复。
            // var editResult = UpdateCargo("迈巴赫", "000013", "梅赛德斯", "000008"); // 原类型、原Tag、新类型、新Tag；保持当前站点。
            // var deleteResult = DeleteCargo("梅赛德斯", "000008"); // 删除指定实体货物，不清空整站或删除托盘。
            // addResult/editResult/deleteResult 的 Success、Error 表示操作结果；CargoName、CargoTag、StationMark 返回货物信息。
            // 货物编辑要求当前没有未完成任务/批次/巷道锁，修改和删除只允许站点内唯一的一件货物；拒绝编辑 AGV 上的货物。
            // var cargoResult = CreateCargoMoveTask("迈巴赫", "000001", "目标空货位Mark"); // 类型 + Tag 自动定位起点。
            // cargoResult.Success / cargoResult.Error：任务创建结果和失败原因；选择 cargoTags 中的编号作为上述第二个参数，能否搬运由创建函数检查。
            // cargoResult.BatchId / cargoResult.ItemId：单件搬运也返回统一批次和明细编号，通过 GetBatchDetail(cargoResult.BatchId) 查询。
            // detail.Items：目标货物类型、Tag、目标货位/巷道和实际卸货位；每项 Steps 包含清障、暂存及目标搬运的全部步骤。
            // var result = CreatePocketMoveBatch(new[] { "01" }, new[] { "03" }); // 巷道搬运：来源、目标子分类名称；不要传站点号。
            // var multiResult = CreatePocketMoveBatch(new[] { "01", "02", "03" }, new[] { "06", "07" }); // 不定长列表；目标按顺序填充。
            // result.RequiredCount / result.TargetCapacities：待搬数量及各目标可进入空位、预留、剩余容量。
            // result.Success / result.Error / result.Batch.BatchId：创建结果、失败原因、成功后的批次编号。
            // var batches = GetBatches(); // 所有批次的进度快照。
            // var detail = GetBatchDetail("填写创建成功返回的批次编号"); // 全部明细；找不到时为 null。
            // var pickStations = GetSelectableStations(true); // 有货的取货站点。
            // var dropStationsForApi = GetSelectableStations(false); // 可选卸货站点。

            // ===== 当前测试：切换时直接注释调用，不使用开关变量 =====
            var testCargoTags = GetCargoTags("迈巴赫");
            Tools.trace($"[货物编号查询测试] 类型=迈巴赫，编号数量={testCargoTags.Count}，编号列表=[{string.Join(", ", testCargoTags)}]");
            // TestPocketMoveBatchCreation(new[] { "06", "07", "08", "09", "10" }, new[] { "01", "02", "03" });
            TestCargoMoveCreation(); // 单件搬运测试；与上面的巷道搬运测试轮流注释，只启用一个。
            TestCargoBatchQueries(); // 两种搬运共用同一批次详情查询。
            StartScheduler();
            // 查询测试：每 10 秒输出最新状态，不需要时直接注释这一行。
            API.SetInterval(tc => TestCargoBatchQueries(), 10000); // 两种搬运共用，打印货物、目标位置及全部步骤。
            // API.SetInterval(tc => TraceCargoLocations("000001"), 10000); // 可选的货物位置查询。
            StartHttpApi();
        }
        #endregion

        #region 调度主循环

        void StartScheduler()
        {
            API.SetInterval(tc =>
            {
                ExecuteMoveTasks();
            }, 1000);
        }

        #endregion




        public void Main()
        {
            Init();
            while (true)
            {
                Thread.Sleep(1000);
            }
        }

        public void Kill() { }
        
    }
}
 
                                       
         
   
 
 
   
             
 
 
    
   
      
             
            
           
                                                  
  
 
       
  
    
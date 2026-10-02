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

            var targetStations = new List<IStation>
            {
                API.GetStation("71"),
                API.GetStation("72"),
                API.GetStation("67"),
                API.GetStation("68"),
                API.GetStation("58"),
                API.GetStation("59"),
                API.GetStation("54"),
                API.GetStation("55"),
                API.GetStation("44"),
                API.GetStation("45"),
                API.GetStation("40"),
                API.GetStation("41"),
            };

            var clearStations = new List<IStation>
            {
                API.GetStation("85"),
                API.GetStation("63"),
                API.GetStation("16"),
                API.GetStation("139"),

            };

            ClearCargo(clearStations);
            GenerateCargo(targetStations);

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
            // CreateStationMoveTask("4", "53", "00"); // 点到点搬运：起点 Mark、终点 Mark、车辆名称。
            // var result = CreatePocketMoveBatch(new[] { "01" }, new[] { "03" }); // 巷道搬运：来源、目标子分类名称；不要传站点号。
            // var multiResult = CreatePocketMoveBatch(new[] { "01", "02", "03" }, new[] { "06", "07" }); // 不定长列表；目标按顺序填充。
            // result.RequiredCount / result.TargetCapacities：待搬数量及各目标可进入空位、预留、剩余容量。
            // result.Success / result.Error / result.Batch.BatchId：创建结果、失败原因、成功后的批次编号。
            // var batches = GetBatches(); // 所有批次的进度快照。
            // var detail = GetBatchDetail("填写创建成功返回的批次编号"); // 全部明细；找不到时为 null。
            // var pickStations = GetSelectableStations(true); // 有货的取货站点。
            // var dropStationsForApi = GetSelectableStations(false); // 可选卸货站点。
            // ===== 当前测试：切换时直接注释调用，不使用开关变量 =====
            TestPocketMoveBatchCreation(new[] { "06", "07", "08", "09", "10" }, new[] { "01", "02", "03" }); // 按实际地图填写巷道子分类名称。
            TestCargoBatchQueries(); // 创建后立即查询一次，检查全部等待明细。
            StartScheduler();
            // 查询测试：每 10 秒输出最新状态，不需要时直接注释这一行。
            API.SetInterval(tc => TestCargoBatchQueries(), 10000);
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
 
                                       
         
   
 
 
   
             
 
 
    
   
      
             
            
        
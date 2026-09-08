namespace ProfControl
{
    public partial class SampleProject : IAppScript
    {
        #region 参数配置
        const int TaskGenerationInterval = 3;
        const int MaxParallelMoveTask = 1; // 全局最多一个未完成的移库任务
        const int PickDropDelay = 5;
        const bool EnableAutoTaskGeneration = false;

        const string GarageArea = "车库位";
        List<string> 巷道子分类名称 = new List<string> { "01", "02", "03", "04" };
        #endregion
        bool is_GoHome = false;
        //记录所有任务
        public SmartArray<TaskInfo> TaskInfos { get; set; } = new SmartArray<TaskInfo>();

        // 🔒 巷道锁表（Key = PocketKey，使用巷道首站Mark作为唯一标识）
        Dictionary<string, TaskInfo> PocketLocks = new Dictionary<string, TaskInfo>();
        Dictionary<string, Queue<TaskInfo>> PendingCargoBatches = new Dictionary<string, Queue<TaskInfo>>();

        #region Init

        public void Init()
        {
            var targetStations = new List<IStation>
            {
                API.GetStation("4"),
                API.GetStation("38"),
                API.GetStation("36"),
                API.GetStation("7"),
                API.GetStation("9"),
                API.GetStation("33"),
                API.GetStation("32"),
                API.GetStation("20"),

            };

            var dropStations = new List<IStation>
            {
                API.GetStation("2"),
                API.GetStation("53"),
                API.GetStation("54"),
                API.GetStation("55"),
                API.GetStation("3"),
                API.GetStation("18"),
                API.GetStation("22"),
                API.GetStation("23"),

            };

            ClearCargo(dropStations);
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

            // 临时测试：分别打印可取货站点和可放货站点。
            foreach (var station in GetSelectableStations(true))
            {
                Tools.trace($"可取货站点={station.StationMark}, 所属巷道={station.PocketName}, 是否有货={station.HasCargo}");
            }
            foreach (var station in GetSelectableStations(false))
            {
                Tools.trace($"可放货站点={station.StationMark}, 所属巷道={station.PocketName}, 是否有货={station.HasCargo}");
            }

            // CreateMoveTask("53", "20");
            CreateStationMoveTask("4","53", "20");
            StartScheduler();
            StartHttpApi();
        }
        #endregion

        #region 调度主循环

        void StartScheduler()
        {
            API.SetInterval(tc =>
            {
                if (EnableAutoTaskGeneration)
                {
                    GenerateMoveTasks();
                }

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
 
                                       
         
   
 
 
 
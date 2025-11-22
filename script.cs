namespace ProfControl
{
    public class SampleProject : IAppScript
    {
        public enum TaskTypeEnum
        {
            入库任务,
            出库任务,
            移库任务,
        }

        //定义任务类型
        public class TaskInfo
        {
            public DateTime OccurTime { set; get; }
            public string FromMark { set; get; }
            public string ToMark { set; get; }
            public IAgv WorkAgv { set; get; }
            public string Status { set; get; } = "";
            public TaskTypeEnum TaskType { set; get; }

            public TaskInfo()
            {
                OccurTime = DateTime.Now;
            }
        }

        //记录所有任务
        public SmartArray<TaskInfo> TaskInfos { set; get; } = new SmartArray<TaskInfo>();
        //入库站点
        public List<IStation> PickStas { set; get; } = new List<IStation>();
        //出库站点
        public List<IStation> DropStas { set; get; } = new List<IStation>();

        //初始货物比例
        static double haveCargoRatio = 0.5;
        public void GenerateCargo()
        {
            API.RandomGenerateCargoInfo(3);
            API.RandomGenerateContainerInfo(1, true, false, false);

            //所有有货物区域的站点
            var allCAStas = API.Stations.Where(x => x.CargoAreas.Count > 0 && !PickStas.Contains(x) && !DropStas.Contains(x)).ToList();
            //每次创建之前，我们需要清除地图内所有的货物
            API.ClearCargos(API.Stations.Where(x => x.CargoAreas.Count > 0).SelectMany(x => x.CargoAreas).Distinct().ToList());

            //所有有货物区域站点所属于的巷道，注意，这里面需要去重
            var pockets = allCAStas.SelectMany(x => x.BelongPocketLines).Distinct().ToList();
            var needCargoCnt = allCAStas.Count * haveCargoRatio;

            var rand = new Random();
            while (needCargoCnt > 0)
            {
                //随机选中一条巷道
                var pickPocket = pockets[rand.Next(pockets.Count)];

                foreach (var sta in pickPocket.OrderStations)
                {
                    if (!PickStas.Contains(sta) && !DropStas.Contains(sta))
                    {
                        if (sta.CargoAreas.Count > 0 && sta.IsEmpty() == true)
                        {
                            //我们需要有一定的货物信息和容器信息，这里面我们可以通过系统API快速创建
                            var container = sta.CargoAreas.FirstOrDefault().ContainerPlace("托盘_0");
                            container.CargoPlace("货物_1", 1);
                            needCargoCnt--;
                            break;
                        }
                    }
                }
            }
        }

        public void Init()
        {
            PickStas.Add(API.GetStation("2"));
            DropStas.Add(API.GetStation("20"));

            GenerateCargo();

            API.QuickGProperty<IAgv, string>("状态", Dispatch.PPT.PropertyDetail.PropertyCtrlEnum.TextBox, false);
            API.QuickGProperty<IAgv, string>("任务类型", Dispatch.PPT.PropertyDetail.PropertyCtrlEnum.TextBox, false);

            API.Agvs.Foreach(agv =>
            {
                agv.ShowInMap("状态", true);
                agv.ShowInMap("任务类型", true);
                agv.SetValue("状态", "");
                agv.SetValue("任务类型", "");
            });

            //设计一个1秒钟中断，用于模拟任务下发，任务处理等
            API.SetInterval(tc =>
            {
                var tasksSuspend = TaskInfos.Where(x => x.Status != "完成");
                if (tasksSuspend.Count() < 4)
                {
                    //所有有货物区域的站点
                    var allCAStas = API.Stations.Where(x => x.CargoAreas.Count > 0 && !PickStas.Contains(x) && !DropStas.Contains(x)).ToList();
                    //所有有货物区域站点所属于的巷道，注意，这里面需要去重
                    var pockets = allCAStas.SelectMany(x => x.BelongPocketLines).Distinct().ToList();

                    var rand = new Random();

                    //模拟入库任务
                    foreach (var pickSta in PickStas)
                    {
                        //如果上料点存在货物，跳出
                        if (pickSta.IsEmpty() == false)
                            continue;
                        //如果对于某一个上料点，已经有一个未完成的上料任务存在，跳出
                        if (TaskInfos.Any(x => x.Status != "完成" && x.FromMark == pickSta.Mark) == true)
                            continue;

                        //随机选中某一个巷道作为入库任务的放置点
                        var pickPocket = pockets[rand.Next(pockets.Count)];
                        if (pickPocket != null)
                        {
                            //在没有移库任务的时候，我们入库任务尽可能合理，比如说入库任务的放置点应该没有货物，并且相同类型的入库任务不要使用同一个放置点
                            for (var i = 0; i < pickPocket.OrderStations.Count; i++)
                            {
                                var sta = pickPocket.OrderStations[i];

                                if (sta.CargoAreas.Count == 0)
                                    continue;
                                if (sta.IsEmpty() == false)
                                    continue;
                                if (TaskInfos.Any(x => x.Status != "完成" && x.TaskType == TaskTypeEnum.入库任务 && x.ToMark == sta.Mark) == true)
                                    continue;

                                var taskInfo = new TaskInfo();
                                taskInfo.FromMark = pickSta.Mark;
                                var cargoArea = pickSta.CargoAreas.FirstOrDefault();

                                //上料点模拟有货
                                var container = cargoArea.ContainerPlace("托盘_0");
                                container.CargoPlace("货物_1", 1);

                                taskInfo.ToMark = sta.Mark;
                                taskInfo.TaskType = TaskTypeEnum.入库任务;

                                TaskInfos.Add(taskInfo);
                                //一定要跳出，随机的只是某一个巷道，如果不跳出则会根据空余情况生成多个同一巷道的任务！
                                break;
                            }
                        }
                    }
                }

                //编写不同任务的状态机，注意，还是处理未完成的任务
                foreach (var info in tasksSuspend)
                {
                    if (info.TaskType == TaskTypeEnum.入库任务)
                    {
                        //任务刚开始，状态字符串为空，先找寻合适的车辆
                        if (info.Status == "")
                        {
                            //被其他任务选中的Agv也最好不要再次被选择
                            var agvs = API.按条件找一些Agv(x => TaskInfos.Any(y => y.Status != "完成" && y.WorkAgv == x) == false && x.IsFree()).ToList();
                            var pickAgv = agvs.站点距离Agv集合中最近的一个(API.GetStation(info.FromMark));
                            //找到了Agv
                            if (pickAgv != null)
                            {
                                info.WorkAgv = pickAgv;
                                //这里我们可以测试一下
                                pickAgv.GoTo(info.FromMark);
                                info.Status = "行驶到取货点";
                            }
                        }
                        else if (info.Status == "行驶到取货点")
                        {
                            //Agv到达取货点，并且当前导航任务已完成
                            if (info.WorkAgv.API.On == info.FromMark && info.WorkAgv.IsFree())
                            {
                                API.GetStation(info.FromMark).MoveToOtherArea("货物区域1", info.WorkAgv, "货物区域1");
                                info.Status = "取货完成";
                            }
                        }
                        else if (info.Status == "取货完成")
                        {
                            info.WorkAgv.GoTo(info.ToMark);
                            info.Status = "行驶到送货点";
                        }
                        else if (info.Status == "行驶到送货点")
                        {
                            //Agv到达送货点，并且当前导航任务已完成
                            if (info.WorkAgv.API.On == info.ToMark && info.WorkAgv.IsFree())
                            {
                                info.WorkAgv.MoveToOtherArea("货物区域1", API.GetStation(info.ToMark), "货物区域1");
                                info.Status = "送货完成";
                            }
                        }
                        else if (info.Status == "送货完成")
                        {
                            info.Status = "完成";
                        }

                        if (info.WorkAgv != null)
                        {
                            info.WorkAgv.SetValue("状态", info.Status);
                            info.WorkAgv.SetValue("任务类型", info.TaskType.ToString());
                        }
                    }
                    else if (info.TaskType == TaskTypeEnum.出库任务)
                    {
                    }
                    else if (info.TaskType == TaskTypeEnum.移库任务)
                    {
                    }
                }

            }, 1000);

            var parkingStas = new List<IStation>() { API.GetStation("179"), API.GetStation("181"), API.GetStation("183") };
            var idx = 0;
            API.Agvs.Foreach(agv =>
            {
                var parkingSta = parkingStas[idx];
                idx++;
                //为了恢复默认状态，清空所有Agv的任务，并且清空所有的货物，将Agv放置在一个合适的初始位置
                agv.TaskKillAll();
                agv.TakeAll();
                agv.View.Radian = Math.PI;
                agv.View.Move(parkingSta.View.KeyPoint, true);
                agv.View.CalcBound();
                agv.View.SafeInvalidate();
            });
        }

        public void Main()
        {
            Init();

            while (true)
            {
                if (Tools.IsOpenedProject)
                    break;
                Thread.Sleep(100);
            }

            while (true)
            {

                Thread.Sleep(1000);
            }
        }

        public void Kill()
        {
            PickStas.Clear();
            DropStas.Clear();
        }

    }
} 
namespace ProfControl
{
    public partial class SampleProject
    {
        class ParkingLane
        {
            public string Gate;
            public List<string> Slots;
            public IAgv Owner;
        }
        class ParkingTrip
        {
            public ParkingLane Lane;
            public string Slot;
            public bool Entering;
        }
        readonly List<ParkingLane> parkingLanes = new List<ParkingLane>();
        readonly HashSet<string> parkingSlotMarks = new HashSet<string>();
        readonly Dictionary<IAgv, string> parkedAgvs = new Dictionary<IAgv, string>();
        readonly Dictionary<IAgv, ParkingTrip> parkingTrips = new Dictionary<IAgv, ParkingTrip>();

        void LoadParkingLanes()
        {
            var sub = GetMapCategory("agv停放点");
            var gates = GetMapCategory("入口");
            if (sub == null || gates == null)
            {
                Tools.trace("[停车配置] 缺少 agv停放点 或 入口 分类，暂停动态回家");
                return;
            }
            var slots = sub.Marks;
            foreach (var slot in slots) parkingSlotMarks.Add(slot);
            var entrances = gates.Marks;
            var assigned = new HashSet<string>();
            foreach (var pocket in sub.Children.OfType<IStation>().SelectMany(s => s.BelongPocketLines).Distinct())
            {
                var laneSlots = pocket.OrderStations.Where(s => slots.Contains(s.Mark) && !entrances.Contains(s.Mark)).Select(s => s.Mark).ToList();
                var laneGates = pocket.OrderStations.Where(s => entrances.Contains(s.Mark)).Select(s => s.Mark).Distinct().ToList();
                if (laneSlots.Count == 0 || laneGates.Count != 1 || laneSlots.Any(assigned.Contains))
                {
                    Tools.trace($"[停车配置明细] 深处到门口={string.Join(" -> ", pocket.OrderStations.Select(s => s.Mark))}, 停放点={string.Join(",", laneSlots)}, 入口={string.Join(",", laneGates)}, 重复停放点={string.Join(",", laneSlots.Where(assigned.Contains))}");
                    parkingLanes.Clear();
                    var reason = laneSlots.Count == 0 ? "该巷道没有有效停放点（检查是否全部也被标为入口）" :
                        laneGates.Count == 0 ? "该巷道站点列表内没有属于入口分类的点" :
                        laneGates.Count > 1 ? "该巷道有多个入口点，无法确定进出口" : "停放点同时被识别到多条巷道";
                    Tools.trace($"[停车配置] {reason}，暂停动态停车及受影响停车位派车");
                    return;
                }
                parkingLanes.Add(new ParkingLane { Gate = laneGates[0], Slots = laneSlots });
                foreach (var slot in laneSlots) assigned.Add(slot);
                Tools.trace($"[停车配置] 入口={laneGates[0]}, 深处到外侧={string.Join(" -> ", laneSlots)}");
            }
            if (slots.Except(entrances).Any(s => !assigned.Contains(s)))
            {
                parkingLanes.Clear();
                Tools.trace("[停车配置] 部分停放点没有识别到巷道，暂停动态停车");
            }
        }

        string ParkingPosition(IAgv agv)
        {
            // 途中 On 可能为空，已登记的占用一直保持到确认出巷道。
            if (parkedAgvs.TryGetValue(agv, out var mark)) return mark;
            return agv.API.On;
        }

        bool CanLeaveParking(IAgv agv)
        {
            if (parkingTrips.ContainsKey(agv)) return false;
            var mark = ParkingPosition(agv);
            var lane = parkingLanes.FirstOrDefault(l => l.Slots.Contains(mark));
            if (lane == null) return !parkingSlotMarks.Contains(mark);
            if (lane.Owner != null) return false;
            var index = lane.Slots.IndexOf(mark);
            return !API.Agvs.Any(a => a != agv && lane.Slots.Skip(index + 1).Contains(ParkingPosition(a)));
        }

        void StartFromParking(TaskInfo info, string target, TaskStatus next)
        {
            var lane = parkingLanes.FirstOrDefault(l => l.Slots.Contains(ParkingPosition(info.WorkAgv)));
            if (lane == null)
            {
                MoveStage(info, target, next);
                return;
            }
            lane.Owner = info.WorkAgv;
            info.ParkingExitMark = lane.Gate;
            info.ParkingResumeMark = target;
            info.ParkingResumeStatus = next;
            MoveStage(info, lane.Gate, TaskStatus.GoParkingExit);
            Tools.trace($"[停车出库] AGV={info.WorkAgv.Name}, 前往出口={lane.Gate}");
        }

        void FinishParkingExit(TaskInfo info)
        {
            foreach (var lane in parkingLanes.Where(l => l.Owner == info.WorkAgv).ToList())
                DelayParkingLaneRelease(lane, info.WorkAgv);
            parkedAgvs.Remove(info.WorkAgv);
            MoveStage(info, info.ParkingResumeMark, info.ParkingResumeStatus);
        }

        bool TryPark(IAgv agv)
        {
            if (parkingTrips.ContainsKey(agv)) return true;
            if (!agv.IsFree()) return false;
            if (parkingLanes.Any(l => l.Slots.Contains(ParkingPosition(agv)))) return true;
            foreach (var lane in parkingLanes.Where(l => l.Owner == null)
                .OrderBy(l => API.Dist(agv.View.KeyPoint, API.GetStation(l.Gate).View.KeyPoint)))
            {
                // 外侧有车时，不能分配其后方的空洞；预留由巷道 Owner 保护。
                var slot = lane.Slots.AsEnumerable().Reverse()
                    .TakeWhile(s => !API.Agvs.Any(a => a != agv && ParkingPosition(a) == s))
                    .LastOrDefault();
                if (slot == null) continue;
                lane.Owner = agv;
                parkingTrips[agv] = new ParkingTrip { Lane = lane, Slot = slot };
                agv.GoTo(lane.Gate);
                Tools.trace($"[动态回家] AGV={agv.Name}, 入口={lane.Gate}, 预留停车位={slot}");
                return true;
            }
            return false;
        }

        void DelayParkingLaneRelease(ParkingLane lane, IAgv owner)
        {
            DelayLockRelease(() =>
            {
                if (lane.Owner == owner) lane.Owner = null;
            });
        }

        void UpdateParkingTrips()
        {
            foreach (var pair in parkingTrips.ToList())
            {
                var agv = pair.Key;
                var trip = pair.Value;
                if (!agv.IsFree()) continue;
                if (!trip.Entering && agv.API.On == trip.Lane.Gate)
                {
                    agv.GoTo(trip.Slot);
                    trip.Entering = true;
                }
                else if (trip.Entering && agv.API.On == trip.Slot)
                {
                    parkedAgvs[agv] = trip.Slot;
                    DelayParkingLaneRelease(trip.Lane, agv);
                    parkingTrips.Remove(agv);
                    Tools.trace($"[停车完成] AGV={agv.Name}, 停车位={trip.Slot}");
                }
            }
        }
    }
}

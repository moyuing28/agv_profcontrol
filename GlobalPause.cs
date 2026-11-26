namespace ProfControl
{
    public partial class SampleProject
    {
        public class GlobalPauseResult
        {
            public bool Success { get; set; } = true;
            public string PauseId { get; set; }
            public string Stage { get; set; }
            public bool IsPaused { get; set; }
            public bool CanResume { get; set; }
            public string Message { get; set; }
            public List<string> Issues { get; set; } = new List<string>();
            public List<PausedAgvStatus> Agvs { get; set; } = new List<PausedAgvStatus>();
        }

        public class PausedAgvStatus
        {
            public string AgvName { get; set; }
            public string TargetMark { get; set; }
            public string BusinessStage { get; set; }
            public string CurrentMark { get; set; }
            public bool NavigationCleared { get; set; }
            public int TaskCount { get; set; }
            public int RunningTaskCount { get; set; }
            public int SuspendTaskCount { get; set; }
            public int CurrentRouteCount { get; set; }
            public int CachedRouteCount { get; set; }
            public bool Idle { get; set; }
            public bool PositionStable { get; set; }
            public bool ActionPending { get; set; }
            public bool ActionCompleted { get; set; }
            public string Error { get; set; }
        }

        class PausedAgv
        {
            public IAgv Agv;
            public TaskInfo Task;
            public TaskStatus BusinessStage;
            public string Target;
            public bool HadNavigation;
            public bool WasOnline;
            public string Error;
            public string CaptureError;
            public Point LastPosition;
            public DateTime StableSince;
            public DateTime? ActionStarted;
            public bool ActionCompleted;
            public bool NavigationCleared;
            public bool PositionStable;
        }

        bool globalPaused;
        string globalPauseId;
        readonly List<PausedAgv> pausedAgvs = new List<PausedAgv>();
        // 反馈锁存到下一次 MoveStage，恢复后的首次状态推进仍可读取完成结果。
        readonly HashSet<TaskInfo> completedStationActions = new HashSet<TaskInfo>();

        void NavigateBusiness(IAgv agv, string mark)
        {
            if (globalPaused) throw new InvalidOperationException("全局暂停期间禁止下发导航");
            agv.GoTo(mark);
        }

        // RunningTask/SuspendTask 是集合；路线也可能保留空对象，不能用非 null 判断有导航。
        bool HasNavigation(IAgv agv) => agv.Tasks.Count > 0 || (agv.RunningTask?.Count ?? 0) > 0 ||
            (agv.SuspendTask?.Count ?? 0) > 0 || (agv.CurRoute?.Count ?? 0) > 0 || (agv.CacheRoute?.Count ?? 0) > 0;

        string BusinessTarget(TaskInfo info)
        {
            if (info == null) return null;
            switch (info.Status)
            {
                case TaskStatus.GoMove: case TaskStatus.GoDrop: return info.ToMark;
                case TaskStatus.GoPick: return info.FromMark;
                case TaskStatus.GoPickEntrance: case TaskStatus.GoPickExit: return info.PickEntranceMark;
                case TaskStatus.GoDropEntrance: return info.DropEntranceMark;
                case TaskStatus.GoParkingExit: return info.ParkingExitMark;
                default: return null;
            }
        }

        string ActionMark(PausedAgv saved)
        {
            if (saved.Task == null) return null;
            switch (saved.BusinessStage)
            {
                case TaskStatus.GoPick: case TaskStatus.Picking: return saved.Task.FromMark;
                case TaskStatus.GoDrop: case TaskStatus.Dropping: return saved.Task.ToMark;
                case TaskStatus.GoDropEntrance: case TaskStatus.InitPosition: return saved.Task.DropEntranceMark;
                default: return null;
            }
        }

        /// <summary>一键暂停。重复调用保留原快照，仅重试失败的导航取消。</summary>
        public GlobalPauseResult PauseGlobal()
        {
            lock (cargoBatchSync)
            {
                if (!globalPaused)
                {
                    globalPaused = true; // 先关闭调度闸门，即使快照或取消失败也保持暂停。
                    globalPauseId = Guid.NewGuid().ToString();
                    pausedAgvs.Clear();
                    foreach (var agv in API.Agvs)
                    {
                        var saved = new PausedAgv { Agv = agv, StableSince = DateTime.UtcNow };
                        pausedAgvs.Add(saved);
                        try
                        {
                            var tasks = TaskInfos.Where(t => t.WorkAgv == agv && t.Status != TaskStatus.Finished).ToList();
                            saved.Task = tasks.FirstOrDefault();
                            saved.BusinessStage = saved.Task?.Status ?? TaskStatus.None;
                            saved.WasOnline = agv.OnLine;
                            saved.LastPosition = agv.View.KeyPoint;
                            saved.HadNavigation = HasNavigation(agv);
                            saved.Target = BusinessTarget(saved.Task);
                            if (parkingTrips.TryGetValue(agv, out var trip))
                                saved.Target = trip.Entering ? trip.Slot : trip.Lane.Gate;
                            if (saved.HadNavigation && saved.Target == null)
                                saved.CaptureError = "存在无法恢复的非业务导航，需人工核对";
                            if (tasks.Count > 1) saved.CaptureError = "车辆关联多个未完成业务，需人工核对";
                            if (saved.Task != null && (saved.BusinessStage == TaskStatus.Picking ||
                                saved.BusinessStage == TaskStatus.Dropping || saved.BusinessStage == TaskStatus.InitPosition))
                                saved.ActionStarted = saved.Task.StateTime.ToUniversalTime();
                            ObservePausedAction(saved);
                        }
                        catch (Exception ex) { saved.CaptureError = "保存暂停状态失败：" + ex.Message; }
                    }
                    foreach (var saved in pausedAgvs) CancelPausedNavigation(saved);
                    Tools.trace($"[全局暂停] 编号={globalPauseId}，已禁止派单及业务推进");
                }
                else
                    foreach (var saved in pausedAgvs.Where(s => s.Error != null)) CancelPausedNavigation(saved);
                return ReadGlobalPauseStatus();
            }
        }

        void CancelPausedNavigation(PausedAgv saved)
        {
            try
            {
                saved.Agv.TaskKillAll();
                saved.Error = null;
                saved.StableSince = DateTime.UtcNow;
            }
            catch (Exception ex) { saved.Error = "取消导航失败：" + ex.Message; }
        }

        void ObservePausedAction(PausedAgv saved)
        {
            var mark = ActionMark(saved);
            if (mark == null || saved.ActionCompleted) return;
            // 途中取消后尚未到站的动作未开始，恢复导航后再按原流程执行。
            // 已处于等待反馈阶段的实机动作，即使 Idle 尚未变为 true 也锁存完成反馈。
            if (!saved.ActionStarted.HasValue && saved.Agv.API.On != mark) return;
            if (SimulateStationActions && (saved.Agv.API.On != mark || !saved.Agv.Idle)) return;
            if (!saved.ActionStarted.HasValue) saved.ActionStarted = DateTime.UtcNow;
            if (completedStationActions.Contains(saved.Task) ||
                (SimulateStationActions
                    ? (DateTime.UtcNow - saved.ActionStarted.Value).TotalSeconds >= PickDropDelay
                    : saved.Agv.GetValue<string>("ActionState") == "FINISHED"))
            {
                saved.ActionCompleted = true;
                completedStationActions.Add(saved.Task);
            }
        }

        void UpdateGlobalPause()
        {
            foreach (var saved in pausedAgvs)
            {
                try
                {
                    var point = saved.Agv.View.KeyPoint;
                    saved.NavigationCleared = !HasNavigation(saved.Agv);
                    if (!saved.Agv.Idle || !saved.NavigationCleared || (point - saved.LastPosition).Length > 0.01)
                        saved.StableSince = DateTime.UtcNow;
                    saved.LastPosition = point;
                    saved.PositionStable = saved.Agv.Idle && saved.NavigationCleared &&
                        (DateTime.UtcNow - saved.StableSince).TotalSeconds >= 2;
                    ObservePausedAction(saved);
                }
                catch (Exception ex)
                {
                    saved.PositionStable = false;
                    saved.Error = "读取暂停状态失败：" + ex.Message;
                }
            }
        }

        public GlobalPauseResult GetGlobalPauseStatus()
        {
            lock (cargoBatchSync) return ReadGlobalPauseStatus();
        }

        GlobalPauseResult ReadGlobalPauseStatus()
        {
            var result = new GlobalPauseResult { PauseId = globalPauseId, IsPaused = globalPaused,
                Stage = globalPaused ? "Pausing" : "Running", Message = globalPaused ? "全局业务已暂停" : "全局业务运行中" };
            if (!globalPaused) return result;
            UpdateGlobalPause();
            foreach (var saved in pausedAgvs)
            {
                var error = saved.CaptureError ?? saved.Error;
                if (saved.Agv.OnLine != saved.WasOnline) error = "暂停期间上下线状态改变，需人工核对";
                if (saved.Task != null && saved.Task.Status != saved.BusinessStage) error = "暂停期间业务步骤发生变化";
                if (saved.BusinessStage == TaskStatus.NeedsReview) error = "业务处于 NeedsReview，需人工核对";
                var pending = saved.ActionStarted.HasValue && !saved.ActionCompleted;
                result.Agvs.Add(new PausedAgvStatus { AgvName = saved.Agv.Name, TargetMark = saved.Target,
                    BusinessStage = saved.BusinessStage.ToString(), CurrentMark = saved.Agv.API.On,
                    NavigationCleared = saved.NavigationCleared, PositionStable = saved.PositionStable,
                    TaskCount = saved.Agv.Tasks.Count, RunningTaskCount = saved.Agv.RunningTask?.Count ?? 0,
                    SuspendTaskCount = saved.Agv.SuspendTask?.Count ?? 0, CurrentRouteCount = saved.Agv.CurRoute?.Count ?? 0,
                    CachedRouteCount = saved.Agv.CacheRoute?.Count ?? 0, Idle = saved.Agv.Idle,
                    ActionPending = pending, ActionCompleted = saved.ActionCompleted, Error = error });
                if (error != null) result.Issues.Add(saved.Agv.Name + ": " + error);
                if (!saved.PositionStable) result.Issues.Add(saved.Agv.Name + ": 等待导航清空及位置稳定至少2秒");
                if (pending) result.Issues.Add(saved.Agv.Name + ": 等待站点动作完成反馈");
            }
            if (API.Agvs.Any(a => !pausedAgvs.Any(s => s.Agv == a)) ||
                pausedAgvs.Any(s => !API.Agvs.Any(a => a == s.Agv)))
                result.Issues.Add("暂停期间车辆集合发生变化，需人工核对");
            result.CanResume = result.Issues.Count == 0;
            result.Stage = result.CanResume ? "Paused" : "Pausing";
            return result;
        }

        /// <summary>恢复原导航及业务。任一指令失败则重新取消导航并保留暂停快照。</summary>
        public GlobalPauseResult ResumeGlobal()
        {
            lock (cargoBatchSync)
            {
                var result = ReadGlobalPauseStatus();
                if (!globalPaused) return result; // 重复恢复不下发导航。
                if (!result.CanResume)
                {
                    result.Success = false;
                    result.Message = "尚不满足恢复条件，请查看 Issues";
                    return result;
                }
                try
                {
                    foreach (var saved in pausedAgvs)
                    {
                        if (saved.Target == null || saved.Agv.API.On == saved.Target) continue;
                        if (!saved.Agv.OnLine) throw new InvalidOperationException(saved.Agv.Name + " 已下线，不能恢复导航");
                        // 不重新设置 staAction，也不调用 MoveStage，保留原步骤及动作反馈。
                        var navigation = saved.Agv.GoTo(saved.Target);
                        if (navigation == null || !navigation.Success)
                            throw new InvalidOperationException(saved.Agv.Name + " 导航未受理：" + navigation?.Message);
                    }
                }
                catch (Exception ex)
                {
                    foreach (var saved in pausedAgvs) CancelPausedNavigation(saved);
                    result = ReadGlobalPauseStatus();
                    result.Success = false;
                    result.Message = "恢复导航失败，保持全局暂停：" + ex.Message;
                    result.Issues.Add(result.Message);
                    return result;
                }
                globalPaused = false;
                pausedAgvs.Clear();
                Tools.trace($"[全局恢复] 编号={globalPauseId}");
                return ReadGlobalPauseStatus();
            }
        }
    }
}

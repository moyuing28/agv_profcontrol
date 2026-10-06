namespace ProfControl
{
    public partial class SampleProject
    {
        public enum TaskTypeEnum
        {
            移动任务,
            移库任务
        }

        public enum TaskStatus
        {
            None,
            GoPick,
            Picking,
            Picked,
            GoDrop,
            Dropping,
            Dropped,
            GoHome,
            Finished,
            GoMove,
            GoPickEntrance,
            GoPickExit,
            GoDropEntrance,
            GoDropExit,
            ExitedDrop,
            GoParkingExit,
            NeedsReview,
        }

        public enum CargoMoveType
        {
            PointToPoint,
            PocketToPocket,
            AreaToArea
        }

        public enum CargoBatchStatus
        {
            Waiting,
            Running,
            Completed,
            NeedsReview
        }

        public enum CargoTaskStatus
        {
            Waiting,
            Executing,
            Completed,
            NeedsReview
        }

        // 一次客户搬运要求。巷道搬运的范围标识使用子分类名称。
        public class CargoMoveBatch
        {
            public string BatchId { get; set; } = Guid.NewGuid().ToString();
            public CargoMoveType MoveType { get; set; }
            public string SourceScopeId { get; set; }
            public string TargetScopeId { get; set; }
            // 仅供内部调度使用，不作为 CreatePocketMoveBatch 的调用参数。
            public string TargetPocketKey { get; set; }
            public List<string> SourcePocketNames { get; set; } = new List<string>();
            public List<string> TargetPocketNames { get; set; } = new List<string>();
            public List<string> SourcePocketKeys { get; set; } = new List<string>();
            public List<string> TargetPocketKeys { get; set; } = new List<string>();
            internal int NextSourceIndex { get; set; }
            public CargoBatchStatus Status { get; set; } = CargoBatchStatus.Waiting;
            public DateTime CreatedTime { get; set; } = DateTime.Now;
            public DateTime? CompletedTime { get; set; }
            public List<CargoMoveItem> Items { get; set; } = new List<CargoMoveItem>();

            // 从明细计算，避免维护两份数量造成不一致。
            public int TotalCount => Items.Count;
            public int CompletedCount => Items.Count(i => i.Status == CargoTaskStatus.Completed);
        }

        // 每件货一条业务明细；等待时不创建执行任务，也不要求已分配卸货位。
        // 只存稳定标识，不存 IAgv / IStation 等现场对象，便于后续持久化。
        public class CargoMoveItem
        {
            public string ItemId { get; set; } = Guid.NewGuid().ToString();
            public string BatchId { get; set; }
            public string CargoName { get; set; }
            public string CargoTag { get; set; }
            public string TargetMark { get; set; }
            public List<string> TargetPocketNames { get; set; } = new List<string>();
            internal List<TaskInfo> ExecutionSteps { get; set; } = new List<TaskInfo>();
            public string SourcePocketKey { get; set; }
            public string SourceMark { get; set; }
            // 来源巷道内从外到内的顺序，从 1 开始；不是整批强制串行顺序。
            public int PickSequence { get; set; }
            // 客户指定范围保存在所属批次；以下字段仅记录实际分配结果。
            public string AssignedDropPocketKey { get; set; }
            public string AssignedDropMark { get; set; }
            public CargoTaskStatus Status { get; set; } = CargoTaskStatus.Waiting;
            public string WaitReason { get; set; }
            public string ExecutionTaskId { get; set; }
            public string AssignedAgv { get; set; }
            public TaskStatus? ExecutionStatus { get; set; }
            public DateTime? CompletedTime { get; set; }
        }

        public class CargoPocketCapacity
        {
            public string PocketName { get; set; }
            public string PocketKey { get; set; }
            public int ReachableEmptyCount { get; set; }
            public int ReservedCount { get; set; }
            public int AvailableCount => Math.Max(0, ReachableEmptyCount - ReservedCount);
        }

        public class CargoBatchCreateResult
        {
            public bool Success { get; set; }
            public string Error { get; set; }
            public CargoMoveBatch Batch { get; set; }
            public int RequiredCount { get; set; }
            public List<CargoPocketCapacity> TargetCapacities { get; set; } = new List<CargoPocketCapacity>();
        }

        // 查询结果为独立快照，修改返回对象不会修改任务池。
        public class CargoBatchSummary
        {
            public List<string> SourcePocketNames { get; set; } = new List<string>();
            public List<string> TargetPocketNames { get; set; } = new List<string>();
            public List<CargoPocketCapacity> TargetCapacities { get; set; } = new List<CargoPocketCapacity>();
            public string BatchId { get; set; }
            public CargoMoveType MoveType { get; set; }
            public string SourceScopeId { get; set; }
            public string TargetScopeId { get; set; }
            public CargoBatchStatus Status { get; set; }
            public string StatusText { get; set; }
            public DateTime CreatedTime { get; set; }
            public DateTime? CompletedTime { get; set; }
            public int TotalCount { get; set; }
            public int WaitingCount { get; set; }
            public int ExecutingCount { get; set; }
            public int CompletedCount { get; set; }
            public int NeedsReviewCount { get; set; }
        }

        public class CargoBatchDetail : CargoBatchSummary
        {
            public List<CargoItemDetail> Items { get; set; } = new List<CargoItemDetail>();
        }

        public class CargoItemDetail : CargoMoveItem
        {
            public List<CargoStepDetail> Steps { get; set; } = new List<CargoStepDetail>();
            public int TotalStepCount => Steps.Count;
            public int CompletedStepCount => Steps.Count(s => s.Status == TaskStatus.Finished);
            public string StatusText { get; set; }
            // 未生成执行任务时为空，不能把等待明细显示成已经开始执行。
            public string ExecutionStatusText { get; set; }
        }

        public enum CargoStepType { Clearance, Staging, TargetMove }

        public class CargoStepDetail
        {
            public string TaskId { get; set; }
            public int Sequence { get; set; }
            public CargoStepType StepType { get; set; }
            public string CargoName { get; set; }
            public string CargoTag { get; set; }
            public string FromMark { get; set; }
            public string ToMark { get; set; }
            public string DropPocketKey { get; set; }
            public string AgvName { get; set; }
            public bool Activated { get; set; }
            public TaskStatus Status { get; set; }
            public string StatusText { get; set; }
            public string WaitReason { get; set; }
        }

        public class CargoEditResult
        {
            public bool Success { get; set; }
            public string Error { get; set; }
            public string CargoName { get; set; }
            public string CargoTag { get; set; }
            public string StationMark { get; set; }
        }

        public class CargoMoveCreateResult
        {
            public string BatchId { get; set; }
            public string ItemId { get; set; }
            public bool Success { get; set; }
            public string Error { get; set; }
            public string CargoName { get; set; }
            public string CargoTag { get; set; }
            public string FromMark { get; set; }
            public string ToMark { get; set; }
        }

        public class TaskInfo
        {
            public string TaskId { get; set; } = Guid.NewGuid().ToString();
            // 按货物创建的任务使用类型 + Tag 核验取货身份。
            public string CargoName { get; set; }
            public string CargoTag { get; set; }
            public CargoStepType StepType { get; set; } = CargoStepType.TargetMove;
            // 业务批次关联，与下方已有的清障步骤队列编号分开。
            public string CargoBatchId { get; set; }
            public string CargoItemId { get; set; }
            public string FromMark { get; set; }
            public string ToMark { get; set; }
            public string ParkingExitMark { get; set; }
            public string ParkingResumeMark { get; set; }
            public TaskStatus ParkingResumeStatus { get; set; }
            public string DropPocketKey { get; set; }
            public bool DynamicDrop { get; set; }
            public bool DropCapacityReserved { get; set; }
            public string PickEntranceMark { get; set; }
            public string DropEntranceMark { get; set; }
            public bool SamePocket { get; set; }
            public string LastWaitReason { get; set; }
            public IAgv WorkAgv { get; set; }
            public string RequestedAgv { get; set; }
            public TaskTypeEnum TaskType { get; set; }
            public TaskStatus Status { get; set; } = TaskStatus.None;
            public DateTime StateTime { get; set; } = DateTime.Now;

            // 巷道锁（取货 + 卸货）
            public string LockPickPocketKey { get; set; }
            public string LockDropPocketKey { get; set; }
            // 清障步骤组：串行清理取卸货两端阻碍 + 搬走目标货物，不包含自动归位。
            public string ClearanceBatchId { get; set; }
            public int ClearanceSequence { get; set; }
        }

        public class SelectableStationInfo
        {
            public string StationMark { get; set; }
            public string PocketName { get; set; }
            public bool HasCargo { get; set; }
        }
    }
}

namespace ProfControl
{
    public partial class SampleProject
    {

        void InitAgvParking()
        {
            API.Agvs.Foreach(agv =>
            {
                var sta = API.GetStation(agv.GetValue<string>("parkingstaID"));
                agv.TaskKillAll();
                agv.TakeAll();
                agv.View.Radian = 0;
                agv.View.Move(sta.View.KeyPoint, true);
                agv.View.CalcBound();
                agv.View.SafeInvalidate();
            });
        }

    }
}

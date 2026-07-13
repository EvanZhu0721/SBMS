namespace SBMSGui
{
    internal sealed class DisplayChoice
    {
        public int Number;
        public string DeviceName;
        public string Resolution;
        public string Refresh;
        public string Name;
        public string SunshineId;
        public int Orientation;
        public bool Primary;
        public bool Virtual;

        public override string ToString()
        {
            return Number + "  " + DeviceName + "  " + Resolution + "@" + Refresh +
                   (Primary ? "  基准" : "") +
                   (Virtual ? "  虚拟" : "") +
                   "  " + Name;
        }
    }

    internal sealed class DisplayRuntimeMode
    {
        public Resolution Resolution;
        public string Refresh;
        public int Orientation;
    }
}

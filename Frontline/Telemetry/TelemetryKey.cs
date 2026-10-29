namespace FrontLineOverlay
{

    internal static partial class TelemetryKey
    {
        public static string Value
        {
            get
            {
                string key = "";
                Load(ref key);
                return key;
            }
        }

        static partial void Load(ref string key);
    }
}

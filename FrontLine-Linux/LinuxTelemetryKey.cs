namespace FrontLine_Linux;

internal static partial class LinuxTelemetryKey
{
    internal static string Value
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

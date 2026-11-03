namespace FrontLine_Linux;

// The implementation lives in LinuxTelemetryKey.local.cs (ignored by Git).
// When absent, the partial method is omitted and Value is empty.
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

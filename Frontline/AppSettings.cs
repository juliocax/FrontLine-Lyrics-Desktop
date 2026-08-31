using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace FrontLineOverlay
{
    /// <summary>
    /// Preferência: Windows.Storage.ApplicationData.LocalSettings (MSIX, sobrevive
    /// a atualização da Store). Se o app estiver unpackaged no Visual Studio, cai
    /// para %LOCALAPPDATA%\FrontLineLyrics\settings.json.
    ///
    /// Persistência de fonte / Auto / posição: ideia original de Warith Adetayo
    /// (%APPDATA% JSON), adaptada para ApplicationData nesta build empacotada.
    /// Posição fica em LocalSettings de propósito — outro monitor / outro PC
    /// não deve herdar coordenadas.
    /// </summary>
    internal static class AppSettings
    {
        private static readonly object Gate = new();
        private static object? _winrtValues;
        private static MethodInfo? _winrtHasKey;
        private static MethodInfo? _winrtLookup;
        private static MethodInfo? _winrtInsert;
        private static Dictionary<string, JsonElement>? _fileCache;
        private static bool _probed;
        private static bool _usingWinRt;

        public static double GetDouble(string key, double fallback)
        {
            try
            {
                if (TryGetRaw(key, out var raw) && raw != null)
                    return Convert.ToDouble(raw);
            }
            catch (Exception ex) { CrashReporter.Log(ex, "AppSettings.GetDouble"); }
            return fallback;
        }

        public static bool GetBool(string key, bool fallback)
        {
            try
            {
                if (TryGetRaw(key, out var raw) && raw != null)
                    return Convert.ToBoolean(raw);
            }
            catch (Exception ex) { CrashReporter.Log(ex, "AppSettings.GetBool"); }
            return fallback;
        }

        public static void SetDouble(string key, double value) => Set(key, value);
        public static void SetBool(string key, bool value) => Set(key, value);

        public static void Set(string key, object value)
        {
            try
            {
                EnsureBackends();
                if (_usingWinRt && _winrtValues != null)
                {
                    WinRtSet(key, value);
                    return;
                }
                lock (Gate)
                {
                    _fileCache ??= LoadFile();
                    _fileCache[key] = JsonSerializer.SerializeToElement(value);
                    SaveFile();
                }
            }
            catch (Exception ex) { CrashReporter.Log(ex, "AppSettings.Set"); }
        }

        private static bool TryGetRaw(string key, out object? raw)
        {
            raw = null;
            EnsureBackends();
            if (_usingWinRt && _winrtValues != null)
                return WinRtTryGet(key, out raw);
            lock (Gate)
            {
                _fileCache ??= LoadFile();
                if (!_fileCache.TryGetValue(key, out var el)) return false;
                raw = el.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Number => el.GetDouble(),
                    JsonValueKind.String => el.GetString(),
                    _ => el.ToString()
                };
                return true;
            }
        }

        private static void EnsureBackends()
        {
            if (_probed) return;
            _probed = true;
            _usingWinRt = TryOpenWinRtSettings();
        }

        private static bool TryOpenWinRtSettings()
        {
            try
            {
                Type? t = Type.GetType("Windows.Storage.ApplicationData, Microsoft.Windows.SDK.NET")
                          ?? Type.GetType("Windows.Storage.ApplicationData");
                if (t == null)
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        t = asm.GetType("Windows.Storage.ApplicationData");
                        if (t != null) break;
                    }
                }
                if (t == null) return false;
                object? current = t.GetProperty("Current")?.GetValue(null);
                if (current == null) return false;
                object? local = current.GetType().GetProperty("LocalSettings")?.GetValue(current);
                object? values = local?.GetType().GetProperty("Values")?.GetValue(local);
                if (values == null) return false;

                Type vt = values.GetType();
                _winrtHasKey = vt.GetMethod("HasKey", new[] { typeof(string) })
                               ?? vt.GetMethod("ContainsKey", new[] { typeof(string) });
                _winrtLookup = vt.GetMethod("Lookup", new[] { typeof(string) })
                               ?? vt.GetMethod("get_Item", new[] { typeof(string) });
                _winrtInsert = vt.GetMethod("Insert", new[] { typeof(string), typeof(object) });

                if (_winrtLookup == null && values is IDictionary)
                {
                    _winrtValues = values;
                    CrashReporter.Info("AppSettings: ApplicationData.LocalSettings");
                    return true;
                }

                if (_winrtHasKey == null || _winrtLookup == null)
                    return false;

                _winrtValues = values;
                CrashReporter.Info("AppSettings: ApplicationData.LocalSettings");
                return true;
            }
            catch (Exception ex)
            {
                CrashReporter.Log(ex, "AppSettings.WinRT");
                _winrtValues = null;
                return false;
            }
        }

        private static bool WinRtTryGet(string key, out object? raw)
        {
            raw = null;
            if (_winrtValues is IDictionary dict && _winrtHasKey == null)
            {
                if (!dict.Contains(key)) return false;
                raw = dict[key];
                return raw != null;
            }
            object? has = _winrtHasKey?.Invoke(_winrtValues, new object[] { key });
            if (has is not true) return false;
            raw = _winrtLookup?.Invoke(_winrtValues, new object[] { key });
            return raw != null;
        }

        private static void WinRtSet(string key, object value)
        {
            if (_winrtValues is IDictionary dict && _winrtInsert == null)
            {
                dict[key] = value;
                return;
            }
            if (_winrtInsert != null)
            {
                _winrtInsert.Invoke(_winrtValues, new object[] { key, value });
                return;
            }
            var item = _winrtValues!.GetType().GetProperty("Item");
            item?.SetValue(_winrtValues, value, new object[] { key });
        }

        private static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FrontLineLyrics", "settings.json");

        private static Dictionary<string, JsonElement> LoadFile()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var json = File.ReadAllText(FilePath);
                    var doc = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
                    if (doc != null) return doc;
                }
            }
            catch (Exception ex) { CrashReporter.Log(ex, "AppSettings.LoadFile"); }
            return new Dictionary<string, JsonElement>();
        }

        private static void SaveFile()
        {
            if (_fileCache == null) return;
            string dir = Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(_fileCache));
        }
    }
}

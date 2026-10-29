using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Frontline.Themes.Seasonal
{
    internal enum SeasonalTheme { None, Halloween, Christmas, NewYear }

    internal static class SeasonalThemeProvider
    {
        // ISO 3166-1 alpha-2 codes
        private static readonly HashSet<string> HalloweenRegions = new(StringComparer.OrdinalIgnoreCase)
        {
            // The Americas 
            "US", "CA", "BR", "MX", "AR", "CL", "CO", "PE", "VE", "EC", "SV", "DO", "CR", "BO", "GT", "PA", "JM", "HT", "PR",
            // Europe 
            "GB", "SE", "DE", "PT", "IT", "NL", "ES", "CH", "IE", "LT", "UA", "BE", "FR", "RU", "GR", "AT", "NO", "GE", "RO", "PL", "DK", "FI",
            // Asia and Oceania
            "PH", "AU", "SG", "VN", "TW", "TH", "JP", "HK", "KR", "NZ", "PG",
            // Africa
            "NG", "ZW", "GH", "KE", "TG", "ZA", "MW", "TZ", "CI", "CM", "RE"
        };

        private static readonly HashSet<string> ChristmasRegions = new(StringComparer.OrdinalIgnoreCase)
        {
            // Christmas encompasses ALL the countries that celebrate Halloween...
            "US", "CA", "BR", "MX", "AR", "CL", "CO", "PE", "VE", "EC", "SV", "DO", "CR", "BO", "GT", "PA", "JM", "HT", "PR",
            "GB", "SE", "DE", "PT", "IT", "NL", "ES", "CH", "IE", "LT", "UA", "BE", "FR", "RU", "GR", "AT", "NO", "GE", "RO", "PL", "DK", "FI",
            "PH", "AU", "SG", "VN", "TW", "TH", "JP", "HK", "KR", "NZ", "PG",
            "NG", "ZW", "GH", "KE", "TG", "ZA", "MW", "TZ", "CI", "CM", "RE",
          
            "ID", "MY", "IN", "MM", "CN", "LB", "NP", "LK", "LA", "KZ", "MN", "AE", "BA", "ET"
        };

        private static readonly HashSet<string> NewYearRegions = new(StringComparer.OrdinalIgnoreCase)
        {
            // The Gregorian New Year.
            "US", "CA", "BR", "MX", "AR", "CL", "CO", "PE", "VE", "EC", "SV", "DO", "CR", "BO", "GT", "PA", "JM", "HT", "PR",
            "GB", "SE", "DE", "PT", "IT", "NL", "ES", "CH", "IE", "LT", "UA", "BE", "FR", "RU", "GR", "AT", "NO", "GE", "RO", "PL", "DK", "FI",
            "PH", "AU", "SG", "VN", "TW", "TH", "JP", "HK", "KR", "NZ", "PG",
            "NG", "ZW", "GH", "KE", "TG", "ZA", "MW", "TZ", "CI", "CM", "RE",
            "ID", "MY", "IN", "MM", "CN", "LB", "NP", "LK", "LA", "KZ", "MN", "AE", "BA", "ET",
            // While New Year’s Day (January 1st) is well-received and marketed, the same cannot necessarily be said for Christmas and Halloween.
            "EG", "MA", "PK", "IQ", "SA", "TR", "BD", "KW", "DZ", "JO"
        };

        // dates
        private static bool InHalloweenWindow(DateTime d) =>
            (d.Month == 10 && d.Day >= 15) || (d.Month == 11 && d.Day == 1);

        private static bool InChristmasWindow(DateTime d) =>
            d.Month == 12 && d.Day >= 1 && d.Day <= 26;

        private static bool InNewYearWindow(DateTime d) =>
            (d.Month == 12 && d.Day >= 30) || (d.Month == 1 && d.Day <= 3);

        // system region (Windows Settings > Time & language > Region
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int GetUserDefaultGeoName(StringBuilder geoName, int count);

        public static string GetSystemRegion()
        {
            try
            {
                var sb = new StringBuilder(24);
                if (GetUserDefaultGeoName(sb, sb.Capacity) > 0)
                    return sb.ToString();
            }
            catch { /* API unavailable: falling back */ }

            try { return RegionInfo.CurrentRegion.TwoLetterISORegionName; }
            catch { return ""; }
        }

        public static SeasonalTheme GetCurrent(DateTime? now = null)
        {
            // For testing
            // FRONTLINE_SEASON = halloween | christmas | newyear | none
            var forced = Environment.GetEnvironmentVariable("FRONTLINE_SEASON");
            if (!string.IsNullOrWhiteSpace(forced))
            {
                switch (forced.Trim().ToLowerInvariant())
                {
                    case "halloween": return SeasonalTheme.Halloween;
                    case "christmas": return SeasonalTheme.Christmas;
                    case "newyear":
                    case "new-year":
                    case "ano-novo":
                    case "anonovo": return SeasonalTheme.NewYear;
                    case "none": return SeasonalTheme.None;
                }
            }

            var date = now ?? DateTime.Now;      // system date
            var region = GetSystemRegion();      // system region
            if (string.IsNullOrEmpty(region)) return SeasonalTheme.None;

            if (InNewYearWindow(date) && NewYearRegions.Contains(region)) return SeasonalTheme.NewYear;
            if (InHalloweenWindow(date) && HalloweenRegions.Contains(region)) return SeasonalTheme.Halloween;
            if (InChristmasWindow(date) && ChristmasRegions.Contains(region)) return SeasonalTheme.Christmas;
            return SeasonalTheme.None;
        }
    }
}
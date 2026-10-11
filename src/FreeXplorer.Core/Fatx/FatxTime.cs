namespace FreeXplorer.Fatx;

// FATX timestamps are plain FAT date/time words in local time. The 360 counts years from
// 1980. The original Xbox counts from 2000, going by the docs.
public static class FatxTime
{
    // raw layout used everywhere in this code: date in the high word, time in the low word
    public static uint Encode(DateTime local, FatxFlavor flavor)
    {
        int baseYear = BaseYear(flavor);
        int year = Math.Clamp(local.Year, baseYear, baseYear + 127);
        uint date = (uint)(((year - baseYear) << 9) | (local.Month << 5) | local.Day);
        uint time = (uint)((local.Hour << 11) | (local.Minute << 5) | (local.Second / 2));
        return (date << 16) | time;
    }

    public static DateTime Decode(uint raw, FatxFlavor flavor)
    {
        int date = (int)(raw >> 16);
        int time = (int)(raw & 0xFFFF);
        int year = BaseYear(flavor) + (date >> 9);
        int month = Math.Clamp((date >> 5) & 0xF, 1, 12);
        int day = Math.Clamp(date & 0x1F, 1, DateTime.DaysInMonth(year, month));
        int hour = Math.Min(time >> 11, 23);
        int minute = Math.Min((time >> 5) & 0x3F, 59);
        int second = Math.Min((time & 0x1F) * 2, 59);
        return new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified);
    }

    public static DateTime ToUtc(DateTime fatxLocal)
    {
        var local = DateTime.SpecifyKind(fatxLocal, DateTimeKind.Unspecified);
        try
        {
            return TimeZoneInfo.ConvertTimeToUtc(local, TimeZoneInfo.Local);
        }
        catch (ArgumentException)
        {
            // lands in a DST gap, just treat it as UTC
            return DateTime.SpecifyKind(local, DateTimeKind.Utc);
        }
    }

    public static DateTime FromUtc(DateTime utc) =>
        DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), TimeZoneInfo.Local), DateTimeKind.Unspecified);

    static int BaseYear(FatxFlavor flavor) => flavor == FatxFlavor.Xbox360 ? 1980 : 2000;
}

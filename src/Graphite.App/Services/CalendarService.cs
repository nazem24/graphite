using System.IO;
using System.Net.Http;
using Graphite.Core.Canvas;

namespace Graphite.App.Services;

/// <summary>Adding and removing calendar links (.ics) for the Canvas calendar. Used by the calendar page and by
/// Settings › Canvas, so both behave the same.</summary>
public static class CalendarService
{
    /// <summary>Download the calendar to make sure it works, keep a copy in the cache, and remember the link.</summary>
    public static async Task<(bool Ok, string Message)> AddSourceAsync(string url, CancellationToken ct = default)
    {
        url = (url ?? "").Trim();
        if (url.Length == 0) return (false, "Paste the calendar's .ics address first.");

        string address;
        string text;
        IcsCalendar calendar;
        try
        {
            address = IcsParser.NormalizeUrl(url);
            if (CanvasSettings.CalendarSources().Any(s => SameAddress(s.Url, address)))
                return (false, "That calendar is already added.");
            text = await IcsFetcher.DownloadAsync(address, ct);
            calendar = IcsParser.Parse(text);
        }
        catch (Exception ex) when (ex is HttpRequestException or FormatException or ArgumentException or IOException or TaskCanceledException)
        {
            return (false, Describe(ex));
        }

        string name = calendar.Name.Length > 0 ? calendar.Name : new Uri(address).Host;
        string id = Guid.NewGuid().ToString("N");
        new CanvasCache(CanvasSettings.CacheRoot).SaveText(CanvasSettings.CalendarCacheName(id), text);
        CanvasSettings.AddCalendarSource(id, name, address);
        return (true, $"Added “{name}”: {calendar.EventCount} events.");
    }

    public static void Remove(string id)
    {
        new CanvasCache(CanvasSettings.CacheRoot).DeleteText(CanvasSettings.CalendarCacheName(id));
        CanvasSettings.RemoveCalendarSource(id);
    }

    private static bool SameAddress(string a, string b)
    {
        try { return string.Equals(IcsParser.NormalizeUrl(a), IcsParser.NormalizeUrl(b), StringComparison.OrdinalIgnoreCase); }
        catch (ArgumentException) { return false; }
    }

    private static string Describe(Exception ex) => ex switch
    {
        TaskCanceledException => "The calendar address did not answer in time.",
        HttpRequestException h when h.StatusCode == null => "Couldn't reach that address. Check it and your internet connection.",
        _ => ex.Message,
    };
}

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Graphite.Core.Canvas;

/// <summary>A course the person is enrolled in, with the term it belongs to.</summary>
public sealed class CanvasCourse
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Code { get; set; } = "";
    public string TermId { get; set; } = "0";
    public string TermName { get; set; } = "";
    public DateTime? TermStartUtc { get; set; }
    public DateTime? TermEndUtc { get; set; }
}

/// <summary>A term (semester) as the course list reports it.</summary>
public sealed record CanvasTerm(string Id, string Name, DateTime? StartUtc, DateTime? EndUtc);

/// <summary>What a row in a module stands for.</summary>
public enum CanvasItemKind { Header, File, Page, Assignment, Quiz, Discussion, Link, Tool, Other }

/// <summary>A file's download address and facts, as Canvas reports them. <see cref="Url"/> is a
/// short-lived, signed download link (it redirects to a file host).</summary>
public sealed class CanvasFileInfo
{
    public string Url { get; set; } = "";
    public string Name { get; set; } = "";
    public long? Size { get; set; }
    public string Mime { get; set; } = "";
    public DateTime? UpdatedUtc { get; set; }
}

/// <summary>How to find a file: its Canvas id, the API endpoint a page link names (if any), the
/// page address, and the download facts when the module item already carried them.</summary>
public sealed class CanvasFileRef
{
    public string Id { get; set; } = "";
    public string? Endpoint { get; set; }
    public string Href { get; set; } = "";
    public CanvasFileInfo? Direct { get; set; }
}

/// <summary>One row of a module (or a link found inside a wiki page). <see cref="Key"/> is
/// stable across refreshes ("file:123", "item:456", "link:https://…") and is what the "seen"
/// sets and pins remember.</summary>
public sealed class CanvasItem
{
    public string Key { get; set; } = "";
    public CanvasItemKind Kind { get; set; } = CanvasItemKind.Other;
    public string Title { get; set; } = "";
    public int Indent { get; set; }
    public string Url { get; set; } = "";
    public bool Locked { get; set; }
    public DateTime? DueUtc { get; set; }
    public double? Points { get; set; }
    public CanvasFileRef? File { get; set; }
    /// <summary>Files and links found in a page's text (pages only).</summary>
    public List<CanvasItem>? Links { get; set; }
}

public sealed class CanvasModule
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTime? UnlockUtc { get; set; }
    public List<CanvasItem> Items { get; set; } = new();
}

/// <summary>The course's start page and what it links to.</summary>
public sealed class CanvasFrontPage
{
    public string Title { get; set; } = "Start page";
    public string Url { get; set; } = "";
    public List<CanvasItem> Links { get; set; } = new();

    /// <summary>The page's HTML, shown as a page in the course view. Null in caches written before it was kept
    /// (the course is then loaded again once).</summary>
    public string? Body { get; set; }
}

/// <summary>Everything Graphite shows of one course: its start page and its modules.</summary>
public sealed class CanvasCourseData
{
    public DateTime FetchedUtc { get; set; }
    public CanvasFrontPage? Front { get; set; }
    public List<CanvasModule> Modules { get; set; } = new();

    /// <summary>What the course contains, without when it was fetched: two loads with the same signature show the
    /// same page, so a refresh that found nothing new does not have to rebuild it.</summary>
    public string Signature() =>
        System.Text.Json.JsonSerializer.Serialize(new { Front, Modules }, CanvasJson.Options);
}

/// <summary>Raised for an answer from Canvas that is not a success (401 wrong token, 404 …).</summary>
public sealed class CanvasApiException : Exception
{
    public int Status { get; }
    public CanvasApiException(int status, string message) : base(message) => Status = status;
}

/// <summary>Shared JSON settings for everything Canvas writes to disk.</summary>
public static class CanvasJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };
}

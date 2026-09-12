namespace PBIExplorer.Models;

public class DashboardSummary
{
    public string OverviewText { get; set; } = "";
    public List<string> Highlights { get; } = new();
    public List<PageSummary> Pages { get; } = new();
}

public class PageSummary
{
    public string PageName { get; set; } = "";
    public int VisualCount { get; set; }
    public string Description { get; set; } = "";
    public List<string> KeyMeasures { get; } = new();
    public List<string> FilterColumns { get; } = new();
    public List<string> SourceTables { get; } = new();
    public string VisualTypeBreakdown { get; set; } = "";
}

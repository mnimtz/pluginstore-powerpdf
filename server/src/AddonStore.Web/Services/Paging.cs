namespace AddonStore.Web.Services;

/// <summary>
/// Paged lists (S0.17.2): 25, 50 or 100 rows per page (?size=), the page of each
/// list in ?p_&lt;key&gt;=, so several lists on one page page independently.
/// Rendered with the partial _Pager (model: <see cref="PagerModel"/>).
/// </summary>
public static class Paging
{
    public static readonly int[] Sizes = { 25, 50, 100 };

    public static int Size(HttpRequest request) =>
        int.TryParse(request.Query["size"], out var s) && Sizes.Contains(s) ? s : Sizes[0];

    public static int PageCount(int total, int size) => Math.Max(1, (total + size - 1) / size);

    /// <summary>Current page of a list (1-based, clamped to the list).</summary>
    public static int Page(HttpRequest request, string key, int total, int size) =>
        Math.Min(int.TryParse(request.Query["p_" + key], out var p) && p > 0 ? p : 1, PageCount(total, size));

    /// <summary>The rows of the current page of an in-memory list.</summary>
    public static IEnumerable<T> Items<T>(IReadOnlyList<T> list, HttpRequest request, string key)
    {
        var size = Size(request);
        return list.Skip((Page(request, key, list.Count, size) - 1) * size).Take(size);
    }

    /// <summary>Rows to skip for a list paged in the database.</summary>
    public static int Skip(HttpRequest request, string key, int total) =>
        (Page(request, key, total, Size(request)) - 1) * Size(request);
}

/// <summary>Model of the _Pager partial: which list, how many rows in total.</summary>
public record PagerModel(string Key, int Total);

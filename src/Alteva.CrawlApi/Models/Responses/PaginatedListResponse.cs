using System;
using System.Collections.Generic;

namespace Alteva.CrawlApi.Models.Responses;

/// <summary>
/// Generic paginated response envelope.
/// </summary>
/// <typeparam name="T">Item type.</typeparam>
public class PaginatedListResponse<T>
{
    public IReadOnlyList<T> Items { get; set; } = [];
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalPages => PageSize > 0 ? (int)Math.Ceiling((double)TotalCount / PageSize) : 0;
    public bool HasNextPage => Page < TotalPages;
    public bool HasPreviousPage => Page > 1;
}

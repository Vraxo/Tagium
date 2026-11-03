using Microsoft.AspNetCore.Components;
using Tagium.Photino.Blazor.Services;

namespace Tagium.Photino.Blazor.Components.Shared;

public record PageItem(int? PageNumber, string DisplayText, bool IsActive);

public partial class PaginationControls : IDisposable
{
    [Inject] private AppState AppState { get; set; } = default!;

    protected override void OnInitialized()
    {
        AppState.OnChange += OnStateChanged;
    }

    private void OnStateChanged()
    {
        _ = InvokeAsync(StateHasChanged);
    }

    private void GoToPage(int page)
    {
        AppState.SetPage(page);
    }

    private int GetStartItemIndex()
    {
        if (AppState.TotalFilesCount == 0)
        {
            return 0;
        }

        return ((AppState.CurrentPage - 1) * AppState.PageSize) + 1;
    }

    private int GetEndItemIndex()
    {
        int end = AppState.CurrentPage * AppState.PageSize;
        return end > AppState.TotalFilesCount ? AppState.TotalFilesCount : end;
    }

    private List<PageItem> GetPageItems()
    {
        List<PageItem> items = [];
        int total = AppState.TotalPages;
        int current = AppState.CurrentPage;

        if (total <= 7)
        {
            for (int i = 1; i <= total; i++)
            {
                items.Add(new PageItem(i, i.ToString(), i == current));
            }
        }
        else
        {
            // First Page
            items.Add(new PageItem(1, "1", current == 1));

            // Ellipsis or early pages
            if (current > 3)
            {
                items.Add(new PageItem(null, "...", false));
            }

            int startPage = Math.Max(2, current - 1);
            int endPage = Math.Min(total - 1, current + 1);

            if (current <= 3)
            {
                startPage = 2;
                endPage = 4;
            }
            else if (current >= total - 2)
            {
                startPage = total - 3;
                endPage = total - 1;
            }

            for (int i = startPage; i <= endPage; i++)
            {
                items.Add(new PageItem(i, i.ToString(), i == current));
            }

            // Ellipsis or late pages
            if (current < total - 2)
            {
                items.Add(new PageItem(null, "...", false));
            }

            // Last Page
            items.Add(new PageItem(total, total.ToString(), current == total));
        }

        return items;
    }

    public void Dispose()
    {
        AppState.OnChange -= OnStateChanged;
    }
}
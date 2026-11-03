using Microsoft.AspNetCore.Components;
using Tagium.Core.Library;

namespace Tagium.Photino.Blazor.Files;

public partial class RatingStars : IDisposable
{
    [Inject] private LibraryService Library { get; set; } = default!;

    [Parameter, EditorRequired] public required IEnumerable<Guid> FileIds { get; set; }

    private int CurrentRating { get; set; }
    private bool MixedRatings { get; set; }

    protected override void OnInitialized()
    {
        Library.LibraryChanged += OnLibraryChanged;
    }

    protected override void OnParametersSet()
    {
        LoadRating();
    }

    private void LoadRating()
    {
        List<Guid> ids = [.. FileIds];
        if (ids.Count == 0)
        {
            CurrentRating = 0;
            MixedRatings = false;
            return;
        }

        List<int?> ratings = ids.Select(id => Library.GetFileById(id)?.Rating).ToList();
        int? first = ratings[0];

        if (ratings.All(r => r == first))
        {
            CurrentRating = first ?? 0;
            MixedRatings = false;
        }
        else
        {
            // Selection has differing ratings: show empty stars
            CurrentRating = 0;
            MixedRatings = true;
        }
    }

    private void SetRating(int star)
    {
        // Clicking the current rating again clears it
        int? newRating = CurrentRating == star ? null : star;
        Library.SetRatings(FileIds, newRating);
    }

    private void ClearRating()
    {
        Library.SetRatings(FileIds, null);
    }

    private void OnLibraryChanged()
    {
        _ = InvokeAsync(() =>
        {
            LoadRating();
            StateHasChanged();
        });
    }

    public void Dispose()
    {
        Library.LibraryChanged -= OnLibraryChanged;
    }
}

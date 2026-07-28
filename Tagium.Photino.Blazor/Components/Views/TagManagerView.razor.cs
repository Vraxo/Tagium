using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Tagium.Core.Models;
using Tagium.Core.Settings;

namespace Tagium.Photino.Blazor.Components.Views;

public partial class TagManagerView
{
    private List<Tag> Tags = [];
    private string NewTagName = "";
    private Guid? ActiveColorPickerId = null;

    private static readonly List<string> Palette =
    [
        "#e53935",
        "#d81b60",
        "#8e24aa",
        "#5e35b1",
        "#3949ab",
        "#1e88e5",
        "#039be5",
        "#00acc1",
        "#00897b",
        "#43a047",
        "#7cb342",
        "#c0ca33",
        "#fdd835",
        "#ffb300",
        "#fb8c00",
        "#f4511e",
        "#6d4c41",
        "#757575",
        "#546e7a",
        "#FFFFFF",
        "#000000"
    ];

    protected override void OnInitialized()
    {
        RefreshTags();
        Library.LibraryChanged += RefreshTags;
        Settings.SettingsChanged += StateHasChanged;
    }

    private void RefreshTags()
    {
        _ = Task.Run(async () =>
        {
            List<Tag> tags = Library.GetTags(Settings.Current.TagSortBy, Settings.Current.TagSortDirection);
            await InvokeAsync(() =>
            {
                Tags = tags;
                StateHasChanged();
            });
        });
    }

    private void HandleKeyUp(KeyboardEventArgs e)
    {
        if (e.Key != "Enter")
        {
            return;
        }

        AddTag();
    }

    private void AddTag()
    {
        if (string.IsNullOrWhiteSpace(NewTagName))
        {
            return;
        }

        Library.AddTag(NewTagName);
        NewTagName = "";
    }

    private void DeleteTag(Guid id)
    {
        Library.DeleteTag(id);
    }

    private void UpdateTagName(Tag tag, string? newName)
    {
        if (string.IsNullOrWhiteSpace(newName) || newName == tag.Name)
        {
            return;
        }

        tag.Name = newName;
        Library.UpdateTag(tag);
    }

    private void UpdateTagEmoji(Tag tag, string? newEmoji)
    {
        tag.Emoji = string.IsNullOrWhiteSpace(newEmoji) ? null : newEmoji;
        Library.UpdateTag(tag);
    }

    private void ToggleColorPicker(Guid id)
    {
        ActiveColorPickerId = ActiveColorPickerId == id ? null : id;
    }

    private void UpdateTagColor(Tag tag, string color)
    {
        tag.ColorHex = color;
        Library.UpdateTag(tag);
        ActiveColorPickerId = null;
    }

    private void OnSortByChange(ChangeEventArgs e)
    {
        if (Enum.TryParse<TagSortBy>(e.Value?.ToString(), out TagSortBy result))
        {
            Settings.Update(s => s.TagSortBy = result);
            RefreshTags();
        }
    }

    private void OnSortDirChange(ChangeEventArgs e)
    {
        if (Enum.TryParse<SortDirection>(e.Value?.ToString(), out SortDirection result))
        {
            Settings.Update(s => s.TagSortDirection = result);
            RefreshTags();
        }
    }

    public void Dispose()
    {
        Library.LibraryChanged -= RefreshTags;
        Settings.SettingsChanged -= StateHasChanged;
    }
}
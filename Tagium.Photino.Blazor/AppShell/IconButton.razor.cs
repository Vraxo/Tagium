using Microsoft.AspNetCore.Components;

namespace Tagium.Photino.Blazor.AppShell;

public partial class IconButton
{
    [Parameter, EditorRequired] public RenderFragment? ChildContent { get; set; }
    [Parameter, EditorRequired] public string Title { get; set; } = "";
    [Parameter] public string Class { get; set; } = "";
    [Parameter] public bool Disabled { get; set; }
    [Parameter] public EventCallback OnClick { get; set; }
}
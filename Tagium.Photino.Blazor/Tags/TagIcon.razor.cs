using Microsoft.AspNetCore.Components;
using Tagium.Core.Tags;

namespace Tagium.Photino.Blazor.Tags;

public partial class TagIcon
{
    [Parameter, EditorRequired] public required Tag Tag { get; set; }
    [Parameter] public bool ShowIcon { get; set; } = true;
}
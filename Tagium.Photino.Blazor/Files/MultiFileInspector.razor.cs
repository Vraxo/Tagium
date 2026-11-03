using Microsoft.AspNetCore.Components;
using Tagium.Photino.Blazor;

namespace Tagium.Photino.Blazor.Files;

public partial class MultiFileInspector
{
    [Inject] private AppState AppState { get; set; } = default!;
}
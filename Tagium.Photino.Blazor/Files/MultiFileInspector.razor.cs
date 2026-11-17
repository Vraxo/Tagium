using Microsoft.AspNetCore.Components;
using Tagium.Core.Files;

namespace Tagium.Photino.Blazor.Files;

public partial class MultiFileInspector
{
    [Inject] private FileBrowser FileBrowser { get; set; } = default!;
}

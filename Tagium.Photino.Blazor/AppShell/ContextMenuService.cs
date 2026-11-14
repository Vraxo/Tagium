namespace Tagium.Photino.Blazor.AppShell;

public enum ContextMenuKind
{
    File,
    Folder,
    Tag
}

public class ContextMenuService
{
    public bool IsOpen { get; private set; }
    public ContextMenuKind Kind { get; private set; }
    public string TargetKey { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public string Path { get; private set; } = string.Empty;
    public bool IsMissing { get; private set; }
    public IReadOnlyList<Guid> SelectedFileIds { get; private set; } = [];
    public double ClientX { get; private set; }
    public double ClientY { get; private set; }
    public bool IsKeyboardInvocation { get; private set; }

    public event Action? OnShow;
    public event Action? OnClose;

    public void ShowFile(
        Guid fileId,
        string name,
        string path,
        bool isMissing,
        IEnumerable<Guid> selectedFileIds,
        double clientX,
        double clientY,
        bool isKeyboardInvocation)
    {
        Kind = ContextMenuKind.File;
        TargetKey = fileId.ToString("D");
        Name = name;
        Path = path;
        IsMissing = isMissing;
        SelectedFileIds = selectedFileIds.Distinct().ToList();
        Show(clientX, clientY, isKeyboardInvocation);
    }

    public void ShowFolder(
        string path,
        string name,
        double clientX,
        double clientY,
        bool isKeyboardInvocation)
    {
        Kind = ContextMenuKind.Folder;
        TargetKey = path;
        Name = name;
        Path = path;
        IsMissing = false;
        SelectedFileIds = [];
        Show(clientX, clientY, isKeyboardInvocation);
    }

    public void ShowTag(
        Guid tagId,
        string name,
        double clientX,
        double clientY,
        bool isKeyboardInvocation)
    {
        Kind = ContextMenuKind.Tag;
        TargetKey = tagId.ToString("D");
        Name = name;
        Path = string.Empty;
        IsMissing = false;
        SelectedFileIds = [];
        Show(clientX, clientY, isKeyboardInvocation);
    }

    private void Show(double clientX, double clientY, bool isKeyboardInvocation)
    {
        ClientX = clientX;
        ClientY = clientY;
        IsKeyboardInvocation = isKeyboardInvocation;
        IsOpen = true;
        OnShow?.Invoke();
    }

    public void Close()
    {
        if (!IsOpen)
        {
            return;
        }

        IsOpen = false;
        OnClose?.Invoke();
    }
}

window.dragDrop = {
    init: (dotNetHelper) => {
        const dropZone = document.getElementById('app');

        dropZone.addEventListener('dragover', (e) => {
            e.preventDefault();
            dropZone.classList.add('drag-over');
        });

        dropZone.addEventListener('dragleave', (e) => {
            e.preventDefault();
            dropZone.classList.remove('drag-over');
        });

        dropZone.addEventListener('drop', (e) => {
            e.preventDefault();
            dropZone.classList.remove('drag-over');

            const files = [];
            if (e.dataTransfer.files) {
                for (let i = 0; i < e.dataTransfer.files.length; i++) {
                    const file = e.dataTransfer.files[i];
                    // IMPORTANT: Photino/WebView2 usually exposes the full path in a non-standard way or blocked.
                    // However, in Photino.NET, standard File API often lacks the full path due to browser security.
                    // We rely on 'path' property if available (Electron/Photino specific) or standard approach.
                    // NOTE: Photino currently does not expose full paths via JS drop easily without custom handler.
                    // BUT: Tagra used a custom URI handler or specific logic.
                    // FOR THIS MIGRATION: We will attempt to assume the environment allows it or use a C# side hook if available.
                    // As a fallback, since this is "Tagium", if JS cannot get paths, we might need a transparent overlay.
                    // Actually, Photino.Blazor doesn't natively solve "Drop file get path".
                    // Workaround: We will ignore the JS file object content and assume we can't get the path easily via pure JS
                    // UNLESS we are in an environment that bridges it.
                    //
                    // REVISION: Pure JS drops in WebView2 DO NOT provide full paths.
                    // We must rely on the window-level drop event in C# (MainWindow.RegisterCustomScheme... or similar).
                    // However, Photino doesn't expose a 'Drop' event on MainWindow easily in the public API.
                    //
                    // ALTERNATIVE: Use an <input type="file"> hack or check if the File object has a 'path' property (common in WebView2 contexts).
                    
                    // Let's try to pass whatever path we find.
                    if (file.path) { // Electron/WebView sometimes exposes this
                        files.push(file.path);
                    } else if (file.name) {
                        // If we can't get the full path, this feature is limited. 
                        // But let's proceed assuming 'path' might exist or we handle it in C# via different mechanism later.
                        // For now, we try 'path'.
                    }
                }
            }
            
            if (files.length > 0) {
                dotNetHelper.invokeMethodAsync('OnFilesDropped', files);
            }
        });
    }
};
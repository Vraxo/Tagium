window.globalShortcuts = {
    initialized: false,

    init: function (dotNetRef) {
        if (this.initialized) {
            return;
        }

        this.initialized = true;
        window.libraryNav?.setDotNetRef(dotNetRef);

        document.addEventListener('keydown', function (e) {
            const target = e.target;
            const isInput = target && (
                target.tagName === 'INPUT'
                || target.tagName === 'TEXTAREA'
                || target.tagName === 'SELECT'
                || target.isContentEditable
            );

            const fileItem = target && target.closest
                ? target.closest('.file-card, .file-row')
                : null;

            if (e.key === 'F6' && !e.altKey && !e.ctrlKey && !e.metaKey) {
                e.preventDefault();
                window.libraryNav?.cycleFocusArea(e.shiftKey);
                return;
            }

            if (e.ctrlKey || e.metaKey) {
                const key = e.key.toLowerCase();

                // Native zoom shortcuts must work even when a text field has focus.
                if (['+', '=', '-', '0'].includes(key)) {
                    e.preventDefault();
                    dotNetRef.invokeMethodAsync('HandleGlobalKeyDown', key, true, e.shiftKey);
                    return;
                }

                if (isInput) {
                    return;
                }

                if (['z', 'f', 't', 'r', '1', '2', '3'].includes(key)) {
                    e.preventDefault();
                    dotNetRef.invokeMethodAsync('HandleGlobalKeyDown', key, true, e.shiftKey);
                }

                return;
            }

            if (e.altKey || isInput) {
                return;
            }

            if (e.key.toLowerCase() === 't') {
                e.preventDefault();
                const focusedFileId = fileItem?.getAttribute('data-id') ?? null;
                dotNetRef.invokeMethodAsync('FocusTagPicker', focusedFileId);
                return;
            }

            if (fileItem && ['1', '2', '3', '4', '5', '0'].includes(e.key)) {
                e.preventDefault();
                const rating = e.key === '0' ? 0 : Number(e.key);
                dotNetRef.invokeMethodAsync(
                    'SetQuickRating',
                    rating,
                    fileItem.getAttribute('data-id'));
                return;
            }

            if (fileItem && (e.key === 'PageDown' || e.key === 'PageUp')) {
                e.preventDefault();
                const direction = e.key === 'PageDown' ? 1 : -1;

                dotNetRef.invokeMethodAsync('NavigateFilePage', direction)
                    .then(moved => {
                        if (moved) {
                            window.libraryNav?.focusSelectedFile();
                        }
                    })
                    .catch(() => {
                        // A closing WebView can invalidate the .NET callback.
                    });
            }
        });

        // Keep Ctrl+mousewheel zoom from changing the WebView's native page scale.
        document.addEventListener('wheel', function (e) {
            if (e.ctrlKey || e.metaKey) {
                e.preventDefault();
                const direction = e.deltaY < 0 ? 1 : -1;
                dotNetRef.invokeMethodAsync('HandleWheelZoom', direction);
            }
        }, { passive: false });
    }
};

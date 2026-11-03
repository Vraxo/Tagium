window.globalShortcuts = {
    init: function (dotNetRef) {
        document.addEventListener('keydown', function (e) {
            const isInput = e.target.tagName === 'INPUT' || e.target.tagName === 'TEXTAREA' || e.target.tagName === 'SELECT' || e.target.isContentEditable;

            if (e.ctrlKey || e.metaKey) {
                const key = e.key.toLowerCase();

                // Native zoom shortcuts must be intercepted globally even when typing in input controls
                if (['+', '=', '-', '0'].includes(key)) {
                    e.preventDefault();
                    dotNetRef.invokeMethodAsync('HandleGlobalKeyDown', key, e.ctrlKey || e.metaKey, e.shiftKey);
                    return;
                }

                if (isInput) {
                    return;
                }

                if (['z', 'f', 't', 'r'].includes(key)) {
                    e.preventDefault();
                    dotNetRef.invokeMethodAsync('HandleGlobalKeyDown', key, e.ctrlKey || e.metaKey, e.shiftKey);
                }
            }
        });

        // Intercept Ctrl + mousewheel scroll zooms globally to prevent native distortion
        document.addEventListener('wheel', function (e) {
            if (e.ctrlKey || e.metaKey) {
                e.preventDefault();
                const direction = e.deltaY < 0 ? 1 : -1;
                dotNetRef.invokeMethodAsync('HandleWheelZoom', direction);
            }
        }, { passive: false });
    }
};
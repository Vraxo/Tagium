window.layoutResizer = {
    init: function (dotNetRef, leftPanelId, leftSplitterId, rightPanelId, rightSplitterId) {
        const leftPanel = document.getElementById(leftPanelId);
        const rightPanel = document.getElementById(rightPanelId);
        const leftSplitter = document.getElementById(leftSplitterId);
        const rightSplitter = document.getElementById(rightSplitterId);

        if (leftSplitter && leftPanel) {
            setupResizer(leftSplitter, leftPanel, 'left');
        }

        if (rightSplitter && rightPanel) {
            setupResizer(rightSplitter, rightPanel, 'right');
        }

        function getMaximumWidth(side) {
            const compact = window.innerWidth <= 900;
            const minCenterWidth = compact ? 240 : 320;
            const sidePanelShare = compact ? 0.24 : (side === 'left' ? 0.30 : 0.35);
            const otherPanel = side === 'left' ? rightPanel : leftPanel;
            const otherPanelWidth = otherPanel?.getBoundingClientRect().width ?? 0;
            const splitterWidth = (leftSplitter?.getBoundingClientRect().width ?? 0)
                + (rightSplitter?.getBoundingClientRect().width ?? 0);
            const availableWidth = window.innerWidth - minCenterWidth - otherPanelWidth - splitterWidth;
            const legacyLimit = side === 'left' ? 600 : 800;

            return Math.max(side === 'left' ? 150 : 200,
                Math.min(legacyLimit, 420, window.innerWidth * sidePanelShare, availableWidth));
        }

        function setupResizer(splitter, panel, side) {
            splitter.addEventListener('mousedown', function (e) {
                e.preventDefault();

                splitter.classList.add('active');
                document.body.style.cursor = 'col-resize';
                document.body.classList.add('no-select');

                function onMouseMove(e) {
                    const minWidth = side === 'left' ? 150 : 200;
                    const requestedWidth = side === 'left'
                        ? e.clientX
                        : window.innerWidth - e.clientX;
                    const maxWidth = getMaximumWidth(side);
                    const newWidth = Math.min(Math.max(requestedWidth, minWidth), maxWidth);
                    panel.style.width = newWidth + 'px';
                }

                function onMouseUp() {
                    splitter.classList.remove('active');
                    document.body.style.cursor = '';
                    document.body.classList.remove('no-select');

                    document.removeEventListener('mousemove', onMouseMove);
                    document.removeEventListener('mouseup', onMouseUp);

                    // Persist the rendered width, which may be CSS-clamped to preserve room for files.
                    const width = Math.round(panel.getBoundingClientRect().width);
                    panel.style.width = width + 'px';
                    if (side === 'left') {
                        dotNetRef.invokeMethodAsync('SaveLeftSidebarWidth', width);
                    } else {
                        dotNetRef.invokeMethodAsync('SaveRightSidebarWidth', width);
                    }
                }

                document.addEventListener('mousemove', onMouseMove);
                document.addEventListener('mouseup', onMouseUp);
            });
        }
    }
};

// Keyboard navigation for library surfaces and the major workspace areas.
window.libraryNav = {
    initialized: false,
    dotNetRef: null,
    lastFileElement: null,

    setDotNetRef: function (dotNetRef) {
        this.dotNetRef = dotNetRef;
    },

    init: function () {
        if (this.initialized) {
            return;
        }

        this.initialized = true;

        document.addEventListener('focusin', e => {
            const fileItem = e.target && e.target.closest
                ? e.target.closest('.file-card, .file-row')
                : null;

            if (fileItem) {
                this.lastFileElement = fileItem;
            }
        });

        document.addEventListener('keydown', function (e) {
            const target = e.target;
            const item = target && target.closest
                ? target.closest('.file-card, .file-row, .tag-item, .folder-node')
                : null;

            const tagInput = target && target.closest
                ? target.closest('.tag-selector-input')
                : null;

            // These keys are implemented by TagSelector. Prevent their browser defaults
            // (caret movement, form submission and history navigation) without stopping
            // the Blazor key handlers from receiving them.
            if (tagInput && [' ', 'ArrowDown', 'ArrowUp', 'Enter', 'Escape'].includes(e.key)) {
                if (e.key !== ' ') {
                    e.preventDefault();
                }
                return;
            }

            if (!item) {
                return;
            }

            if (e.key === ' ' || e.key === 'Home' || e.key === 'End' || e.key.startsWith('Arrow')) {
                e.preventDefault();
            }
        });
    },

    isVisible: function (element) {
        return !!element
            && element.isConnected
            && element.getClientRects().length > 0
            && getComputedStyle(element).visibility !== 'hidden';
    },

    focusPreferred: function (container, selector) {
        if (!container) {
            return false;
        }

        const items = Array.from(container.querySelectorAll(selector))
            .filter(item => this.isVisible(item));

        if (items.length === 0) {
            return false;
        }

        const target = items.find(item => item.getAttribute('aria-selected') === 'true')
            || items.find(item => item.classList.contains('active'))
            || items.find(item => item.tabIndex === 0)
            || items[0];

        target.scrollIntoView({ block: 'nearest', inline: 'nearest' });
        target.focus();
        return true;
    },

    areas: function () {
        return [
            {
                name: 'search',
                find: () => document.querySelector('.search-input')
            },
            {
                name: 'sidebar',
                find: () => {
                    const root = document.querySelector('#sidebar-panel');
                    if (!root) {
                        return null;
                    }

                    return root.querySelector('.folder-node')
                        ? this.preferredIn(root, '.folder-node')
                        : this.preferredIn(root, '.tag-item');
                }
            },
            {
                name: 'workspace',
                find: () => {
                    const grid = document.querySelector('#file-grid-container');
                    if (grid && this.focusPreferredTarget(grid, '.file-card')) {
                        return document.activeElement;
                    }

                    const list = document.querySelector('#file-list-container');
                    if (list && this.focusPreferredTarget(list, '.file-row')) {
                        return document.activeElement;
                    }

                    const tagManager = document.querySelector('.tag-manager-view');
                    return tagManager
                        ? tagManager.querySelector('.add-row input, .tag-search input, button')
                        : null;
                }
            },
            {
                name: 'inspector',
                find: () => {
                    const root = document.querySelector('#inspector-panel');
                    if (!root) {
                        return null;
                    }

                    return root.querySelector('.tag-selector-input')
                        || root.querySelector('.rating-stars button');
                }
            }
        ];
    },

    preferredIn: function (root, selector) {
        const items = Array.from(root.querySelectorAll(selector)).filter(item => this.isVisible(item));
        return items.find(item => item.getAttribute('aria-selected') === 'true')
            || items.find(item => item.classList.contains('active'))
            || items.find(item => item.tabIndex === 0)
            || items[0]
            || null;
    },

    focusPreferredTarget: function (root, selector) {
        const items = Array.from(root.querySelectorAll(selector)).filter(item => this.isVisible(item));
        if (items.length === 0) {
            return false;
        }

        const target = items.find(item => item.getAttribute('aria-selected') === 'true')
            || items.find(item => item.tabIndex === 0)
            || items[0];

        target.scrollIntoView({ block: 'nearest', inline: 'nearest' });
        target.focus();
        return true;
    },

    areaIndex: function (element, areas) {
        if (!element) {
            return -1;
        }

        return areas.findIndex(area => {
            if (area.name === 'search') {
                return !!element.closest('.search-box');
            }
            if (area.name === 'sidebar') {
                return !!element.closest('#sidebar-panel');
            }
            if (area.name === 'workspace') {
                return !!element.closest('#file-grid-container, #file-list-container, .tag-manager-view');
            }
            if (area.name === 'inspector') {
                return !!element.closest('#inspector-panel');
            }
            return false;
        });
    },

    cycleFocusArea: function (reverse) {
        const areas = this.areas();
        const active = document.activeElement;
        const currentIndex = this.areaIndex(active, areas);
        const direction = reverse ? -1 : 1;

        for (let offset = 1; offset <= areas.length; offset++) {
            const index = (currentIndex + direction * offset + areas.length) % areas.length;
            const target = areas[index].find();

            if (target && this.isVisible(target) && typeof target.focus === 'function') {
                target.scrollIntoView({ block: 'nearest', inline: 'nearest' });
                target.focus();
                return areas[index].name;
            }
        }

        return null;
    },

    focusView: async function (view) {
        // Blazor may need more than one frame to replace the current view's DOM.
        for (let attempt = 0; attempt < 12; attempt++) {
            await new Promise(resolve => requestAnimationFrame(resolve));

            if (view === 'files') {
                const grid = document.querySelector('#file-grid-container');
                if (grid && this.focusPreferredTarget(grid, '.file-card')) {
                    return true;
                }

                const list = document.querySelector('#file-list-container');
                if (list && this.focusPreferredTarget(list, '.file-row')) {
                    return true;
                }
            } else if (view === 'folders') {
                const tree = document.querySelector('.folder-list');
                if (tree && this.focusPreferred(tree, '.folder-node')) {
                    return true;
                }
            } else if (view === 'tags') {
                const input = document.querySelector('.tag-manager-view .add-row input');
                if (this.isVisible(input)) {
                    input.focus();
                    return true;
                }
            }
        }

        return false;
    },

    focusTagPicker: async function () {
        for (let attempt = 0; attempt < 12; attempt++) {
            await new Promise(resolve => requestAnimationFrame(resolve));
            const input = document.querySelector('#inspector-panel .tag-selector-input');

            if (this.isVisible(input)) {
                input.focus();
                input.select();
                return true;
            }
        }

        return false;
    },

    focusSelectedFile: async function () {
        for (let attempt = 0; attempt < 12; attempt++) {
            await new Promise(resolve => requestAnimationFrame(resolve));

            const selected = document.querySelector(
                '#file-grid-container .file-card[aria-selected="true"], #file-list-container .file-row[aria-selected="true"]');

            if (this.isVisible(selected)) {
                selected.scrollIntoView({ block: 'nearest', inline: 'nearest' });
                selected.focus();
                this.lastFileElement = selected;
                return true;
            }
        }

        return false;
    },

    focusLastFile: function () {
        if (this.isVisible(this.lastFileElement)) {
            this.lastFileElement.focus();
            this.lastFileElement.scrollIntoView({ block: 'nearest', inline: 'nearest' });
            return true;
        }

        return this.focusSelectedFile();
    },

    getFocusedFileId: function () {
        const item = document.activeElement && document.activeElement.closest
            ? document.activeElement.closest('.file-card, .file-row')
            : null;
        return item ? item.getAttribute('data-id') : null;
    },

    // Focuses an item at a position in DOM order. Folder rows are flattened in tree order.
    focusItem: function (containerSelector, itemSelector, index) {
        const container = document.querySelector(containerSelector);
        if (!container) {
            return false;
        }

        const items = container.querySelectorAll(itemSelector);
        if (index < 0 || index >= items.length) {
            return false;
        }

        items[index].focus();
        return true;
    },

    move: function (key, containerSelector, itemSelector) {
        const container = document.querySelector(containerSelector);
        if (!container) {
            return null;
        }

        const active = document.activeElement;
        const current = active && active.closest ? active.closest(itemSelector) : null;
        if (!current || !container.contains(current)) {
            return null;
        }

        const items = Array.from(container.querySelectorAll(itemSelector));
        if (items.length === 0) {
            return null;
        }

        let target = null;

        if (key === 'Home') {
            target = items[0];
        } else if (key === 'End') {
            target = items[items.length - 1];
        } else {
            target = this.nearest(items, current, key);
        }

        if (!target || target === current) {
            return null;
        }

        target.scrollIntoView({ block: 'nearest', inline: 'nearest' });
        target.focus();
        this.lastFileElement = target;

        return target.getAttribute('data-id');
    },

    nearest: function (items, current, key) {
        const currentRect = current.getBoundingClientRect();
        let best = null;
        let bestScore = Number.MAX_VALUE;

        for (const item of items) {
            if (item === current) {
                continue;
            }

            const rect = item.getBoundingClientRect();
            let primary;
            let secondary;

            switch (key) {
                case 'ArrowRight':
                    if (rect.left <= currentRect.left) { continue; }
                    primary = rect.left - currentRect.left;
                    secondary = Math.abs(rect.top - currentRect.top);
                    break;
                case 'ArrowLeft':
                    if (rect.left >= currentRect.left) { continue; }
                    primary = currentRect.left - rect.left;
                    secondary = Math.abs(rect.top - currentRect.top);
                    break;
                case 'ArrowDown':
                    if (rect.top <= currentRect.top) { continue; }
                    primary = rect.top - currentRect.top;
                    secondary = Math.abs(rect.left - currentRect.left);
                    break;
                case 'ArrowUp':
                    if (rect.top >= currentRect.top) { continue; }
                    primary = currentRect.top - rect.top;
                    secondary = Math.abs(rect.left - currentRect.left);
                    break;
                default:
                    continue;
            }

            const score = (primary * 1000) + secondary;
            if (score < bestScore) {
                bestScore = score;
                best = item;
            }
        }

        return best;
    }
};

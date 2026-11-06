// Keyboard navigation for the library surfaces (grid cards and list rows) and the
// sidebar filters. Geometry based, so it works for the auto-fill grid and the single
// column list without either side knowing about the other's layout.
window.libraryNav = {
    initialized: false,

    // Prevents the browser default for keys the app handles itself: Space would scroll
    // the container and arrows/Home/End would scroll it away from the focused item.
    init: function () {
        if (this.initialized) {
            return;
        }

        this.initialized = true;

        document.addEventListener('keydown', function (e) {
            const item = e.target && e.target.closest
                ? e.target.closest('.file-card, .file-row, .tag-item, .folder-node')
                : null;

            if (!item) {
                return;
            }

            if (e.key === ' ' || e.key === 'Home' || e.key === 'End' || e.key.startsWith('Arrow')) {
                e.preventDefault();
            }
        });
    },

    // Focuses the item at a position in DOM order. The folder tree renders its rows in the
    // same order it navigates them, so an index is enough and Blazor needs no element refs.
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

    // Moves focus to the neighbouring item in the requested direction and returns the
    // id of the element that received focus, so Blazor can mirror it in the selection.
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

        target.scrollIntoView({ block: 'nearest' });
        target.focus();

        return target.getAttribute('data-id');
    },

    // Picks the item closest to the current one in the requested direction: the dominant
    // axis decides, the cross axis breaks ties (which is what makes grid arrows behave).
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

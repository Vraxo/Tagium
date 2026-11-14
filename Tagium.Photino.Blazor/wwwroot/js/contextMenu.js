window.libraryContextMenu = {
    initialized: false,

    init: function () {
        if (this.initialized) {
            return;
        }

        this.initialized = true;

        // Menus keep keyboard focus inside their own commands until Escape or an action closes them.
        document.addEventListener('keydown', event => {
            const menu = document.querySelector('.context-menu');
            if (!menu) {
                return;
            }

            if (['ArrowDown', 'ArrowUp', 'Home', 'End'].includes(event.key)) {
                event.preventDefault();
                return;
            }

            if (event.key === 'Tab') {
                event.preventDefault();
                this.moveFocus(menu, event.shiftKey ? -1 : 1);
            }
        });
    },

    findAnchor: function (kind, key) {
        let selector;
        let attribute;

        switch (kind) {
            case 'file':
                selector = '.file-card, .file-row';
                attribute = 'data-id';
                break;
            case 'tag':
                selector = '.tag-item[data-context-id]';
                attribute = 'data-context-id';
                break;
            case 'folder':
                selector = '.folder-node';
                attribute = 'data-path';
                break;
            default:
                return null;
        }

        return Array.from(document.querySelectorAll(selector))
            .find(element => element.getAttribute(attribute) === key) || null;
    },

    position: function (menu, kind, key, clientX, clientY, keyboardInvocation) {
        if (!menu) {
            return false;
        }

        const anchor = keyboardInvocation ? this.findAnchor(kind, key) : null;
        const anchorRect = anchor ? anchor.getBoundingClientRect() : null;
        const menuRect = menu.getBoundingClientRect();
        const margin = 8;

        let left = anchorRect ? anchorRect.left : clientX;
        let top = anchorRect ? anchorRect.bottom : clientY;

        left = Math.max(margin, Math.min(left, window.innerWidth - menuRect.width - margin));
        top = Math.max(margin, Math.min(top, window.innerHeight - menuRect.height - margin));

        menu.style.left = left + 'px';
        menu.style.top = top + 'px';
        return true;
    },

    focusFirst: function (menu) {
        const items = Array.from(menu?.querySelectorAll('button:not(:disabled)') || []);
        if (items.length === 0) {
            return false;
        }

        items[0].focus();
        return true;
    },

    moveFocus: function (menu, direction) {
        const items = Array.from(menu?.querySelectorAll('button:not(:disabled)') || []);
        if (items.length === 0) {
            return false;
        }

        const currentIndex = items.indexOf(document.activeElement);
        const nextIndex = currentIndex < 0
            ? (direction > 0 ? 0 : items.length - 1)
            : (currentIndex + direction + items.length) % items.length;

        items[nextIndex].focus();
        return true;
    },

    focusEdge: function (menu, edge) {
        const items = Array.from(menu?.querySelectorAll('button:not(:disabled)') || []);
        if (items.length === 0) {
            return false;
        }

        const target = edge === 'last' ? items[items.length - 1] : items[0];
        target.focus();
        return true;
    },

    focusTarget: function (kind, key) {
        const target = this.findAnchor(kind, key);
        if (!target) {
            return false;
        }

        target.focus();
        target.scrollIntoView({ block: 'nearest', inline: 'nearest' });
        return true;
    },

    copyText: async function (value) {
        if (!navigator.clipboard?.writeText) {
            throw new Error('Clipboard access is unavailable.');
        }

        await navigator.clipboard.writeText(value);
    }
};

window.libraryContextMenu.init();

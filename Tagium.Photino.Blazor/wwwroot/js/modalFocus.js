// Modal focus management: keeps Tab inside an open dialog and puts focus back on
// whatever opened it. Escaping/activating stays in Blazor; this file only owns focus.
window.modalFocus = (function () {
    const FOCUSABLE = [
        'a[href]',
        'button:not([disabled])',
        'input:not([disabled]):not([type="hidden"])',
        'select:not([disabled])',
        'textarea:not([disabled])',
        '[tabindex]:not([tabindex="-1"])',
        '[contenteditable="true"]'
    ].join(',');

    let opener = null;
    let container = null;
    let isTrapping = false;

    function focusableIn(root) {
        return Array.from(root.querySelectorAll(FOCUSABLE)).filter(function (el) {
            // offsetParent is null while an ancestor is display:none, which is how
            // Blazor hides the dialog body content that is not in use.
            return !el.hasAttribute('disabled') && el.getClientRects().length > 0;
        });
    }

    function onKeyDown(e) {
        if (e.key !== 'Tab' || !isTrapping || !container) {
            return;
        }

        const items = focusableIn(container);
        if (items.length === 0) {
            // Nothing reachable inside: keep focus on the dialog itself.
            e.preventDefault();
            container.focus();
            return;
        }

        const first = items[0];
        const last = items[items.length - 1];
        const active = document.activeElement;

        if (!container.contains(active)) {
            // Focus escaped (for example via a programmatic focus behind the backdrop).
            e.preventDefault();
            (e.shiftKey ? last : first).focus();
            return;
        }

        if (e.shiftKey && (active === container || active === first)) {
            e.preventDefault();
            last.focus();
        } else if (!e.shiftKey && active === last) {
            e.preventDefault();
            first.focus();
        }
    }

    function isInsideContainer(el) {
        return !!el && !!container && container.contains(el);
    }

    return {
        // Remember the element that opened the dialog. Safe to call before the
        // dialog renders; the previous opener is kept if a modal replaces a modal.
        capture: function () {
            const active = document.activeElement;
            if (!active || active === document.body || isInsideContainer(active)) {
                return;
            }
            opener = active;
        },

        // Start trapping Tab inside the dialog element handed over by Blazor.
        trap: function (element) {
            if (!element) {
                return;
            }
            container = element;
            if (isTrapping) {
                return;
            }
            isTrapping = true;
            // Capture phase so the trap wins before anything inside can act on Tab.
            document.addEventListener('keydown', onKeyDown, true);
        },

        // Stop trapping and hand focus back to the opener. Called after the dialog
        // has left the DOM, so the restore is the last thing that happens.
        release: function () {
            if (isTrapping) {
                isTrapping = false;
                document.removeEventListener('keydown', onKeyDown, true);
            }
            container = null;

            const target = opener;
            opener = null;
            if (target && target.isConnected && typeof target.focus === 'function') {
                target.focus({ preventScroll: true });
            }
        }
    };
})();

// Progressive loading for infinite scroll.
//
// The file views render InfiniteScrollSentinel after the last file while batches remain.
// One IntersectionObserver per scroll container watches that sentinel and asks .NET for
// the next batch before the user reaches the bottom, so scrolling stays continuous.
window.libraryScroll = {
    observer: null,
    root: null,
    sentinel: null,
    dotNetRef: null,

    setDotNetRef: function (dotNetRef) {
        const changed = this.dotNetRef !== dotNetRef;
        this.dotNetRef = dotNetRef;

        if (changed) {
            // Re-arm so the very first check runs with a live .NET reference.
            this.reconnect();
        }
    },

    // Safe to call after every render: it re-observes only when the observer was spent
    // (a batch was just requested) or the sentinel element or its scroll container changed.
    observe: function () {
        const sentinel = document.querySelector('[data-infinite-scroll-sentinel]');
        const root = sentinel
            ? sentinel.closest('#file-grid-container, #file-list-container')
            : null;

        if (sentinel === this.sentinel && root === this.root && this.observer) {
            return !!sentinel;
        }

        this.sentinel = sentinel;
        this.root = root;
        this.reconnect();
        return !!sentinel;
    },

    reconnect: function () {
        if (this.observer) {
            this.observer.disconnect();
            this.observer = null;
        }

        if (!this.sentinel || !this.root || typeof IntersectionObserver === 'undefined') {
            return;
        }

        const root = this.root;

        // The sentinel is inside the scrolling grid/list container, so that container is
        // the observer root. The margin starts loading one screenful early.
        this.observer = new IntersectionObserver(entries => {
            const visible = entries.some(entry => entry.isIntersecting);
            if (!visible || !this.dotNetRef) {
                return;
            }

            // Request exactly one batch: stop observing until the render that appends it
            // calls observe() again. Without this, a sentinel that is still on screen
            // would ask for every remaining batch in a single burst.
            this.observer?.disconnect();
            this.observer = null;

            this.dotNetRef.invokeMethodAsync('LoadMoreFiles')
                .catch(() => {
                    // A closing WebView can invalidate the .NET callback.
                });
        }, {
            root,
            rootMargin: '400px 0px',
            threshold: 0
        });

        this.observer.observe(this.sentinel);
    },

    // Focuses the file at the given position in the rendered viewport after a keyboard
    // move across a viewport boundary — including a file that a batch append added in the
    // same render. The position is authoritative: with a shift-extended selection several
    // items are selected, and the destination is the one that must receive focus.
    focusPending: async function (index) {
        for (let attempt = 0; attempt < 12; attempt++) {
            await new Promise(resolve => requestAnimationFrame(resolve));

            const container = document.querySelector('#file-grid-container, #file-list-container');
            if (!container) {
                continue;
            }

            const selector = container.id === 'file-grid-container' ? '.file-card' : '.file-row';
            const items = Array.from(container.querySelectorAll(selector));

            if (items.length === 0) {
                continue;
            }

            const target = index >= 0 && index < items.length
                ? items[index]
                : items.find(item => item.getAttribute('aria-selected') === 'true');

            if (!target) {
                continue;
            }

            target.scrollIntoView({ block: 'nearest', inline: 'nearest' });
            target.focus();
            window.libraryNav.lastFileElement = target;
            return true;
        }

        return false;
    }
};

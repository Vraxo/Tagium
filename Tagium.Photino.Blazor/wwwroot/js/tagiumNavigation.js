window.tagiumNavigation = (() => {
    const graphReturnKey = '__tagiumGraphReturn';
    const graphBrowseQueryKey = '__tagiumGraphBrowseQuery';
    let dotNetRef = null;
    let isListening = false;

    function handlePopState(event) {
        if (!dotNetRef) return;

        if (event.state?.[graphReturnKey] === true) {
            dotNetRef.invokeMethodAsync('ReturnToTagGraph').catch(console.error);
        } else if (typeof event.state?.[graphBrowseQueryKey] === 'string') {
            dotNetRef.invokeMethodAsync('RestoreTagBrowse', event.state[graphBrowseQueryKey]).catch(console.error);
        }
    }

    return {
        init: function (reference) {
            dotNetRef = reference;
            if (!isListening) {
                window.addEventListener('popstate', handlePopState);
                isListening = true;
            }
        },

        openGraphBrowse: function (query) {
            const graphState = history.state && typeof history.state === 'object'
                ? { ...history.state }
                : {};
            graphState[graphReturnKey] = true;
            delete graphState[graphBrowseQueryKey];
            history.replaceState(graphState, '', location.href);

            const browseState = {
                ...graphState,
                [graphReturnKey]: false,
                [graphBrowseQueryKey]: query
            };
            history.pushState(browseState, '', location.href);
        }
    };
})();

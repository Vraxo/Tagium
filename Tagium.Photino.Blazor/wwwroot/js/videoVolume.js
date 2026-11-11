(function () {
    const bindings = new WeakMap();

    function clampVolume(volume) {
        const value = Number(volume);
        return Number.isFinite(value) ? Math.min(1, Math.max(0, value)) : 1;
    }

    async function bind(video, volume, dotNetReference) {
        await unbind(video);
        video.volume = clampVolume(volume);

        let timer = null;
        let pending = false;

        const flush = () => {
            if (!pending) {
                return Promise.resolve();
            }

            pending = false;
            timer = null;
            return dotNetReference.invokeMethodAsync("HandleVideoVolumeChanged", video.volume).catch(() => {});
        };

        const onVolumeChange = () => {
            pending = true;
            if (timer !== null) {
                clearTimeout(timer);
            }

            // Native sliders may emit many volumechange events while dragging.
            // Persist once the user pauses, rather than rewriting settings.json per tick.
            timer = setTimeout(flush, 200);
        };

        video.addEventListener("volumechange", onVolumeChange);
        bindings.set(video, { onVolumeChange, flush, getTimer: () => timer });
    }

    async function unbind(video) {
        const binding = bindings.get(video);
        if (!binding) {
            return;
        }

        video.removeEventListener("volumechange", binding.onVolumeChange);
        if (binding.getTimer() !== null) {
            clearTimeout(binding.getTimer());
        }
        bindings.delete(video);
        await binding.flush();
    }

    window.videoVolume = { bind, unbind };
})();

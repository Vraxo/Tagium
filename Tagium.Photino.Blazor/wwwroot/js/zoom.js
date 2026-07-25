window.zoomTracker = {
    dotNetRef: null,
    osScale: 1.0,
    lastZoom: 100,

    init: function (dotNetRef, initialZoomSetting) {
        this.dotNetRef = dotNetRef;
        this.lastZoom = initialZoomSetting;

        const initialRatio = window.devicePixelRatio;
        this.osScale = initialRatio / (initialZoomSetting / 100.0);

        window.addEventListener('resize', this.checkZoom.bind(this));
    },

    checkZoom: function () {
        const currentRatio = window.devicePixelRatio;
        const computedZoom = Math.round((currentRatio / this.osScale) * 100);

        if (Math.abs(computedZoom - this.lastZoom) >= 2) {
            this.lastZoom = computedZoom;
            this.dotNetRef.invokeMethodAsync('OnNativeZoomChanged', computedZoom);
        }
    }
};
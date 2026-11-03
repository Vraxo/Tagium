window.gridInteractions = {
    dotNetRef: null,
    containerId: '',
    selectionBox: null,
    isSelecting: false,
    startX: 0,
    startY: 0,

    init: function (dotNetRef, containerId) {
        this.dotNetRef = dotNetRef;
        this.containerId = containerId;

        // Create selection box element if it doesn't exist
        if (!document.getElementById('selection-box')) {
            this.selectionBox = document.createElement('div');
            this.selectionBox.id = 'selection-box';
            this.selectionBox.style.position = 'absolute';
            this.selectionBox.style.border = '1px solid #42a5f5';
            this.selectionBox.style.backgroundColor = 'rgba(66, 165, 245, 0.2)';
            this.selectionBox.style.display = 'none';
            this.selectionBox.style.zIndex = '9999';
            this.selectionBox.style.pointerEvents = 'none';
            document.body.appendChild(this.selectionBox);
        } else {
            this.selectionBox = document.getElementById('selection-box');
        }

        // Attach global events
        document.addEventListener('keydown', this.handleKeyDown.bind(this));

        const container = document.getElementById(containerId);
        if (container) {
            container.addEventListener('mousedown', this.handleMouseDown.bind(this));
        }
    },

    handleKeyDown: function (e) {
        // Ignore if focus is in an input
        if (e.target.tagName === 'INPUT' || e.target.tagName === 'SELECT') return;

        if (e.key === 'Delete') {
            this.dotNetRef.invokeMethodAsync('OnDeleteKey');
        } else if (e.key === 'Enter') {
            this.dotNetRef.invokeMethodAsync('OnEnterKey');
        } else if (e.key.startsWith('Arrow')) {
            e.preventDefault();
            this.navigate(e.key);
        } else if (e.key === 'a' && (e.ctrlKey || e.metaKey)) {
            e.preventDefault();
            this.dotNetRef.invokeMethodAsync('SelectAll');
        }
    },

    navigate: function (direction) {
        const items = Array.from(document.querySelectorAll('.file-card'));
        if (items.length === 0) return;

        const selected = document.querySelector('.file-card.selected');
        let index = selected ? items.indexOf(selected) : -1;

        if (index === -1) {
            index = 0; // Default to first
        } else {
            const containerWidth = document.getElementById(this.containerId).clientWidth;
            const itemWidth = items[0].getBoundingClientRect().width + 10; // + gap
            const cols = Math.floor(containerWidth / itemWidth) || 1;

            if (direction === 'ArrowRight') index++;
            else if (direction === 'ArrowLeft') index--;
            else if (direction === 'ArrowDown') index += cols;
            else if (direction === 'ArrowUp') index -= cols;
        }

        // Clamp
        if (index < 0) index = 0;
        if (index >= items.length) index = items.length - 1;

        // Get ID (as string)
        const id = items[index].getAttribute('data-id');
        items[index].scrollIntoView({ block: 'nearest' });

        this.dotNetRef.invokeMethodAsync('SelectFile', id);
    },

    handleMouseDown: function (e) {
        // Ignore clicks on items (handled by Blazor onclick)
        if (e.target.closest('.file-card')) return;

        // Detect if click is on scrollbar
        // If the target is the container itself, check if click is outside client area
        if (e.target.id === this.containerId) {
            const rect = e.target.getBoundingClientRect();
            const isVerticalScrollbar = e.clientX > rect.left + e.target.clientWidth;
            const isHorizontalScrollbar = e.clientY > rect.top + e.target.clientHeight;

            if (isVerticalScrollbar || isHorizontalScrollbar) {
                return;
            }
        }

        e.preventDefault();
        this.isSelecting = true;
        this.startX = e.pageX;
        this.startY = e.pageY;

        this.selectionBox.style.display = 'block';
        this.updateBox(e.pageX, e.pageY);

        document.addEventListener('mousemove', this.boundMouseMove = this.handleMouseMove.bind(this));
        document.addEventListener('mouseup', this.boundMouseUp = this.handleMouseUp.bind(this));
    },

    handleMouseMove: function (e) {
        if (!this.isSelecting) return;
        this.updateBox(e.pageX, e.pageY);
    },

    updateBox: function (currentX, currentY) {
        const left = Math.min(this.startX, currentX);
        const top = Math.min(this.startY, currentY);
        const width = Math.abs(currentX - this.startX);
        const height = Math.abs(currentY - this.startY);

        this.selectionBox.style.left = left + 'px';
        this.selectionBox.style.top = top + 'px';
        this.selectionBox.style.width = width + 'px';
        this.selectionBox.style.height = height + 'px';
    },

    handleMouseUp: function (e) {
        this.isSelecting = false;
        this.selectionBox.style.display = 'none';

        document.removeEventListener('mousemove', this.boundMouseMove);
        document.removeEventListener('mouseup', this.boundMouseUp);

        // Calculate intersection
        const boxRect = {
            left: Math.min(this.startX, e.pageX),
            top: Math.min(this.startY, e.pageY),
            right: Math.max(this.startX, e.pageX),
            bottom: Math.max(this.startY, e.pageY)
        };

        const ids = [];
        document.querySelectorAll('.file-card').forEach(item => {
            const rect = item.getBoundingClientRect();
            // Adjust rect to page coordinates
            const pageRect = {
                left: rect.left + window.scrollX,
                top: rect.top + window.scrollY,
                right: rect.right + window.scrollX,
                bottom: rect.bottom + window.scrollY
            };

            const intersect = !(boxRect.right < pageRect.left ||
                boxRect.left > pageRect.right ||
                boxRect.bottom < pageRect.top ||
                boxRect.top > pageRect.bottom);

            if (intersect) {
                ids.push(item.getAttribute('data-id')); // Keep as string
            }
        });

        if (ids.length > 0) {
            this.dotNetRef.invokeMethodAsync('SelectFiles', ids);
        } else {
            this.dotNetRef.invokeMethodAsync('ClearSelection');
        }
    }
};
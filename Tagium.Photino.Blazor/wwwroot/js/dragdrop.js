window.dragDrop = {
    init: (dotNetHelper) => {
        const dropZone = document.getElementById('app');

        // Prevent default browser actions (like opening the file in the tab)
        ['dragenter', 'dragover', 'dragleave', 'drop'].forEach(eventName => {
            dropZone.addEventListener(eventName, preventDefaults, false);
        });

        function preventDefaults(e) {
            e.preventDefault();
            e.stopPropagation();
        }

        // Highlight visual feedback
        ['dragenter', 'dragover'].forEach(eventName => {
            dropZone.addEventListener(eventName, () => dropZone.classList.add('drag-over'), false);
        });

        ['dragleave', 'drop'].forEach(eventName => {
            dropZone.addEventListener(eventName, () => dropZone.classList.remove('drag-over'), false);
        });

        // We do NOT handle the data transfer here.
        // The C# Native Window Handler catches the files before JS sees them.
    }
};
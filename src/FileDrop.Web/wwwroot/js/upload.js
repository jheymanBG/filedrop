(function () {
    const chunkSize = 10 * 1024 * 1024;
    const maxParallelFiles = 2;
    const maxParallelChunksPerFile = 3;

    const form = document.getElementById('transferForm');
    const filesInput = document.getElementById('files');
    const dropZone = document.getElementById('dropZone');
    const progressList = document.getElementById('uploadProgress');
    const uploadButton = document.getElementById('uploadButton');
    const clearButton = document.getElementById('clearUploadButton');
    const createButton = document.getElementById('createButton');
    const uploadedIds = document.getElementById('UploadedIds');
    const uploadSummary = document.getElementById('uploadSummary');

    if (!form || !filesInput || !dropZone || !uploadButton) return;

    let selectedFiles = [];
    let completedUploadIds = [];
    let isUploading = false;

    function formatBytes(bytes) {
        if (!bytes) return '0 B';
        const units = ['B', 'KB', 'MB', 'GB', 'TB'];
        const i = Math.min(Math.floor(Math.log(bytes) / Math.log(1024)), units.length - 1);
        return `${(bytes / Math.pow(1024, i)).toFixed(i === 0 ? 0 : 2)} ${units[i]}`;
    }

    function formatDuration(seconds) {
        if (!Number.isFinite(seconds) || seconds < 0) return 'calculating...';
        if (seconds < 60) return `${Math.ceil(seconds)} sec`;
        if (seconds < 3600) return `${Math.ceil(seconds / 60)} min`;
        const hours = Math.floor(seconds / 3600);
        const minutes = Math.ceil((seconds % 3600) / 60);
        return `${hours} hr ${minutes} min`;
    }

    function escapeHtml(value) {
        return (value || '')
            .replaceAll('&', '&amp;')
            .replaceAll('<', '&lt;')
            .replaceAll('>', '&gt;')
            .replaceAll('"', '&quot;')
            .replaceAll("'", '&#039;');
    }

    function fileClientId(file) {
        return `${file.name}|${file.size}|${file.lastModified}`;
    }

    async function sha256Hex(blob) {
        const buffer = await blob.arrayBuffer();
        const digest = await crypto.subtle.digest('SHA-256', buffer);
        return Array.from(new Uint8Array(digest)).map(b => b.toString(16).padStart(2, '0')).join('');
    }

    function setFiles(files) {
        selectedFiles = Array.from(files || []);
        renderSummary();
    }

    function renderSummary() {
        progressList.innerHTML = '';
        completedUploadIds = [];
        uploadedIds.value = '';
        createButton.disabled = true;

        if (selectedFiles.length === 0) {
            uploadSummary.classList.add('hidden');
            return;
        }

        const total = selectedFiles.reduce((sum, file) => sum + file.size, 0);
        uploadSummary.classList.remove('hidden');
        uploadSummary.textContent = `${selectedFiles.length} file${selectedFiles.length === 1 ? '' : 's'} selected - ${formatBytes(total)}`;

        for (const file of selectedFiles) {
            progressList.appendChild(createRow(file));
        }
    }

    function createRow(file) {
        const row = document.createElement('div');
        row.className = 'upload-progress-row queued';
        row.dataset.clientId = fileClientId(file);
        row.innerHTML = `
            <div class="upload-progress-title">
                <strong>${escapeHtml(file.name)}</strong>
                <span>Queued</span>
            </div>
            <div class="progress-shell"><div class="progress-fill"></div></div>
            <div class="upload-progress-detail">
                <span class="upload-status">Waiting</span>
                <span class="upload-metrics">${formatBytes(file.size)}</span>
            </div>`;
        return row;
    }

    function updateRow(file, percent, status, metrics) {
        const row = progressList.querySelector(`[data-client-id="${CSS.escape(fileClientId(file))}"]`);
        if (!row) return;
        row.classList.remove('queued');
        row.querySelector('.progress-fill').style.width = `${Math.max(0, Math.min(100, percent))}%`;
        row.querySelector('.upload-progress-title span').textContent = `${percent.toFixed(1)}%`;
        row.querySelector('.upload-status').textContent = status;
        row.querySelector('.upload-metrics').textContent = metrics || '';
        if (percent >= 100) row.classList.add('complete');
    }

    async function startUpload(file) {
        const response = await fetch('/Upload/Start', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                fileName: file.name,
                contentType: file.type || 'application/octet-stream',
                totalBytes: file.size,
                chunkSizeBytes: chunkSize,
                clientFileId: fileClientId(file),
                lastModifiedTicks: file.lastModified
            })
        });

        if (!response.ok) {
            const err = await response.json().catch(() => ({ error: 'Could not start upload.' }));
            throw new Error(err.error || 'Could not start upload.');
        }

        return await response.json();
    }

    async function uploadChunk(file, session, chunkIndex) {
        const startByte = chunkIndex * session.chunkSizeBytes;
        const endByte = Math.min(startByte + session.chunkSizeBytes, file.size);
        const blob = file.slice(startByte, endByte);
        const hash = await sha256Hex(blob);

        const data = new FormData();
        data.append('chunk', blob, `${file.name}.part${chunkIndex}`);

        const response = await fetch(`/Upload/Chunk?uploadId=${session.uploadId}&chunkIndex=${chunkIndex}&chunkSha256=${hash}`, {
            method: 'POST',
            body: data
        });

        if (!response.ok) {
            const err = await response.json().catch(() => ({ error: `Chunk ${chunkIndex} failed.` }));
            throw new Error(err.error || `Chunk ${chunkIndex} failed.`);
        }

        return await response.json();
    }

    async function completeUpload(session) {
        const response = await fetch('/Upload/Complete', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ uploadId: session.uploadId })
        });

        if (!response.ok) {
            const err = await response.json().catch(() => ({ error: 'Could not finalize upload.' }));
            throw new Error(err.error || 'Could not finalize upload.');
        }

        return await response.json();
    }

    async function uploadFile(file) {
        const startedAt = Date.now();
        updateRow(file, 0, 'Starting', formatBytes(file.size));
        const session = await startUpload(file);
        const completed = new Set(session.completedChunks || []);

        if (session.alreadyComplete || session.status === 'Complete') {
            completedUploadIds.push(session.uploadId);
            updateRow(file, 100, 'Already uploaded', 'Ready to send');
            return;
        }

        let bytesDone = session.bytesReceived || 0;
        let nextIndex = 0;

        async function worker() {
            while (nextIndex < session.totalChunks) {
                const index = nextIndex++;
                if (completed.has(index)) continue;

                const status = await uploadChunk(file, session, index);
                completed.add(index);
                bytesDone = status.bytesReceived;
                const elapsed = Math.max((Date.now() - startedAt) / 1000, 1);
                const speed = bytesDone / elapsed;
                const remaining = speed > 0 ? (file.size - bytesDone) / speed : Infinity;
                updateRow(
                    file,
                    status.percent,
                    `Uploaded ${status.chunksReceived} of ${status.totalChunks} chunks`,
                    `${formatBytes(speed)}/sec - ${formatDuration(remaining)} remaining`
                );
            }
        }

        await Promise.all(Array.from({ length: Math.min(maxParallelChunksPerFile, session.totalChunks) }, () => worker()));
        updateRow(file, 99.5, 'Finalizing', 'Merging chunks and verifying SHA256');
        const finalStatus = await completeUpload(session);
        completedUploadIds.push(finalStatus.uploadId);
        uploadedIds.value = completedUploadIds.join(',');
        updateRow(file, 100, 'Complete', `${formatBytes(file.size)} uploaded`);
    }

    async function runQueue() {
        if (isUploading) return;
        if (selectedFiles.length === 0) {
            alert('Choose one or more files first.');
            return;
        }

        isUploading = true;
        uploadButton.disabled = true;
        clearButton.disabled = true;
        uploadButton.textContent = 'Uploading...';
        completedUploadIds = [];
        uploadedIds.value = '';

        let nextFile = 0;
        async function fileWorker() {
            while (nextFile < selectedFiles.length) {
                const file = selectedFiles[nextFile++];
                await uploadFile(file);
                uploadedIds.value = completedUploadIds.join(',');
            }
        }

        try {
            await Promise.all(Array.from({ length: Math.min(maxParallelFiles, selectedFiles.length) }, () => fileWorker()));
            createButton.disabled = false;
            uploadButton.textContent = 'Sending...';
            form.submit();
        } catch (err) {
            alert(err.message || err);
            uploadButton.disabled = false;
            clearButton.disabled = false;
            uploadButton.textContent = 'Retry Upload';
        } finally {
            isUploading = false;
        }
    }

    dropZone.addEventListener('click', () => filesInput.click());
    dropZone.addEventListener('keydown', e => { if (e.key === 'Enter' || e.key === ' ') filesInput.click(); });
    dropZone.addEventListener('dragover', e => { e.preventDefault(); dropZone.classList.add('dragover'); });
    dropZone.addEventListener('dragleave', () => dropZone.classList.remove('dragover'));
    dropZone.addEventListener('drop', e => {
        e.preventDefault();
        dropZone.classList.remove('dragover');
        setFiles(e.dataTransfer.files);
    });

    filesInput.addEventListener('change', () => setFiles(filesInput.files));
    clearButton.addEventListener('click', () => {
        if (isUploading) return;
        filesInput.value = '';
        selectedFiles = [];
        renderSummary();
    });
    uploadButton.addEventListener('click', runQueue);

    form.addEventListener('submit', e => {
        if (!uploadedIds.value) {
            e.preventDefault();
            alert('Upload files first.');
        }
    });
})();

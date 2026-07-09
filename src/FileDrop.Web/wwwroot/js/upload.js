(function () {
    const chunkSize = 10 * 1024 * 1024;
    const maxParallelFiles = 3;
    const maxParallelChunksPerFile = 4;
    const maxChunkRetries = 3;

    const blockedExtensions = new Set([
        '.ade', '.adp', '.apk', '.app', '.appx', '.appxbundle', '.bat', '.cab', '.chm', '.cmd', '.com', '.cpl',
        '.dll', '.dmg', '.exe', '.gadget', '.hta', '.ins', '.iso', '.isp', '.jar', '.jnlp', '.js', '.jse',
        '.lib', '.lnk', '.mde', '.msc', '.msi', '.msix', '.msixbundle', '.msp', '.mst', '.osx', '.pif',
        '.ps1', '.ps1xml', '.ps2', '.ps2xml', '.psc1', '.psc2', '.psd1', '.psm1', '.reg', '.scr', '.sh',
        '.sys', '.vb', '.vbe', '.vbs', '.vxd', '.ws', '.wsc', '.wsf', '.wsh'
    ]);

    const form = document.getElementById('transferForm');
    const filesInput = document.getElementById('files');
    const dropZone = document.getElementById('dropZone');
    const progressList = document.getElementById('uploadProgress');
    const uploadButton = document.getElementById('uploadButton');
    const clearButton = document.getElementById('clearUploadButton');
    const createButton = document.getElementById('createButton');
    const uploadedIds = document.getElementById('UploadedIds');
    const uploadSummary = document.getElementById('uploadSummary');

    if (!form || !filesInput || !dropZone || !uploadButton || !progressList || !uploadedIds) return;

    let selectedFiles = [];
    let completedUploadIds = [];
    let isUploading = false;
    let abortRequested = false;
    const uploadControllers = new Map();
    const fileClientIds = new WeakMap();

    function newGuid() {
        if (window.crypto && typeof window.crypto.randomUUID === 'function') return window.crypto.randomUUID();
        return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, function (c) {
            const r = Math.random() * 16 | 0;
            const v = c === 'x' ? r : (r & 0x3 | 0x8);
            return v.toString(16);
        });
    }

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

    function getExtension(fileName) {
        const safe = (fileName || '').toLowerCase();
        const index = safe.lastIndexOf('.');
        return index >= 0 ? safe.substring(index) : '';
    }

    function isBlockedFile(file) {
        const ext = getExtension(file.name);
        return ext && blockedExtensions.has(ext);
    }

    function describeBlockedFiles(files) {
        return files.map(file => `${file.name} (${getExtension(file.name) || 'no extension'})`).join('\n');
    }

    function warnBlockedFiles(blocked) {
        if (!blocked.length) return;
        const plural = blocked.length === 1 ? 'This file type is not allowed' : 'These file types are not allowed';
        alert(`${plural} and will not be uploaded:\n\n${describeBlockedFiles(blocked)}\n\nRemove the blocked file(s) and choose an allowed file type.`);
    }

    function fileClientId(file) {
        if (!fileClientIds.has(file)) {
            fileClientIds.set(file, `${newGuid()}|${file.name}|${file.size}|${file.lastModified}`);
        }
        return fileClientIds.get(file);
    }

    async function sha256Hex(blob) {
        const buffer = await blob.arrayBuffer();
        const digest = await crypto.subtle.digest('SHA-256', buffer);
        return Array.from(new Uint8Array(digest)).map(b => b.toString(16).padStart(2, '0')).join('');
    }

    function setFiles(files) {
        const incoming = Array.from(files || []);
        const blocked = incoming.filter(isBlockedFile);
        const allowed = incoming.filter(file => !isBlockedFile(file));

        if (blocked.length > 0) warnBlockedFiles(blocked);

        selectedFiles = allowed;
        filesInput.value = '';
        renderSummary();
    }

    function renderSummary() {
        progressList.innerHTML = '';
        completedUploadIds = [];
        uploadedIds.value = '';
        if (createButton) createButton.disabled = true;

        if (selectedFiles.length === 0) {
            uploadSummary?.classList.add('hidden');
            return;
        }

        const total = selectedFiles.reduce((sum, file) => sum + file.size, 0);
        uploadSummary?.classList.remove('hidden');
        if (uploadSummary) uploadSummary.textContent = `${selectedFiles.length} file${selectedFiles.length === 1 ? '' : 's'} selected - ${formatBytes(total)}`;

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
        row.classList.remove('queued', 'failed');
        row.querySelector('.progress-fill').style.width = `${Math.max(0, Math.min(100, percent))}%`;
        row.querySelector('.upload-progress-title span').textContent = `${percent.toFixed(1)}%`;
        row.querySelector('.upload-status').textContent = status;
        row.querySelector('.upload-metrics').textContent = metrics || '';
        if (percent >= 100) row.classList.add('complete');
    }

    function failRow(file, message) {
        const row = progressList.querySelector(`[data-client-id="${CSS.escape(fileClientId(file))}"]`);
        if (!row) return;
        row.classList.add('failed');
        row.querySelector('.upload-status').textContent = 'Failed';
        row.querySelector('.upload-metrics').textContent = message || 'Upload failed';
    }

    async function fetchJson(url, options) {
        const response = await fetch(url, options);
        const body = await response.json().catch(() => ({}));
        if (!response.ok) throw new Error(body.error || `Request failed: ${response.status}`);
        return body;
    }

    async function startUpload(file) {
        return await fetchJson('/Upload/Start', {
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
    }

    async function uploadChunkWithRetry(file, session, chunkIndex) {
        let lastError;
        for (let attempt = 1; attempt <= maxChunkRetries; attempt++) {
            if (abortRequested) throw new Error('Upload cancelled.');
            try {
                return await uploadChunk(file, session, chunkIndex);
            } catch (err) {
                lastError = err;
                await new Promise(resolve => setTimeout(resolve, attempt * 750));
            }
        }
        throw lastError;
    }

    async function uploadChunk(file, session, chunkIndex) {
        const startByte = chunkIndex * session.chunkSizeBytes;
        const endByte = Math.min(startByte + session.chunkSizeBytes, file.size);
        const blob = file.slice(startByte, endByte);
        const hash = await sha256Hex(blob);
        const controller = new AbortController();
        uploadControllers.set(`${session.uploadId}:${chunkIndex}`, controller);

        const data = new FormData();
        data.append('chunk', blob, `${file.name}.part${chunkIndex}`);

        try {
            return await fetchJson(`/Upload/Chunk?uploadId=${session.uploadId}&chunkIndex=${chunkIndex}&chunkSha256=${hash}`, {
                method: 'POST',
                body: data,
                signal: controller.signal
            });
        } finally {
            uploadControllers.delete(`${session.uploadId}:${chunkIndex}`);
        }
    }

    async function completeUpload(session) {
        return await fetchJson('/Upload/Complete', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ uploadId: session.uploadId })
        });
    }

    async function cancelUpload(session) {
        try {
            await fetchJson('/Upload/Cancel', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ uploadId: session.uploadId })
            });
        } catch { }
    }

    function getAntiForgeryToken() {
        const token = form.querySelector('input[name="__RequestVerificationToken"]');
        return token ? token.value : '';
    }

    function showTransferError(message) {
        let summary = form.querySelector('.validation-summary');
        if (!summary) {
            summary = document.createElement('div');
            summary.className = 'validation-summary';
            form.prepend(summary);
        }
        summary.innerHTML = `<ul><li>${escapeHtml(message || 'Transfer creation failed.')}</li></ul>`;
        summary.scrollIntoView({ behavior: 'smooth', block: 'center' });
    }

    async function createTransferFromUploads() {
        const payload = {
            uploadedIds: completedUploadIds,
            recipientEmail: document.getElementById('RecipientEmail')?.value || '',
            subject: document.getElementById('Subject')?.value || '',
            message: document.getElementById('Message')?.value || '',
            expirationDays: parseInt(document.getElementById('ExpirationDays')?.value || '7', 10),
            maxDownloads: document.getElementById('MaxDownloads')?.value ? parseInt(document.getElementById('MaxDownloads').value, 10) : null,
            disableAfterFirstDownload: !!form.querySelector('input[name="DisableAfterFirstDownload"]')?.checked
        };

        const token = getAntiForgeryToken();
        const headers = { 'Content-Type': 'application/json' };
        if (token) headers['RequestVerificationToken'] = token;

        const response = await fetch('/Transfer/CreateFromUploads', {
            method: 'POST',
            headers,
            body: JSON.stringify(payload)
        });

        const body = await response.json().catch(() => ({}));
        if (!response.ok || !body.success) {
            throw new Error(body.error || `Transfer creation failed: ${response.status}`);
        }

        return body;
    }

    async function uploadFile(file) {
        if (isBlockedFile(file)) {
            const message = `${file.name} is not allowed and was not uploaded.`;
            failRow(file, message);
            throw new Error(message);
        }

        const startedAt = Date.now();
        updateRow(file, 0, 'Starting', formatBytes(file.size));
        const session = await startUpload(file);
        const completed = new Set(session.completedChunks || []);

        if (session.alreadyComplete || session.status === 'Complete') {
            completedUploadIds.push(session.uploadId);
            uploadedIds.value = completedUploadIds.join(',');
            updateRow(file, 100, 'Already uploaded', 'Ready to send');
            return;
        }

        let bytesDone = session.bytesReceived || 0;
        let nextIndex = 0;

        async function worker() {
            while (nextIndex < session.totalChunks) {
                const index = nextIndex++;
                if (completed.has(index)) continue;

                const status = await uploadChunkWithRetry(file, session, index);
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

        try {
            await Promise.all(Array.from({ length: Math.min(maxParallelChunksPerFile, session.totalChunks) }, () => worker()));
            updateRow(file, 99.5, 'Finalizing', 'Merging chunks and verifying SHA256');
            const finalStatus = await completeUpload(session);
            completedUploadIds.push(finalStatus.uploadId);
            uploadedIds.value = completedUploadIds.join(',');
            updateRow(file, 100, 'Complete', `${formatBytes(file.size)} uploaded`);
        } catch (err) {
            if (abortRequested) await cancelUpload(session);
            failRow(file, err.message || 'Upload failed');
            throw err;
        }
    }

    function updateSummaryAfterTransfer(transfer) {
        if (!uploadSummary) return;
        uploadSummary.classList.remove('hidden');
        uploadSummary.textContent = `Transfer created. ${transfer.fileCount || selectedFiles.length} file(s) attached. Emails are being sent.`;
    }

    async function runQueue() {
        if (isUploading) return;
        if (selectedFiles.length === 0) {
            alert('Choose one or more files first.');
            return;
        }

        const blocked = selectedFiles.filter(isBlockedFile);
        if (blocked.length > 0) {
            warnBlockedFiles(blocked);
            selectedFiles = selectedFiles.filter(file => !isBlockedFile(file));
            renderSummary();
            return;
        }

        isUploading = true;
        abortRequested = false;
        uploadButton.disabled = true;
        if (clearButton) clearButton.disabled = false;
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
            uploadedIds.value = completedUploadIds.join(',');

            if (completedUploadIds.length !== selectedFiles.length) {
                throw new Error(`Upload completed ${completedUploadIds.length} of ${selectedFiles.length} file(s). The transfer was not sent.`);
            }

            if (!form.reportValidity()) {
                uploadButton.disabled = false;
                uploadButton.textContent = 'Upload and Send';
                if (createButton) createButton.disabled = false;
                return;
            }

            if (createButton) createButton.disabled = false;
            uploadButton.textContent = 'Creating transfer and sending emails...';

            const transfer = await createTransferFromUploads();
            updateSummaryAfterTransfer(transfer);
            window.location.href = transfer.redirectUrl || `/Transfer/Created/${transfer.downloadToken}`;
        } catch (err) {
            if (!abortRequested) {
                showTransferError(err.message || err);
                alert(err.message || err);
            }
            uploadButton.disabled = false;
            if (clearButton) clearButton.disabled = false;
            uploadButton.textContent = abortRequested ? 'Upload Cancelled - Retry' : 'Retry Upload';
        } finally {
            isUploading = false;
        }
    }

    function cancelAll() {
        if (!isUploading) {
            filesInput.value = '';
            selectedFiles = [];
            renderSummary();
            return;
        }

        abortRequested = true;
        for (const controller of uploadControllers.values()) controller.abort();
        uploadControllers.clear();
        uploadButton.textContent = 'Cancelling...';
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
    clearButton?.addEventListener('click', cancelAll);
    uploadButton.addEventListener('click', runQueue);

    form.addEventListener('submit', e => {
        if (!uploadedIds.value) {
            e.preventDefault();
            alert('Upload files first.');
            return;
        }

        if (completedUploadIds.length > 0 && completedUploadIds.length !== selectedFiles.length) {
            e.preventDefault();
            alert('Not all selected files finished uploading. Please retry the upload.');
        }
    });
})();

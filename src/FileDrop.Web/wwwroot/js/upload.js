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
    // Phase 53 upload preflight and pre-submit scanning
    function displayPiiCategory(category) {
        const value = String(category || '').trim();
        if (!value) return 'Potential PII';
        const map = {
            CreditCard: 'Credit Card',
            CREDIT_CARD: 'Credit Card',
            SSN: 'Social Security Number',
            US_SSN: 'Social Security Number',
            Email: 'Email Address',
            EMAIL_ADDRESS: 'Email Address',
            Phone: 'Phone Number',
            PHONE_NUMBER: 'Phone Number'
        };
        return map[value] || value.replace(/_/g, ' ').replace(/([a-z])([A-Z])/g, '$1 $2');
    }

    function showUploadProtectionDialog(result) {
        const contactEmail = result.contactEmail || 'gisstaff@bgohio.gov';
        const dept = result.contactDepartment || 'GIS Department';
        const title = result.title || 'Uploads Temporarily Unavailable';
        const message = result.message || `The FileDrop server is not accepting uploads at this time. Please contact the ${dept}.`;
        const securityResult = Boolean(result.malwareStatus) || Boolean(result.piiDetected);
        const blockedFiles = (result.files || []).filter(f => !f.allowed);

        let modal = document.getElementById('uploadProtectionModal');
        if (!modal) {
            modal = document.createElement('div');
            modal.id = 'uploadProtectionModal';
            modal.className = 'upload-protection-modal';
            document.body.appendChild(modal);
        }

        let bodyHtml;
        if (securityResult) {
            const malwareStatus = String(result.malwareStatus || '').toLowerCase();
            let malwareIcon = '✓';
            let malwareIconClass = 'security-check-icon-ok';
            let malwareTitle = 'Malware Protection';
            let malwareText = 'Malware protection completed.';

            if (malwareStatus === 'protected') {
                malwareTitle = 'SentinelOne Endpoint Protection';
                malwareText = 'SentinelOne endpoint protection is active. No FileDrop on-demand malware scan was required.';
            } else if (malwareStatus === 'clean') {
                malwareTitle = 'Malware Protection';
                malwareText = 'The on-demand malware scan passed.';
            } else if (malwareStatus === 'infected' || malwareStatus === 'quarantined') {
                malwareIcon = '×';
                malwareIconClass = 'security-check-icon-block';
                malwareText = 'Malware was detected. This file cannot be uploaded.';
            } else if (malwareStatus === 'disabled') {
                malwareIcon = '–';
                malwareIconClass = 'security-check-icon-neutral';
                malwareText = 'Malware scanning is disabled by administrator policy.';
            } else if (malwareStatus) {
                malwareIcon = '!';
                malwareIconClass = 'security-check-icon-warn';
                malwareText = result.malwareMessage || 'Malware protection could not be verified.';
            }

            const malwareHtml = result.malwareStatus ? `
                <div class="security-check-section security-check-malware">
                    <div class="security-check-heading">
                        <span class="security-check-icon ${malwareIconClass}" aria-hidden="true">${malwareIcon}</span>
                        <strong>${escapeHtml(malwareTitle)}</strong>
                    </div>
                    <p>${escapeHtml(malwareText)}</p>
                </div>` : '';

            let piiHtml = '';
            if (result.piiDetected) {
                const categories = (result.piiCategories || []).map(displayPiiCategory);
                const categoryText = categories.length ? categories.join(', ') : 'Potential PII';
                const confidence = result.piiMaxConfidence || 'Unknown';
                const piiBlocked = result.allowed === false;
                const piiIntro = piiBlocked
                    ? 'Potential PII was detected at a confidence level configured to block this transfer.'
                    : 'Potential PII was detected and evaluated by FileDrop policy.';
                piiHtml = `
                    <div class="security-check-section security-check-pii">
                        <div class="security-check-heading">
                            <span class="security-check-icon ${piiBlocked ? 'security-check-icon-block' : 'security-check-icon-warn'}" aria-hidden="true">${piiBlocked ? '×' : '!'}</span>
                            <strong>PII Protection</strong>
                        </div>
                        <p>${escapeHtml(piiIntro)}</p>
                        <dl class="security-check-facts">
                            <div><dt>Type</dt><dd>${escapeHtml(categoryText)}</dd></div>
                            <div><dt>Confidence</dt><dd>${escapeHtml(confidence)}</dd></div>
                        </dl>
                        ${piiBlocked ? '<p><strong>This file is blocked by policy.</strong></p>' : ''}
                    </div>`;
            }

            const fileHtml = blockedFiles.length ? `
                <div class="security-check-file">
                    <strong>${escapeHtml(blockedFiles[0].fileName)}</strong>
                    <span>${result.piiDetected ? `Blocked because ${escapeHtml(result.piiMaxConfidence || 'the configured')} confidence ${escapeHtml((result.piiCategories || []).map(displayPiiCategory).join(', ') || 'PII')} information was detected.` : escapeHtml(blockedFiles[0].message || message)}</span>
                </div>` : '';

            bodyHtml = `${result.piiDetected ? "" : malwareHtml}${piiHtml}${fileHtml}`;
        } else {
            const details = blockedFiles.map(f => `<li><strong>${escapeHtml(f.fileName)}</strong>: ${escapeHtml(f.message)}</li>`).join('');
            bodyHtml = `<p>${escapeHtml(message)}</p>${details ? `<ul>${details}</ul>` : ''}`;
        }

        modal.innerHTML = `
            <div class="upload-protection-backdrop"></div>
            <div class="upload-protection-card" role="alertdialog" aria-modal="true">
                <h2>${escapeHtml(title)}</h2>
                ${bodyHtml}
                <div class="security-contact-block">
                    <p>Please contact the <strong>${escapeHtml(dept)}</strong> for assistance.</p>
                    <p><a href="mailto:${escapeHtml(contactEmail)}">${escapeHtml(contactEmail)}</a></p>
                </div>
                <div class="upload-protection-actions">
                    ${result.piiReviewUrl ? `<a class="button" href="${escapeHtml(result.piiReviewUrl)}">Review PII Warnings</a>` : ''}
                    <button type="button" id="uploadProtectionClose">Close</button>
                    <a class="button" href="mailto:${escapeHtml(contactEmail)}">Email ${escapeHtml(dept)}</a>
                </div>
            </div>`;
        modal.classList.add('open');
        document.getElementById('uploadProtectionClose')?.addEventListener('click', () => modal.classList.remove('open'));
    }

    async function preflightSelectedFiles() {
        const payload = {
            files: selectedFiles.map(file => ({ fileName: file.name, contentType: file.type || 'application/octet-stream', totalBytes: file.size }))
        };
        const response = await fetchJson('/Upload/Preflight', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(payload)
        });
        if (!response.allowed || !response.acceptingUploads) {
            showUploadProtectionDialog(response);
            return false;
        }
        return true;
    }

    function confidenceClass(confidence) {
        const value = String(confidence || '').toLowerCase();
        if (value === 'high') return 'pii-confidence-high';
        if (value === 'medium') return 'pii-confidence-medium';
        return 'pii-confidence-low';
    }

    function showPiiAcknowledgementDialog(result, file) {
        return new Promise((resolve, reject) => {
            let modal = document.getElementById('piiAcknowledgementModal');
            if (!modal) {
                modal = document.createElement('div');
                modal.id = 'piiAcknowledgementModal';
                modal.className = 'upload-protection-modal';
                document.body.appendChild(modal);
            }

            const findings = Array.isArray(result.piiFindings) ? result.piiFindings : [];
            const findingHtml = findings.length
                ? findings.map(f => `
                    <div class="pii-context-finding">
                        <div class="pii-context-heading">
                            <strong>${escapeHtml(f.category || 'Potential PII')}</strong>
                            <span class="pii-confidence-badge ${confidenceClass(f.confidence)}">${escapeHtml(f.confidence || 'Low')} confidence</span>
                        </div>
                        <div class="pii-context-meta">${escapeHtml(f.location || 'Document text')} &bull; ${escapeHtml(f.redactedValue || '[redacted]')} &bull; ${escapeHtml(String(f.count || 1))} match(es)</div>
                        <div class="pii-context-reason">${escapeHtml(f.reason || 'Pattern matched configured PII detection rules.')}</div>
                        <div class="pii-context-excerpt">&ldquo;${escapeHtml(f.context || 'Context unavailable.')} &rdquo;</div>
                    </div>`).join('')
                : '<p>No redacted context was returned by the scanner.</p>';

            modal.innerHTML = `
                <div class="upload-protection-backdrop"></div>
                <div class="upload-protection-card pii-ack-card" role="dialog" aria-modal="true" aria-labelledby="piiAckTitle">
                    <h2 id="piiAckTitle">Potential PII detected</h2>
                    <p><strong>${escapeHtml(file.name)}</strong></p>
                    <p>${escapeHtml(result.message || 'Potential sensitive information was detected.')}</p>
                    <div class="pii-context-list">${findingHtml}</div>
                    <label class="pii-acknowledge-row">
                        <input type="checkbox" id="piiAcknowledgeCheckbox" />
                        <span>I acknowledge that this file may contain sensitive information and confirm that I am authorized to send it.</span>
                    </label>
                    <p class="pii-ack-note">Continuing records this acknowledgement, the detected categories, confidence, file, user, time, and IP address in the FileDrop audit log. Unredacted PII values are not logged.</p>
                    <div class="upload-protection-actions">
                    ${result.piiReviewUrl ? `<a class="button" href="${escapeHtml(result.piiReviewUrl)}">Review PII Warnings</a>` : ''}
                        <button type="button" id="piiAckCancel">Cancel</button>
                        <button type="button" id="piiAckContinue" disabled>Continue Upload</button>
                    </div>
                </div>`;
            modal.classList.add('open');

            const checkbox = document.getElementById('piiAcknowledgeCheckbox');
            const continueButton = document.getElementById('piiAckContinue');
            const cancelButton = document.getElementById('piiAckCancel');
            checkbox?.addEventListener('change', () => {
                if (continueButton) continueButton.disabled = !checkbox.checked;
            });
            cancelButton?.addEventListener('click', () => {
                modal.classList.remove('open');
                reject(new Error('Upload cancelled because the PII warning was not acknowledged.'));
            });
            continueButton?.addEventListener('click', () => {
                if (!checkbox?.checked) return;
                modal.classList.remove('open');
                resolve(true);
            });
        });
    }

    async function acknowledgePiiWarning(uploadId) {
        return await fetchJson('/Upload/AcknowledgePii', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ uploadId, accepted: true })
        });
    }

    async function scanCompletedUpload(uploadId, file) {
        updateRow(file, 99.8, 'Scanning', 'Security scan before submit');
        const result = await fetchJson('/Upload/ScanCompleted', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ uploadId })
        });

        if (result.requiresAcknowledgement) {
            await showPiiAcknowledgementDialog(result, file);
            const accepted = await acknowledgePiiWarning(uploadId);
            if (!accepted.allowed) throw new Error(accepted.message || 'PII acknowledgement was not accepted.');
            updateRow(file, 99.9, 'PII warning acknowledged', `${result.piiFindingCount || 0} finding(s); acknowledgement logged`);
            return {
                ...result,
                allowed: true,
                requiresAcknowledgement: false,
                piiStatus: 'WarningAccepted',
                message: accepted.message || 'PII warning acknowledged and logged.'
            };
        }

        if (!result.allowed && result.pendingPiiReview) {
            const reviewUrl = `/Upload/PiiReview/${encodeURIComponent(uploadId)}`;
            updateRow(file, 100, 'Uploaded - Pending GIS Review',
                'Upload retained securely; no recipient access granted.');
            showUploadProtectionDialog({
                allowed: false,
                acceptingUploads: true,
                title: 'Uploaded - Pending GIS Review',
                message: result.message || 'Inspection incomplete. The uploaded file is securely held for GIS review.',
                contactEmail: 'gisstaff@bgohio.gov',
                contactDepartment: 'GIS Department',
                malwareStatus: result.malwareStatus,
                piiStatus: result.piiStatus,
                piiReviewUrl: reviewUrl,
                files: [{ fileName: file.name, allowed: false,
                    message: 'Retained securely, pending review and approval.' }]
            });
            return { ...result, pendingPiiReview: true };
        }

        if (!result.allowed) {
            const piiText = result.piiDetected
                ? ` Potential PII: ${(result.piiCategories || []).join(', ')}. Maximum confidence: ${result.piiMaxConfidence || 'High'}.`
                : '';
            showUploadProtectionDialog({
                allowed: false,
                acceptingUploads: false,
                title: result.status || 'File Blocked',
                message: (result.message || 'This file was blocked by the security scan.') + piiText,
                contactEmail: 'gisstaff@bgohio.gov',
                contactDepartment: 'GIS Department',
                malwareStatus: result.malwareStatus,
                malwareMessage: result.malwareMessage,
                piiDetected: result.piiDetected,
                piiCategories: result.piiCategories || [],
                piiMaxConfidence: result.piiMaxConfidence,
                piiStatus: result.piiStatus,
                piiFindingCount: result.piiFindingCount || 0,
                piiReviewUrl: result.piiDetected && String(result.piiStatus || "").toLowerCase() === "blocked" && (result.malwareStatus || "").toLowerCase() !== "infected" && (result.malwareStatus || "").toLowerCase() !== "quarantined" ? `/Upload/PiiReview/${encodeURIComponent(uploadId)}` : null,
                files: [{ fileName: file.name, allowed: false, message: result.message || result.status || 'Blocked' }]
            });
            throw new Error(result.message || 'File blocked by security scan.');
        }

        if (result.piiDetected) {
            updateRow(file, 99.9, 'PII scan complete', `${result.piiFindingCount || 0} finding(s): ${(result.piiCategories || []).join(', ') || 'review complete'}`);
        } else {
            updateRow(file, 99.9, 'Inspection passed', 'Malware and PII checks complete');
        }
        return result;
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

        // Phase73IOriginalRecipientFix
        // Retain the original transfer draft by upload ID.
        // The receiving server must independently validate
        // these values before creating a transfer.
        try {
            const originalTransfer = {
                recipientEmail:
                    document.getElementById('RecipientEmail')?.value?.trim() || '',
                subject:
                    document.getElementById('Subject')?.value || '',
                message:
                    document.getElementById('Message')?.value || '',
                expirationDays:
                    parseInt(document.getElementById('ExpirationDays')?.value || '7', 10),
                maxDownloads:
                    document.getElementById('MaxDownloads')?.value || '',
                disableAfterFirstDownload:
                    !!form.querySelector('input[name="DisableAfterFirstDownload"]')?.checked,
                savedAt: Date.now()
            };

            if (originalTransfer.recipientEmail && session.uploadId) {
                localStorage.setItem(
                    'filedrop-pii-transfer-' + session.uploadId,
                    JSON.stringify(originalTransfer)
                );
            }
        } catch (handoffError) {
            console.warn(
                'Original transfer details could not be retained.',
                handoffError
            );
        }

        const completed = new Set(session.completedChunks || []);

        if (session.alreadyComplete || session.status === 'Complete') {
            const inspection = await scanCompletedUpload(session.uploadId, file);
            if (inspection.pendingPiiReview) return 'pendingReview';
            completedUploadIds.push(session.uploadId);
            uploadedIds.value = completedUploadIds.join(',');
            updateRow(
                file,
                100,
                inspection.piiDetected ? 'Complete - PII warning' : 'Complete',
                inspection.piiDetected ? inspection.message : 'Ready to send'
            );
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
            const inspection = await scanCompletedUpload(finalStatus.uploadId, file);
            if (inspection.pendingPiiReview) return 'pendingReview';
            completedUploadIds.push(finalStatus.uploadId);
            uploadedIds.value = completedUploadIds.join(',');
            updateRow(
                file,
                100,
                inspection.piiDetected ? 'Complete - PII warning' : 'Complete',
                inspection.piiDetected ? inspection.message : `${formatBytes(file.size)} uploaded - inspection passed`
            );
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

        if (!(await preflightSelectedFiles())) {
            uploadButton.disabled = false;
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
        let pendingReviewCount = 0;
        async function fileWorker() {
            while (nextFile < selectedFiles.length) {
                const file = selectedFiles[nextFile++];
                const outcome = await uploadFile(file);
                if (outcome === 'pendingReview') pendingReviewCount++;
                uploadedIds.value = completedUploadIds.join(',');
            }
        }

        try {
            await Promise.all(Array.from({ length: Math.min(maxParallelFiles, selectedFiles.length) }, () => fileWorker()));
            uploadedIds.value = completedUploadIds.join(',');

            if (pendingReviewCount > 0) {
                uploadButton.textContent = 'Review required before sending';
                if (uploadSummary) {
                    uploadSummary.classList.remove('hidden');
                    uploadSummary.textContent = `${pendingReviewCount} file(s) uploaded and held for GIS review. No transfer or email was sent. Use the Review link shown for each held file.`;
                }
                return;
            }

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

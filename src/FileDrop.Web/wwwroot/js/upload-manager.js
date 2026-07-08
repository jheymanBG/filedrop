(function () {
    const refreshButton = document.getElementById('refreshUploadDashboard');
    const metrics = document.getElementById('uploadLiveMetrics');
    const activeTable = document.querySelector('#activeUploadsTable tbody');
    const recentTable = document.querySelector('#recentUploadsTable tbody');

    if (!metrics || !activeTable) return;

    function formatBytes(value) {
        value = Number(value || 0);
        const units = ['B', 'KB', 'MB', 'GB', 'TB'];
        let unit = 0;
        while (value >= 1024 && unit < units.length - 1) {
            value /= 1024;
            unit++;
        }
        return `${value.toFixed(unit === 0 ? 0 : 2)} ${units[unit]}`;
    }

    function escapeHtml(value) {
        return String(value || '')
            .replaceAll('&', '&amp;')
            .replaceAll('<', '&lt;')
            .replaceAll('>', '&gt;')
            .replaceAll('"', '&quot;')
            .replaceAll("'", '&#039;');
    }

    function localDate(value) {
        if (!value) return '-';
        const date = new Date(value);
        return Number.isNaN(date.getTime()) ? '-' : date.toLocaleString();
    }

    function setMetric(name, value) {
        const el = metrics.querySelector(`[data-field="${name}"]`);
        if (el) el.textContent = value;
    }

    async function refresh() {
        refreshButton && (refreshButton.disabled = true);
        try {
            const response = await fetch('/Admin/Uploads/Live', { headers: { 'Accept': 'application/json' } });
            if (!response.ok) throw new Error(`Dashboard refresh failed: ${response.status}`);
            const data = await response.json();

            setMetric('activeUploadCount', data.activeUploadCount || 0);
            setMetric('finalizingUploadCount', data.finalizingUploadCount || 0);
            setMetric('completedUploadCount24h', data.completedUploadCount24h || 0);
            setMetric('failedUploadCount24h', data.failedUploadCount24h || 0);
            setMetric('abandonedUploadCount', data.abandonedUploadCount || 0);
            setMetric('activePercent', `${Number(data.activePercent || 0).toFixed(1)}%`);

            const active = data.activeUploads || [];
            activeTable.innerHTML = active.length === 0
                ? '<tr><td colspan="6" class="muted">No active uploads.</td></tr>'
                : active.map(row => `
                    <tr>
                        <td class="hash">${escapeHtml(row.originalFileName)}</td>
                        <td>${escapeHtml(row.createdByEmail)}</td>
                        <td>${escapeHtml(row.status)}</td>
                        <td>${Number(row.percent || 0).toFixed(1)}% (${row.chunksReceived || 0}/${row.totalChunks || 0})</td>
                        <td>${formatBytes(row.bytesReceived)} / ${formatBytes(row.totalBytes)}</td>
                        <td>${localDate(row.lastActivityDate)}</td>
                    </tr>`).join('');

            if (recentTable) {
                const recent = data.recentUploads || [];
                recentTable.innerHTML = recent.length === 0
                    ? '<tr><td colspan="6" class="muted">No completed, failed, or cancelled upload sessions yet.</td></tr>'
                    : recent.map(row => `
                        <tr>
                            <td class="hash">${escapeHtml(row.originalFileName)}</td>
                            <td>${escapeHtml(row.createdByEmail)}</td>
                            <td>${escapeHtml(row.status)}</td>
                            <td>${formatBytes(row.totalBytes)}</td>
                            <td>${localDate(row.completedDate || row.lastActivityDate)}</td>
                            <td class="hash">${escapeHtml(row.sha256Hash || '-')}</td>
                        </tr>`).join('');
            }
        } catch (err) {
            console.warn(err);
        } finally {
            refreshButton && (refreshButton.disabled = false);
        }
    }

    refreshButton?.addEventListener('click', refresh);
    window.setInterval(refresh, 15000);
})();

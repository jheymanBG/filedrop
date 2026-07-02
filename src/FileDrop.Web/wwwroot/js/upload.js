(function () {
    const fileInput = document.getElementById("Files");
    const dropZone = document.getElementById("dropZone");
    const fileList = document.getElementById("fileList");
    const fileSummary = document.getElementById("fileSummary");
    const fileCount = document.getElementById("fileCount");
    const totalSize = document.getElementById("totalSize");
    const clearFiles = document.getElementById("clearFiles");
    const form = document.getElementById("transferForm");
    const uploadButton = document.getElementById("uploadButton");
    const uploadStatus = document.getElementById("uploadStatus");

    if (!fileInput || !dropZone) return;

    function formatBytes(bytes) {
        if (bytes === 0) return "0 B";
        const units = ["B", "KB", "MB", "GB", "TB"];
        const i = Math.floor(Math.log(bytes) / Math.log(1024));
        return `${(bytes / Math.pow(1024, i)).toFixed(i === 0 ? 0 : 2)} ${units[i]}`;
    }

    function renderFiles() {
        fileList.innerHTML = "";
        const files = Array.from(fileInput.files || []);
        const bytes = files.reduce((sum, f) => sum + f.size, 0);

        if (files.length === 0) {
            fileSummary.classList.add("hidden");
            return;
        }

        fileSummary.classList.remove("hidden");
        fileCount.innerText = files.length === 1 ? "1 file" : `${files.length} files`;
        totalSize.innerText = ` â€¢ ${formatBytes(bytes)}`;

        for (const file of files) {
            const li = document.createElement("li");
            li.innerHTML = `
                <div>
                    <strong>${file.name}</strong>
                    <span>${formatBytes(file.size)}</span>
                </div>
            `;
            fileList.appendChild(li);
        }
    }

    dropZone.addEventListener("click", () => fileInput.click());

    dropZone.addEventListener("dragover", e => {
        e.preventDefault();
        dropZone.classList.add("dragover");
    });

    dropZone.addEventListener("dragleave", () => {
        dropZone.classList.remove("dragover");
    });

    dropZone.addEventListener("drop", e => {
        e.preventDefault();
        dropZone.classList.remove("dragover");
        if (e.dataTransfer.files.length) {
            fileInput.files = e.dataTransfer.files;
            renderFiles();
        }
    });

    fileInput.addEventListener("change", renderFiles);

    clearFiles.addEventListener("click", () => {
        fileInput.value = "";
        renderFiles();
    });

    form.addEventListener("submit", () => {
        uploadButton.disabled = true;
        uploadButton.innerText = "Uploading...";
        uploadStatus.classList.remove("hidden");
    });
})();

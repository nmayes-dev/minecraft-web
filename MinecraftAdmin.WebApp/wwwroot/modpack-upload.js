window.modpackUpload = (() => {
    const uploads = new Map();

    function getFile(input) {
        if (!input || !input.files || input.files.length === 0)
            return null;

        return input.files[0];
    }

    return {
        getFileInfo(input) {
            const file = getFile(input);
            if (!file)
                return null;

            return {
                name: file.name,
                size: file.size
            };
        },

        start(id, input, name, environment, dotNetReference) {
            if (uploads.has(id))
                throw new Error("An upload with this id is already in progress.");

            const file = getFile(input);
            if (!file)
                throw new Error("Select a server pack ZIP first.");

            const formData = new FormData();
            formData.append("name", name ?? "");
            formData.append("environment", environment ?? "");
            formData.append("fileSize", file.size.toString());
            formData.append("serverPack", file, file.name);

            const xhr = new XMLHttpRequest();
            let lastProgressUpdate = 0;

            uploads.set(id, { xhr, dotNetReference });
            xhr.open("POST", "/api/modpacks/upload", true);

            xhr.upload.onprogress = event => {
                if (!event.lengthComputable)
                    return;

                const now = performance.now();
                const finished = event.loaded >= event.total;
                if (!finished && now - lastProgressUpdate < 100)
                    return;

                lastProgressUpdate = now;

                // XHR reports progress for the entire multipart body. Convert that
                // proportion back to the selected file size so the UI shows useful
                // file-byte figures instead of multipart framing bytes.
                const fraction = event.total > 0 ? event.loaded / event.total : 0;
                const fileBytes = Math.min(file.size, Math.round(file.size * fraction));
                void dotNetReference.invokeMethodAsync("OnUploadProgress", fileBytes, file.size);
            };

            xhr.onload = () => {
                uploads.delete(id);
                void dotNetReference.invokeMethodAsync("OnUploadProgress", file.size, file.size)
                    .then(() => dotNetReference.invokeMethodAsync("OnUploadCompleted", xhr.status, xhr.responseText ?? ""));
            };

            xhr.onerror = () => {
                uploads.delete(id);
                void dotNetReference.invokeMethodAsync("OnUploadFailed", "The upload failed because of a network error.");
            };

            xhr.onabort = () => {
                const entry = uploads.get(id);
                uploads.delete(id);
                if (entry?.notifyOnAbort !== false)
                    void dotNetReference.invokeMethodAsync("OnUploadCancelled");
            };

            xhr.send(formData);
        },

        cancel(id, notifyOnAbort = true) {
            const upload = uploads.get(id);
            if (!upload)
                return;

            upload.notifyOnAbort = notifyOnAbort;
            upload.xhr.abort();
        }
    };
})();

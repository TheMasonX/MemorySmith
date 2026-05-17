(function () {
    window.memorySmith = window.memorySmith || {};

    window.memorySmith.markdown = {
        insert: function (textarea, prefix, suffix, placeholder) {
            if (!textarea) {
                return "";
            }

            const start = textarea.selectionStart || 0;
            const end = textarea.selectionEnd || 0;
            const selected = textarea.value.substring(start, end) || placeholder || "";
            const inserted = `${prefix || ""}${selected}${suffix || ""}`;
            textarea.value = textarea.value.substring(0, start) + inserted + textarea.value.substring(end);
            const cursor = start + inserted.length;
            textarea.focus();
            textarea.setSelectionRange(cursor, cursor);
            textarea.dispatchEvent(new Event("input", { bubbles: true }));
            return textarea.value;
        }
    };

    window.memorySmith.chat = {
        registerComposer: function (textarea, dotNetRef, sendOnEnter) {
            if (!textarea) {
                return;
            }

            if (textarea.memorySmithComposerKeyHandler) {
                textarea.removeEventListener("keydown", textarea.memorySmithComposerKeyHandler);
            }
            if (textarea.memorySmithComposerPasteHandler) {
                textarea.removeEventListener("paste", textarea.memorySmithComposerPasteHandler, true);
            }
            if (textarea.memorySmithComposerDocumentPasteHandler) {
                document.removeEventListener("paste", textarea.memorySmithComposerDocumentPasteHandler, true);
            }

            textarea.dataset.sendOnEnter = sendOnEnter ? "true" : "false";
            textarea.memorySmithComposerKeyHandler = function (event) {
                if (event.key === "Enter" && !event.shiftKey && textarea.dataset.sendOnEnter === "true") {
                    const message = textarea.value;
                    event.preventDefault();
                    textarea.value = "";
                    textarea.dispatchEvent(new Event("input", { bubbles: true }));
                    dotNetRef.invokeMethodAsync("SendFromKeyboard", message);
                }
            };
            textarea.memorySmithComposerPasteHandler = function (event) {
                void window.memorySmith.chat.attachClipboardImage(event, dotNetRef);
            };
            textarea.memorySmithComposerDocumentPasteHandler = function (event) {
                const shell = textarea.closest(".chat-shell");
                const target = event.target;
                if (shell && target instanceof Node && !shell.contains(target) && target !== document.body) {
                    return;
                }

                void window.memorySmith.chat.attachClipboardImage(event, dotNetRef);
            };
            textarea.addEventListener("keydown", textarea.memorySmithComposerKeyHandler);
            textarea.addEventListener("paste", textarea.memorySmithComposerPasteHandler, true);
            document.addEventListener("paste", textarea.memorySmithComposerDocumentPasteHandler, true);
        },

        unregisterComposer: function (textarea) {
            if (!textarea) {
                return;
            }

            if (textarea.memorySmithComposerKeyHandler) {
                textarea.removeEventListener("keydown", textarea.memorySmithComposerKeyHandler);
                textarea.memorySmithComposerKeyHandler = null;
            }
            if (textarea.memorySmithComposerPasteHandler) {
                textarea.removeEventListener("paste", textarea.memorySmithComposerPasteHandler, true);
                textarea.memorySmithComposerPasteHandler = null;
            }
            if (textarea.memorySmithComposerDocumentPasteHandler) {
                document.removeEventListener("paste", textarea.memorySmithComposerDocumentPasteHandler, true);
                textarea.memorySmithComposerDocumentPasteHandler = null;
            }
        },

        attachClipboardImage: async function (event, dotNetRef) {
            if (!dotNetRef || event.defaultPrevented || event.memorySmithClipboardHandled) {
                return false;
            }

            event.memorySmithClipboardHandled = true;
            const clipboardData = event.clipboardData || window.clipboardData;
            let files = window.memorySmith.chat.getClipboardImageFiles(clipboardData);
            const hasImageHint = files.length > 0 || window.memorySmith.chat.clipboardHasImageReference(clipboardData);
            if (hasImageHint) {
                event.preventDefault();
                event.stopPropagation();
            }

            if (files.length === 0) {
                files = await window.memorySmith.chat.getClipboardReferencedImages(clipboardData);
            }

            if (files.length === 0) {
                files = await window.memorySmith.chat.readNavigatorClipboardImages();
            }

            if (files.length === 0) {
                return false;
            }

            event.preventDefault();
            event.stopPropagation();
            await window.memorySmith.chat.attachImageFiles(files, dotNetRef);
            return true;
        },

        getClipboardImageFiles: function (clipboardData) {
            const files = [];
            const seen = new Set();
            const addFile = function (file, hintedType) {
                if (!file || !window.memorySmith.chat.isImageFile(file, hintedType)) {
                    return;
                }

                const key = `${file.name || ""}|${file.type || hintedType || ""}|${file.size || 0}|${file.lastModified || 0}`;
                if (!seen.has(key)) {
                    seen.add(key);
                    files.push(file);
                }
            };

            if (clipboardData && clipboardData.files) {
                Array.from(clipboardData.files).forEach(file => addFile(file, file.type));
            }

            if (clipboardData && clipboardData.items) {
                Array.from(clipboardData.items).forEach(item => {
                    if (item.kind !== "file" || typeof item.getAsFile !== "function") {
                        return;
                    }

                    addFile(item.getAsFile(), item.type);
                });
            }

            return files;
        },

        clipboardHasImageReference: function (clipboardData) {
            const text = window.memorySmith.chat.readClipboardText(clipboardData, "text/html") + "\n" + window.memorySmith.chat.readClipboardText(clipboardData, "text/plain");
            return /<img\b/i.test(text) || /data:image\//i.test(text) || /^https?:\/\/\S+\.(png|jpe?g|gif|webp|bmp|heic|heif)(\?\S*)?$/im.test(text.trim());
        },

        getClipboardReferencedImages: async function (clipboardData) {
            const references = window.memorySmith.chat.extractImageReferences(
                window.memorySmith.chat.readClipboardText(clipboardData, "text/html"),
                window.memorySmith.chat.readClipboardText(clipboardData, "text/plain"));
            const files = [];
            let index = 0;
            for (const reference of references) {
                const file = await window.memorySmith.chat.referenceToImageFile(reference, ++index);
                if (file) {
                    files.push(file);
                }
            }

            return files;
        },

        readClipboardText: function (clipboardData, type) {
            if (!clipboardData || typeof clipboardData.getData !== "function") {
                return "";
            }

            try {
                return clipboardData.getData(type) || "";
            } catch {
                return "";
            }
        },

        extractImageReferences: function (html, plainText) {
            const references = [];
            const add = function (value) {
                const reference = String(value || "").trim();
                if (!reference || references.includes(reference)) {
                    return;
                }

                if (/^data:image\//i.test(reference) || /^https?:\/\//i.test(reference) || /^blob:/i.test(reference)) {
                    references.push(reference);
                }
            };

            const dataUrlMatches = `${html || ""}\n${plainText || ""}`.match(/data:image\/[a-z0-9.+-]+;base64,[A-Za-z0-9+/=\r\n]+/gi) || [];
            dataUrlMatches.forEach(match => add(match.replace(/\s+/g, "")));

            if (html) {
                try {
                    const document = new DOMParser().parseFromString(html, "text/html");
                    Array.from(document.images || []).forEach(image => add(image.currentSrc || image.src));
                } catch {
                }
            }

            const plain = String(plainText || "").trim();
            if (/^https?:\/\/\S+\.(png|jpe?g|gif|webp|bmp|heic|heif)(\?\S*)?$/i.test(plain)) {
                add(plain);
            }

            return references.slice(0, 4);
        },

        referenceToImageFile: async function (reference, index) {
            try {
                const response = await fetch(reference);
                if (!response.ok) {
                    return null;
                }

                const blob = await response.blob();
                if (!window.memorySmith.chat.isImageFile(blob, blob.type)) {
                    return null;
                }

                const contentType = blob.type || "image/png";
                const extension = window.memorySmith.chat.extensionForContentType(contentType);
                const stamp = new Date().toISOString().replace(/[:.]/g, "-");
                return new File([blob], `clipboard-${stamp}-${index}.${extension}`, { type: contentType });
            } catch {
                return null;
            }
        },

        readNavigatorClipboardImages: async function () {
            if (!navigator.clipboard || typeof navigator.clipboard.read !== "function") {
                return [];
            }

            try {
                const items = await navigator.clipboard.read();
                const files = [];
                let index = 0;
                for (const item of items) {
                    const type = (item.types || []).find(candidate => candidate && candidate.toLowerCase().startsWith("image/"));
                    if (!type) {
                        continue;
                    }

                    const blob = await item.getType(type);
                    const extension = window.memorySmith.chat.extensionForContentType(type);
                    const stamp = new Date().toISOString().replace(/[:.]/g, "-");
                    files.push(new File([blob], `clipboard-${stamp}-${++index}.${extension}`, { type }));
                }

                return files;
            } catch {
                return [];
            }
        },

        attachImageFiles: async function (files, dotNetRef) {
            let index = 0;
            for (const file of files) {
                const result = await window.memorySmith.chat.readFileAsBase64(file);
                const contentType = file.type || "image/png";
                const extension = window.memorySmith.chat.extensionForContentType(contentType);
                const stamp = new Date().toISOString().replace(/[:.]/g, "-");
                const name = file.name || `clipboard-${stamp}-${++index}.${extension}`;
                await dotNetRef.invokeMethodAsync("AttachClipboardImage", name, contentType, result, file.size || 0);
            }
        },

        readFileAsBase64: function (file) {
            return new Promise(function (resolve, reject) {
                const reader = new FileReader();
                reader.onerror = function () {
                    reject(reader.error || new Error("Clipboard image could not be read."));
                };
                reader.onload = function () {
                    const result = String(reader.result || "");
                    const comma = result.indexOf(",");
                    resolve(comma >= 0 ? result.substring(comma + 1) : result);
                };
                reader.readAsDataURL(file);
            });
        },

        isImageFile: function (file, hintedType) {
            const type = String(file.type || hintedType || "").toLowerCase();
            if (type.startsWith("image/")) {
                return true;
            }

            return /\.(png|jpe?g|gif|webp|bmp|heic|heif)$/i.test(file.name || "");
        },

        extensionForContentType: function (contentType) {
            const subtype = String(contentType || "image/png").split("/")[1] || "png";
            const clean = subtype.split("+")[0].replace(/[^a-z0-9]/gi, "").toLowerCase();
            return clean || "png";
        },

        setSendOnEnter: function (textarea, enabled) {
            if (textarea) {
                textarea.dataset.sendOnEnter = enabled ? "true" : "false";
            }
        },

        scrollToBottom: function (element) {
            if (element) {
                element.scrollTop = element.scrollHeight;
            }
        }
    };

    window.memorySmith.storage = {
        getJson: function (key) {
            try {
                const value = localStorage.getItem(key);
                return value ? JSON.parse(value) : null;
            } catch {
                return null;
            }
        },

        setJson: function (key, value) {
            try {
                localStorage.setItem(key, JSON.stringify(value));
                return true;
            } catch {
                return false;
            }
        }
    };
})();
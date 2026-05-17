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
                textarea.removeEventListener("paste", textarea.memorySmithComposerPasteHandler);
            }
            if (textarea.memorySmithComposerDocumentPasteHandler) {
                document.removeEventListener("paste", textarea.memorySmithComposerDocumentPasteHandler);
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
                window.memorySmith.chat.attachClipboardImage(event, dotNetRef);
            };
            textarea.memorySmithComposerDocumentPasteHandler = function (event) {
                const shell = textarea.closest(".chat-shell");
                const target = event.target;
                if (shell && target instanceof Node && !shell.contains(target) && target !== document.body) {
                    return;
                }

                window.memorySmith.chat.attachClipboardImage(event, dotNetRef);
            };
            textarea.addEventListener("keydown", textarea.memorySmithComposerKeyHandler);
            textarea.addEventListener("paste", textarea.memorySmithComposerPasteHandler);
            document.addEventListener("paste", textarea.memorySmithComposerDocumentPasteHandler);
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
                textarea.removeEventListener("paste", textarea.memorySmithComposerPasteHandler);
                textarea.memorySmithComposerPasteHandler = null;
            }
            if (textarea.memorySmithComposerDocumentPasteHandler) {
                document.removeEventListener("paste", textarea.memorySmithComposerDocumentPasteHandler);
                textarea.memorySmithComposerDocumentPasteHandler = null;
            }
        },

        attachClipboardImage: function (event, dotNetRef) {
                if (event.defaultPrevented) {
                    return false;
                }

                const items = event.clipboardData && event.clipboardData.items ? Array.from(event.clipboardData.items) : [];
                const imageItem = items.find(item => item.kind === "file" && item.type && item.type.startsWith("image/"));
                if (!imageItem) {
                    return false;
                }

                const file = imageItem.getAsFile();
                if (!file) {
                    return false;
                }

                event.preventDefault();
                event.stopPropagation();
                const reader = new FileReader();
                reader.onload = function () {
                    const result = String(reader.result || "");
                    const comma = result.indexOf(",");
                    const base64 = comma >= 0 ? result.substring(comma + 1) : result;
                    const stamp = new Date().toISOString().replace(/[:.]/g, "-");
                    const extension = (file.type.split("/")[1] || "png").replace(/[^a-z0-9]/gi, "").toLowerCase() || "png";
                    dotNetRef.invokeMethodAsync("AttachClipboardImage", file.name || `clipboard-${stamp}.${extension}`, file.type || "image/png", base64, file.size);
                };
                reader.readAsDataURL(file);
                return true;
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
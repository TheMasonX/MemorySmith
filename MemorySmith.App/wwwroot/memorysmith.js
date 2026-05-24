(function () {
    window.memorySmith = window.memorySmith || {};

    const mermaidThemeStorageKey = "memorysmith.markdown.mermaidTheme.v1";
    const headingCollapseStoragePrefix = "memorysmith.pages.headingCollapse.v1:";
    const mermaidThemeModes = new Set(["auto", "light", "dark"]);
    const reconnectRecoveryStorageKey = "memorysmith.reconnect.resumeFailedReloadAt.v1";
    const reconnectRecoveryCooldownMs = 15000;
    let mermaidSequence = 0;
    let mermaidTheme = null;
    let mermaidThemeWatcher = null;

    function getLastReconnectRecoveryAttempt() {
        try {
            const value = Number.parseInt(sessionStorage.getItem(reconnectRecoveryStorageKey) || "0", 10);
            return Number.isFinite(value) ? value : 0;
        } catch {
            return 0;
        }
    }

    function markReconnectRecoveryAttempt(at) {
        try {
            sessionStorage.setItem(reconnectRecoveryStorageKey, String(at));
        } catch {
        }
    }

    function recoverFromResumeFailureIfNeeded() {
        const resumeFailedModal = document.querySelector(".components-reconnect-resume-failed");
        if (!resumeFailedModal) {
            return;
        }

        const now = Date.now();
        if (now - getLastReconnectRecoveryAttempt() < reconnectRecoveryCooldownMs) {
            return;
        }

        markReconnectRecoveryAttempt(now);
        window.location.reload();
    }

    function initializeReconnectRecovery() {
        const observe = function () {
            if (!document.body || typeof MutationObserver !== "function") {
                return false;
            }

            const observer = new MutationObserver(recoverFromResumeFailureIfNeeded);
            observer.observe(document.body, {
                subtree: true,
                attributes: true,
                childList: true,
                attributeFilter: ["class"]
            });

            return true;
        };

        recoverFromResumeFailureIfNeeded();
        if (!observe()) {
            document.addEventListener("DOMContentLoaded", function () {
                observe();
                recoverFromResumeFailureIfNeeded();
            }, { once: true });
        }

        // Polling is a fallback in case reconnect UI classes change without observable mutations.
        window.setInterval(recoverFromResumeFailureIfNeeded, 2000);
    }

    initializeReconnectRecovery();

    function markdownRoot(root) {
        return root && typeof root.querySelectorAll === "function" ? root : document;
    }

    function toHeadingLevel(element) {
        if (!element || !element.tagName) {
            return 0;
        }

        const match = /^H([1-6])$/i.exec(element.tagName);
        if (!match) {
            return 0;
        }

        const value = Number.parseInt(match[1], 10);
        return Number.isFinite(value) ? value : 0;
    }

    function slugifyHeadingText(text) {
        return String(text || "")
            .toLowerCase()
            .trim()
            .replace(/[\s_]+/g, "-")
            .replace(/[^a-z0-9-]/g, "")
            .replace(/-+/g, "-")
            .replace(/^-|-$/g, "");
    }

    function headingDisplayText(heading) {
        if (!heading) {
            return "";
        }

        const clone = heading.cloneNode(true);
        Array.from(clone.querySelectorAll(":scope > .md-heading-toggle")).forEach(function (toggle) {
            toggle.remove();
        });

        return (clone.textContent || "").trim();
    }

    function ensureHeadingIds(scope) {
        const seen = new Set();
        Array.from(scope.querySelectorAll("h1, h2, h3, h4, h5, h6")).forEach(function (heading) {
            if (!heading.id) {
                const base = slugifyHeadingText(headingDisplayText(heading) || "section") || "section";
                let candidate = base;
                let suffix = 2;
                while (seen.has(candidate) || document.getElementById(candidate)) {
                    candidate = `${base}-${suffix++}`;
                }

                heading.id = candidate;
            }

            seen.add(heading.id);
        });
    }

    function headingStorageKey(pageKey) {
        const key = String(pageKey || "").trim();
        return key ? `${headingCollapseStoragePrefix}${key}` : "";
    }

    function loadCollapsedHeadings(pageKey) {
        const key = headingStorageKey(pageKey);
        if (!key) {
            return new Set();
        }

        try {
            const raw = localStorage.getItem(key);
            if (!raw) {
                return new Set();
            }

            const values = JSON.parse(raw);
            if (!Array.isArray(values)) {
                return new Set();
            }

            return new Set(values.filter(function (value) {
                return typeof value === "string" && value.length > 0;
            }));
        } catch {
            return new Set();
        }
    }

    function saveCollapsedHeadings(pageKey, values) {
        const key = headingStorageKey(pageKey);
        if (!key) {
            return;
        }

        try {
            localStorage.setItem(key, JSON.stringify(Array.from(values || [])));
        } catch {
        }
    }

    function sectionContentElements(heading) {
        const level = toHeadingLevel(heading);
        if (level <= 0) {
            return [];
        }

        const elements = [];
        let sibling = heading.nextElementSibling;
        while (sibling) {
            const siblingLevel = toHeadingLevel(sibling);
            if (siblingLevel > 0 && siblingLevel <= level) {
                break;
            }

            elements.push(sibling);
            sibling = sibling.nextElementSibling;
        }

        return elements;
    }

    function applyHeadingCollapsed(heading, collapsed) {
        if (!heading) {
            return;
        }

        heading.classList.toggle("is-collapsed", collapsed);
        const toggle = heading.querySelector(":scope > .md-heading-toggle");
        if (toggle) {
            toggle.setAttribute("aria-expanded", collapsed ? "false" : "true");
            toggle.setAttribute("title", collapsed ? "Expand section" : "Collapse section");
            toggle.textContent = collapsed ? "+" : "-";
        }

        sectionContentElements(heading).forEach(function (element) {
            element.classList.toggle("md-collapsed-content", collapsed);
        });
    }

    function enhanceCollapsibleHeadings(scope, settings) {
        if (!scope || scope.dataset.headingSectionsEnhanced === "true") {
            return;
        }

        const shouldEnable = settings.enableCollapsibleHeadings === true;
        if (!shouldEnable) {
            return;
        }

        ensureHeadingIds(scope);
        const storagePageKey = settings.collapseStateKey || "";
        const collapsedIds = loadCollapsedHeadings(storagePageKey);
        const headings = Array.from(scope.querySelectorAll("h1[id], h2[id], h3[id], h4[id], h5[id], h6[id]"));
        headings.forEach(function (heading) {
            if (!heading || heading.dataset.headingCollapseEnhanced === "true") {
                return;
            }

            const sectionElements = sectionContentElements(heading);
            if (sectionElements.length === 0) {
                return;
            }

            heading.classList.add("md-collapsible-heading");
            let toggle = heading.querySelector(":scope > .md-heading-toggle");
            if (!toggle) {
                toggle = document.createElement("button");
                toggle.type = "button";
                toggle.className = "md-heading-toggle";
                toggle.setAttribute("aria-label", "Toggle section");
                heading.insertBefore(toggle, heading.firstChild);
            }

            toggle.onclick = function (event) {
                event.preventDefault();
                const nextCollapsed = !heading.classList.contains("is-collapsed");
                applyHeadingCollapsed(heading, nextCollapsed);

                if (nextCollapsed) {
                    collapsedIds.add(heading.id);
                } else {
                    collapsedIds.delete(heading.id);
                }

                saveCollapsedHeadings(storagePageKey, collapsedIds);
            };

            applyHeadingCollapsed(heading, collapsedIds.has(heading.id));
            heading.dataset.headingCollapseEnhanced = "true";
        });

        scope.dataset.headingSectionsEnhanced = "true";
    }

    function normalizeMermaidThemeMode(mode) {
        const normalized = (mode || "").toString().trim().toLowerCase();
        return mermaidThemeModes.has(normalized) ? normalized : "auto";
    }

    function storedMermaidThemeMode() {
        try {
            return normalizeMermaidThemeMode(localStorage.getItem(mermaidThemeStorageKey));
        } catch {
            return "auto";
        }
    }

    function preferredMermaidThemeMode() {
        return window.matchMedia && window.matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light";
    }

    function resolveMermaidTheme() {
        const mode = storedMermaidThemeMode();
        const resolvedMode = mode === "auto" ? preferredMermaidThemeMode() : mode;
        return {
            mode,
            resolvedMode,
            mermaidTheme: resolvedMode === "dark" ? "dark" : "default"
        };
    }

    function configureMermaid() {
        if (!window.mermaid || typeof window.mermaid.initialize !== "function") {
            return false;
        }

        const theme = resolveMermaidTheme().mermaidTheme;
        if (theme !== mermaidTheme) {
            window.mermaid.initialize({ startOnLoad: false, theme: theme, securityLevel: "strict" });
            mermaidTheme = theme;
        }

        return true;
    }

    function restoreMermaidSource(root) {
        const scope = markdownRoot(root);
        Array.from(scope.querySelectorAll(".mermaid-rendered[data-mermaid-code], .mermaid-error[data-mermaid-code]")).forEach(function (element) {
            const source = document.createElement("pre");
            source.className = "mermaid";
            source.textContent = element.dataset.mermaidCode || "";
            element.replaceWith(source);
        });
    }

    function watchMermaidTheme() {
        if (mermaidThemeWatcher || !window.matchMedia) {
            return;
        }

        mermaidThemeWatcher = window.matchMedia("(prefers-color-scheme: dark)");
        const handler = function () {
            if (storedMermaidThemeMode() !== "auto") {
                return;
            }

            mermaidTheme = null;
            restoreMermaidSource(document);
            void window.renderMermaid(document);
        };

        if (typeof mermaidThemeWatcher.addEventListener === "function") {
            mermaidThemeWatcher.addEventListener("change", handler);
        } else if (typeof mermaidThemeWatcher.addListener === "function") {
            mermaidThemeWatcher.addListener(handler);
        }
    }

    window.renderMermaid = async function (root) {
        if (!configureMermaid()) {
            return;
        }

        watchMermaidTheme();
        const scope = markdownRoot(root);
        const theme = resolveMermaidTheme();
        const blocks = Array.from(scope.querySelectorAll("pre.mermaid"));
        for (const block of blocks) {
            const code = block.textContent || "";
            const container = document.createElement("div");
            const id = `mermaid-${++mermaidSequence}`;
            container.id = id;
            container.className = "mermaid-rendered";
            container.dataset.mermaidCode = code;
            container.dataset.mermaidThemeMode = theme.mode;
            container.dataset.mermaidTheme = theme.resolvedMode;
            block.replaceWith(container);

            try {
                const result = await window.mermaid.render(`${id}-svg`, code);
                container.innerHTML = result.svg || "";
                if (typeof result.bindFunctions === "function") {
                    result.bindFunctions(container);
                }
            } catch (error) {
                const overlay = document.createElement("pre");
                overlay.className = "mermaid-error";
                overlay.dataset.mermaidCode = code;
                overlay.textContent = `Mermaid render error:\n${error && error.message ? error.message : error}\n\n${code}`;
                container.replaceWith(overlay);
            }
        }
    };

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
        },

        getMermaidThemeMode: function () {
            return storedMermaidThemeMode();
        },

        setMermaidThemeMode: function (mode) {
            const normalized = normalizeMermaidThemeMode(mode);
            try {
                localStorage.setItem(mermaidThemeStorageKey, normalized);
            } catch {
            }

            mermaidTheme = null;
            restoreMermaidSource(document);
            void window.renderMermaid(document);
            return normalized;
        },

        renderEnhancements: async function (root, options) {
            const scope = markdownRoot(root);
            const settings = options || {};
            ensureHeadingIds(scope);
            enhanceCollapsibleHeadings(scope, settings);
            if (window.Prism) {
                if (typeof window.Prism.highlightAllUnder === "function" && scope !== document) {
                    window.Prism.highlightAllUnder(scope);
                } else if (typeof window.Prism.highlightAll === "function") {
                    window.Prism.highlightAll();
                }
            }

            if (settings.skipMermaid !== true) {
                await window.renderMermaid(scope);
            }
        },

        extractHeadings: function (root) {
            const scope = markdownRoot(root);
            ensureHeadingIds(scope);
            return Array.from(scope.querySelectorAll("h1[id], h2[id], h3[id], h4[id], h5[id], h6[id]"))
                .map(function (heading) {
                    return {
                        id: heading.id,
                        text: headingDisplayText(heading),
                        level: toHeadingLevel(heading)
                    };
                })
                .filter(function (heading) {
                    return heading.id && heading.text && heading.level > 0;
                });
        },

        scrollToHeading: function (root, id) {
            const scope = markdownRoot(root);
            if (!id) {
                return false;
            }

            const selector = `#${CSS.escape(id)}`;
            const heading = scope.querySelector(selector);
            if (!heading) {
                return false;
            }

            Array.from(scope.querySelectorAll("h1.is-collapsed, h2.is-collapsed, h3.is-collapsed, h4.is-collapsed, h5.is-collapsed, h6.is-collapsed"))
                .forEach(function (collapsedHeading) {
                    applyHeadingCollapsed(collapsedHeading, false);
                });

            heading.scrollIntoView({ block: "start", behavior: "smooth" });
            return true;
        }
    };

    window.memorySmith.chat = {
        isNarrowViewport: function (maxWidth) {
            const width = Number.isFinite(maxWidth) ? maxWidth : 640;
            if (window.matchMedia) {
                return window.matchMedia(`(max-width: ${width}px)`).matches;
            }

            return window.innerWidth <= width;
        },

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

        getJsonString: function (key) {
            try {
                return localStorage.getItem(key);
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
        },

        remove: function (key) {
            try {
                localStorage.removeItem(key);
                return true;
            } catch {
                return false;
            }
        }
    };
})();
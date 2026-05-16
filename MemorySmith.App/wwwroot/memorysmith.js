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

            if (textarea.memorySmithComposerHandler) {
                textarea.removeEventListener("keydown", textarea.memorySmithComposerHandler);
            }

            textarea.dataset.sendOnEnter = sendOnEnter ? "true" : "false";
            textarea.memorySmithComposerHandler = function (event) {
                if (event.key === "Enter" && !event.shiftKey && textarea.dataset.sendOnEnter === "true") {
                    event.preventDefault();
                    dotNetRef.invokeMethodAsync("SendFromKeyboard");
                }
            };
            textarea.addEventListener("keydown", textarea.memorySmithComposerHandler);
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
            localStorage.setItem(key, JSON.stringify(value));
        }
    };
})();
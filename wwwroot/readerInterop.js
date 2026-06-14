window.jankReader = {
    initialize(dotNetReference) {
        if (this._initialized) {
            this._dotNetReference = dotNetReference;
            return;
        }

        this._initialized = true;
        this._dotNetReference = dotNetReference;

        document.addEventListener("wheel", (event) => {
            if (!event.ctrlKey || !this._dotNetReference) {
                return;
            }

            event.preventDefault();
            const delta = event.deltaY > 0 ? -10 : 10;
            this._dotNetReference.invokeMethodAsync("ChangeZoomFromShortcutAsync", delta);
        }, { passive: false });

        document.addEventListener("keydown", (event) => {
            if (this._isTypingTarget(event.target) || !this._dotNetReference) {
                return;
            }

            if (event.key === "F11") {
                event.preventDefault();
                this._dotNetReference.invokeMethodAsync("ToggleFullscreenFromShortcutAsync");
                return;
            }

            if (event.key === "Escape") {
                this._dotNetReference.invokeMethodAsync("ExitFullscreenFromShortcutAsync");
                return;
            }

            if (event.ctrlKey || event.metaKey) {
                if (event.key === "+" || event.key === "=") {
                    event.preventDefault();
                    this._dotNetReference.invokeMethodAsync("ChangeZoomFromShortcutAsync", 10);
                    return;
                }

                if (event.key === "-" || event.key === "_") {
                    event.preventDefault();
                    this._dotNetReference.invokeMethodAsync("ChangeZoomFromShortcutAsync", -10);
                    return;
                }

                if (event.key === "0") {
                    event.preventDefault();
                    this._dotNetReference.invokeMethodAsync("ChangeZoomFromShortcutAsync", 0);
                    return;
                }
            }

            const key = event.key.toLowerCase();

            if (key === "v") {
                event.preventDefault();
                this._dotNetReference.invokeMethodAsync("SetReaderModeFromShortcutAsync", "vertical");
                return;
            }

            if (key === "j") {
                event.preventDefault();
                this._dotNetReference.invokeMethodAsync("SetReaderModeFromShortcutAsync", "japanese");
                return;
            }

            if (key === "w") {
                event.preventDefault();
                this._dotNetReference.invokeMethodAsync("SetReaderModeFromShortcutAsync", "western");
                return;
            }

            if (event.key === "ArrowRight") {
                event.preventDefault();
                this._dotNetReference.invokeMethodAsync("NavigateHorizontalFromShortcutAsync", 1);
                return;
            }

            if (event.key === "ArrowLeft") {
                event.preventDefault();
                this._dotNetReference.invokeMethodAsync("NavigateHorizontalFromShortcutAsync", -1);
            }
        });
    },

    _isTypingTarget(target) {
        const tagName = target?.tagName;
        return target?.isContentEditable ||
            tagName === "INPUT" ||
            tagName === "TEXTAREA" ||
            tagName === "SELECT";
    },

    scrollToPage(index) {
        const page = document.getElementById(`page-${index}`);

        if (!page) {
            return;
        }

        page.scrollIntoView({
            behavior: "smooth",
            block: "nearest",
            inline: "center"
        });
    },

    async requestFullscreen(selector) {
        const element = document.querySelector(selector) || document.documentElement;

        if (!document.fullscreenElement && element.requestFullscreen) {
            await element.requestFullscreen();
        }
    },

    async exitFullscreen() {
        if (document.fullscreenElement && document.exitFullscreen) {
            await document.exitFullscreen();
        }
    }
};

// Global keyboard shortcuts, mirroring the prototype's document-level keydown wiring.
window.rtw = {
    registerKeys: function (dotnetRef) {
        if (this._registered) return;
        this._registered = true;
        document.addEventListener('keydown', function (e) {
            const tag = document.activeElement && document.activeElement.tagName;
            const inInput = tag === 'INPUT' || tag === 'TEXTAREA';
            if ((e.metaKey || e.ctrlKey) && e.key.toLowerCase() === 'k') {
                e.preventDefault(); dotnetRef.invokeMethodAsync('TogglePalette'); return;
            }
            if (e.key === 'Escape') { dotnetRef.invokeMethodAsync('CloseOverlays'); return; }
            if (e.key === '?' && !inInput) { e.preventDefault(); dotnetRef.invokeMethodAsync('ToggleHelpJs'); return; }
            if (/^[0-7]$/.test(e.key) && !inInput) { dotnetRef.invokeMethodAsync('GoView', parseInt(e.key, 10)); }
        });
    },
    focus: function (sel) {
        const el = document.querySelector(sel);
        if (el) setTimeout(() => el.focus(), 30);
    },
    scrollEnd: function (id) {
        const el = document.getElementById(id);
        if (el) el.scrollTop = el.scrollHeight;
    }
};

// Scale the fixed 1440x900 prototype canvas (.frame) to fit the native window — see app.css.
// Both sides are in the same CSS-px space, so the Mac Catalyst 77% interface squish cancels out.
function rtwScaleFrame() {
    const s = Math.min(window.innerWidth / 1440, window.innerHeight / 900);
    document.documentElement.style.setProperty('--frame-scale', s);
}
window.addEventListener('resize', rtwScaleFrame);
rtwScaleFrame();

// Global keyboard shortcuts, mirroring the prototype's document-level keydown wiring.
window.rtw = {
    registerKeys: function (dotnetRef) {
        if (this._registered) return;
        this._registered = true;
        document.addEventListener('keydown', function (e) {
            const tag = document.activeElement && document.activeElement.tagName;
            const inInput = tag === 'INPUT' || tag === 'TEXTAREA';
            // Chat composer: Enter sends (Blazor's @onkeydown handles it); stop the textarea inserting a newline.
            if (e.target && e.target.id === 'chatInput' && e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); }
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

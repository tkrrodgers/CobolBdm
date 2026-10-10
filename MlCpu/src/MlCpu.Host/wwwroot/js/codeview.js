// Read-only Monaco editor (the bundle BlazorMonaco ships) for the code viewer: one editor per host element and one model per open
// tab. Switching tabs keeps each tab's scroll position, cursor, selection and folding (its view state), as VS Code does.
window.mlcpuCode = (function () {
    const hosts = {};
    const languages = { java: 'java', json: 'json', md: 'markdown', xml: 'xml', js: 'javascript', ps1: 'powershell', sh: 'shell', yml: 'yaml', yaml: 'yaml' };

    function ready() {
        return new Promise(resolve => typeof monaco !== 'undefined' ? resolve() : require(['vs/editor/editor.main'], () => resolve()));
    }

    function hostFor(hostId) {
        let h = hosts[hostId];
        const el = document.getElementById(hostId);
        if (h && h.el === el) return h;
        if (h) dispose(hostId);
        if (!el) return null;
        const editor = monaco.editor.create(el, {
            model: null, readOnly: true, theme: 'vs-dark', automaticLayout: true, fontSize: 14,
            fontFamily: "Consolas, 'Courier New', monospace", minimap: { enabled: true }, scrollBeyondLastLine: false,
            renderWhitespace: 'selection', stickyScroll: { enabled: true }
        });
        return hosts[hostId] = { el, editor, models: {}, states: {}, current: null };
    }

    function dispose(hostId) {
        const h = hosts[hostId];
        if (!h) return;
        Object.values(h.models).forEach(m => m.dispose());
        h.editor.dispose();
        delete hosts[hostId];
    }

    function show(h, key) {
        if (h.current === key) return;
        if (h.current !== null && h.models[h.current]) h.states[h.current] = h.editor.saveViewState();
        h.editor.setModel(h.models[key]);
        if (h.states[key]) h.editor.restoreViewState(h.states[key]);
        h.current = key;
        h.editor.focus();
    }

    return {
        // Opens (or re-shows) a file; the language comes from the extension (plain text when Monaco has none for it).
        open: async function (hostId, key, text) {
            await ready();
            const h = hostFor(hostId);
            if (!h) return;
            if (!h.models[key]) {
                const ext = key.includes('.') ? key.substring(key.lastIndexOf('.') + 1).toLowerCase() : '';
                h.models[key] = monaco.editor.createModel(text, languages[ext] || 'plaintext');
            }
            show(h, key);
        },
        show: function (hostId, key) { const h = hosts[hostId]; if (h && h.models[key]) show(h, key); },
        close: function (hostId, key) {
            const h = hosts[hostId];
            if (!h || !h.models[key]) return;
            if (h.current === key) { h.editor.setModel(null); h.current = null; }
            h.models[key].dispose();
            delete h.models[key];
            delete h.states[key];
        },
        destroy: dispose
    };
})();

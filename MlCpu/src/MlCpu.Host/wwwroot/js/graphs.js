// Cytoscape rendering for the Graphs page (call graph, paragraph graph, PDG). Elements come from the server as
// { nodes: [{ data: {...} }], edges: [{ data: {...} }] }; a tap on a node calls back OnNodeTap(id).
window.mlcpuGraphs = (function () {
    const palette = {
        job: '#0969da', program: '#1a7f37', subprogram: '#2da44e', online: '#9a6700', unreferenced: '#8c959f',
        utility: '#6e7781', external: '#cf222e', paragraph: '#8250df', dead: '#cf222e', statement: '#57606a'
    };
    const edgeColor = {
        exec: '#0969da', call: '#1a7f37', perform: '#8250df', 'perform-thru': '#8250df', goto: '#bc4c00', alter: '#cf222e',
        fallthrough: '#8c959f', control: '#bc4c00', data: '#1b7c83'
    };
    function style() {
        return [
            { selector: 'node', style: {
                'label': 'data(label)', 'font-size': 11, 'color': '#1f2328', 'text-valign': 'bottom', 'text-margin-y': 4,
                'background-color': ele => palette[ele.data('color') || ele.data('kind')] || '#57606a',
                'width': ele => ele.data('kind') === 'job' ? 34 : ele.data('kind') === 'statement' ? 14 : 24,
                'height': ele => ele.data('kind') === 'job' ? 22 : ele.data('kind') === 'statement' ? 14 : 24,
                'shape': ele => ({ job: 'round-rectangle', utility: 'rectangle', external: 'diamond', paragraph: 'round-rectangle' }[ele.data('kind')] || 'ellipse'),
                'border-width': ele => ele.data('selected') ? 3 : 0, 'border-color': '#fb8500' } },
            { selector: 'edge', style: {
                'width': 1.5, 'curve-style': 'bezier', 'target-arrow-shape': 'triangle', 'arrow-scale': 0.9,
                'line-color': ele => edgeColor[ele.data('kind')] || '#8c959f', 'target-arrow-color': ele => edgeColor[ele.data('kind')] || '#8c959f',
                'line-style': ele => ele.data('kind') === 'fallthrough' ? 'dotted' : ele.data('kind') === 'goto' ? 'dashed' : 'solid',
                'label': 'data(label)', 'font-size': 8, 'color': '#57606a', 'text-rotation': 'autorotate', 'text-background-color': '#fff',
                'text-background-opacity': 0.8, 'text-background-padding': 1 } },
            { selector: 'node:selected', style: { 'border-width': 3, 'border-color': '#fb8500' } }
        ];
    }
    return {
        render: function (id, elements, layout, dotnetRef) {
            const el = document.getElementById(id);
            if (!el || typeof cytoscape === 'undefined') return;
            if (el._cy) el._cy.destroy();
            const cy = cytoscape({
                container: el, elements: elements, style: style(), wheelSensitivity: 0.25, minZoom: 0.1, maxZoom: 3,
                layout: { name: layout || 'breadthfirst', directed: true, padding: 24, spacingFactor: 1.15, animate: false, nodeDimensionsIncludeLabels: true }
            });
            cy.on('tap', 'node', evt => { if (dotnetRef) dotnetRef.invokeMethodAsync('OnNodeTap', evt.target.id()); });
            cy.on('mouseover', 'node', evt => { el.title = evt.target.data('title') || evt.target.data('label') || ''; });
            el._cy = cy;
        },
        fit: function (id) { const el = document.getElementById(id); if (el && el._cy) el._cy.fit(undefined, 24); },
        png: function (id) { const el = document.getElementById(id); return el && el._cy ? el._cy.png({ full: true, scale: 2 }) : null; },
        destroy: function (id) { const el = document.getElementById(id); if (el && el._cy) { el._cy.destroy(); el._cy = null; } }
    };
})();

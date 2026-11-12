window.tagGraph = (() => {
    const states = new WeakMap();
    const SVG_NS = 'http://www.w3.org/2000/svg';

    function element(name, attributes = {}) {
        const node = document.createElementNS(SVG_NS, name);
        for (const [key, value] of Object.entries(attributes)) {
            node.setAttribute(key, String(value));
        }
        return node;
    }

    function clamp(value, min, max) {
        return Math.max(min, Math.min(max, value));
    }

    function layout(nodes, edges, width, height) {
        const centerX = width / 2;
        const centerY = height / 2;
        const count = nodes.length;
        const radius = Math.max(35, Math.min(width, height) * 0.34);

        nodes.forEach((node, index) => {
            const angle = 2 * Math.PI * index / Math.max(1, count);
            node.x = centerX + Math.cos(angle) * radius * (0.72 + (index % 4) * 0.07);
            node.y = centerY + Math.sin(angle) * radius * (0.72 + (index % 3) * 0.08);
            node.vx = 0;
            node.vy = 0;
            node.r = clamp(6 + Math.sqrt(node.fileCount) * 1.5, 8, 25);
        });

        const byId = new Map(nodes.map(node => [node.id, node]));
        const links = edges.map(edge => ({ ...edge, a: byId.get(edge.source), b: byId.get(edge.target) }))
            .filter(edge => edge.a && edge.b);

        // A small, bounded force simulation keeps the graph responsive without a runtime dependency.
        for (let step = 0; step < 125; step++) {
            const cooling = 1 - step / 145;
            for (let i = 0; i < count; i++) {
                const a = nodes[i];
                for (let j = i + 1; j < count; j++) {
                    const b = nodes[j];
                    let dx = a.x - b.x;
                    let dy = a.y - b.y;
                    let distanceSquared = dx * dx + dy * dy;
                    if (distanceSquared < 1) {
                        dx = (i + 1) * 0.13;
                        dy = (j + 1) * 0.11;
                        distanceSquared = dx * dx + dy * dy;
                    }
                    const distance = Math.sqrt(distanceSquared);
                    const force = Math.min(1.8, 700 / distanceSquared) * cooling;
                    const fx = dx / distance * force;
                    const fy = dy / distance * force;
                    a.vx += fx;
                    a.vy += fy;
                    b.vx -= fx;
                    b.vy -= fy;

                    const minimum = a.r + b.r + 12;
                    if (distance < minimum) {
                        const push = (minimum - distance) * 0.025 * cooling;
                        a.vx += dx / distance * push;
                        a.vy += dy / distance * push;
                        b.vx -= dx / distance * push;
                        b.vy -= dy / distance * push;
                    }
                }
            }

            for (const edge of links) {
                const dx = edge.b.x - edge.a.x;
                const dy = edge.b.y - edge.a.y;
                const distance = Math.max(1, Math.sqrt(dx * dx + dy * dy));
                const desired = edge.isHierarchy ? 90 : Math.max(65, 105 - Math.min(edge.sharedFileCount, 20));
                const force = (distance - desired) * (edge.isHierarchy ? 0.009 : 0.006) * cooling;
                const fx = dx / distance * force;
                const fy = dy / distance * force;
                edge.a.vx += fx;
                edge.a.vy += fy;
                edge.b.vx -= fx;
                edge.b.vy -= fy;
            }

            for (const node of nodes) {
                node.vx += (centerX - node.x) * 0.0017;
                node.vy += (centerY - node.y) * 0.0017;
                node.vx *= 0.76;
                node.vy *= 0.76;
                node.x = clamp(node.x + node.vx, node.r + 12, width - node.r - 12);
                node.y = clamp(node.y + node.vy, node.r + 12, height - node.r - 12);
            }
        }
    }

    function render(svg, data, minSharedFiles, search, dotnetRef) {
        if (!svg) return;
        const bounds = svg.getBoundingClientRect();
        const width = Math.max(360, Math.round(bounds.width));
        const height = Math.max(320, Math.round(bounds.height));
        const query = (search || '').trim().toLocaleLowerCase();
        const threshold = Number(minSharedFiles) || 1;
        const allowedEdges = data.edges.filter(edge => edge.isHierarchy || edge.sharedFileCount >= threshold);
        const byId = new Map(data.nodes.map(node => [node.id, node]));
        let visibleNodes = data.nodes;

        if (query) {
            const matches = new Set(data.nodes.filter(node => node.name.toLocaleLowerCase().includes(query)).map(node => node.id));
            const keep = new Set(matches);
            for (const edge of allowedEdges) {
                if (matches.has(edge.source)) keep.add(edge.target);
                if (matches.has(edge.target)) keep.add(edge.source);
            }
            visibleNodes = data.nodes.filter(node => keep.has(node.id));
        }

        const visibleIds = new Set(visibleNodes.map(node => node.id));
        let visibleEdges = allowedEdges.filter(edge => visibleIds.has(edge.source) && visibleIds.has(edge.target));
        visibleEdges.sort((a, b) => Number(b.isHierarchy) - Number(a.isHierarchy) || b.sharedFileCount - a.sharedFileCount);
        visibleEdges = visibleEdges.slice(0, 700);
        const linkedIds = new Set();
        for (const edge of visibleEdges) {
            linkedIds.add(edge.source);
            linkedIds.add(edge.target);
        }
        visibleNodes = visibleNodes.filter(node => linkedIds.has(node.id) || (query && node.name.toLocaleLowerCase().includes(query)));

        const previous = states.get(svg);
        svg.replaceChildren();
        svg.setAttribute('viewBox', `0 0 ${width} ${height}`);

        if (!visibleNodes.length) {
            const message = element('text', { x: width / 2, y: height / 2, 'text-anchor': 'middle', class: 'tag-graph-empty-label' });
            message.textContent = query ? 'No matching tags or relationships.' : 'No relationships match this threshold.';
            svg.appendChild(message);
            if (previous?.resizeObserver) previous.resizeObserver.disconnect();
            states.set(svg, {});
            return;
        }

        layout(visibleNodes, visibleEdges, width, height);
        const defs = element('defs');
        const marker = element('marker', { id: 'tag-graph-hierarchy-arrow', viewBox: '0 0 10 10', refX: 8, refY: 5, markerWidth: 6, markerHeight: 6, orient: 'auto-start-reverse' });
        marker.appendChild(element('path', { d: 'M 0 0 L 10 5 L 0 10 z', fill: 'var(--primary-color)' }));
        defs.appendChild(marker);
        svg.appendChild(defs);
        const viewport = element('g', { class: 'tag-graph-viewport' });
        svg.appendChild(viewport);

        for (const edge of visibleEdges) {
            const source = byId.get(edge.source);
            const target = byId.get(edge.target);
            if (!source || !target) continue;
            const line = element('line', {
                x1: source.x,
                y1: source.y,
                x2: target.x,
                y2: target.y,
                class: edge.isHierarchy ? 'tag-graph-edge hierarchy' : 'tag-graph-edge',
                'stroke-width': edge.isHierarchy ? 2 : clamp(0.8 + Math.log2(edge.sharedFileCount + 1) * 0.7, 1, 4),
                ...(edge.isHierarchy ? { 'marker-end': 'url(#tag-graph-hierarchy-arrow)' } : {})
            });
            const title = element('title');
            title.textContent = edge.isHierarchy
                ? `${source.name} → ${target.name} · hierarchy${edge.sharedFileCount ? ` · ${edge.sharedFileCount} shared files` : ''}`
                : `${source.name} and ${target.name} · ${edge.sharedFileCount} shared ${edge.sharedFileCount === 1 ? 'file' : 'files'}`;
            line._sourceNode = source;
            line._targetNode = target;
            line.appendChild(title);
            viewport.appendChild(line);
        }

        const groupFor = new Map();
        for (const node of visibleNodes) {
            const isMatch = query && node.name.toLocaleLowerCase().includes(query);
            const group = element('g', { class: `tag-graph-node${isMatch ? ' focused' : ''}`, tabindex: '0', role: 'button', 'aria-label': `${node.name}, ${node.fileCount} files` });
            group.setAttribute('transform', `translate(${node.x} ${node.y})`);
            const circle = element('circle', { r: node.r, fill: node.color || '#757575' });
            const title = element('title');
            title.textContent = `${node.name} · ${node.fileCount} ${node.fileCount === 1 ? 'file' : 'files'} — click to browse`;
            circle.appendChild(title);
            group.appendChild(circle);
            const label = element('text', { x: node.r + 5, y: 4, class: 'tag-graph-node-label' });
            label.textContent = `${node.emoji ? `${node.emoji} ` : ''}${node.name}`;
            group.appendChild(label);
            group.addEventListener('click', event => {
                if (!group._dragged && dotnetRef) dotnetRef.invokeMethodAsync('BrowseTag', node.id);
                event.stopPropagation();
            });
            group.addEventListener('keydown', event => {
                if (event.key === 'Enter' || event.key === ' ') {
                    event.preventDefault();
                    if (dotnetRef) dotnetRef.invokeMethodAsync('BrowseTag', node.id);
                }
            });
            group.addEventListener('pointerdown', event => {
                event.stopPropagation();
                if (event.button !== 0) return;
                group._dragged = false;
                group.setPointerCapture(event.pointerId);
                const startX = event.clientX;
                const startY = event.clientY;
                const startNodeX = node.x;
                const startNodeY = node.y;
                const onMove = moveEvent => {
                    const dx = (moveEvent.clientX - startX) * width / Math.max(1, svg.clientWidth) / scale;
                    const dy = (moveEvent.clientY - startY) * height / Math.max(1, svg.clientHeight) / scale;
                    if (Math.abs(dx) + Math.abs(dy) > 3) group._dragged = true;
                    node.x = clamp(startNodeX + dx, node.r, width - node.r);
                    node.y = clamp(startNodeY + dy, node.r, height - node.r);
                    group.setAttribute('transform', `translate(${node.x} ${node.y})`);
                    for (const line of viewport.querySelectorAll('line')) {
                        if (line._sourceNode === node) {
                            line.setAttribute('x1', node.x);
                            line.setAttribute('y1', node.y);
                        }
                        if (line._targetNode === node) {
                            line.setAttribute('x2', node.x);
                            line.setAttribute('y2', node.y);
                        }
                    }
                };
                const onUp = () => {
                    group.removeEventListener('pointermove', onMove);
                    group.removeEventListener('pointerup', onUp);
                    group.removeEventListener('pointercancel', onUp);
                };
                group.addEventListener('pointermove', onMove);
                group.addEventListener('pointerup', onUp);
                group.addEventListener('pointercancel', onUp);
            });
            groupFor.set(node.id, group);
            viewport.appendChild(group);
        }

        let scale = 1;
        let offsetX = 0;
        let offsetY = 0;
        const applyTransform = () => viewport.setAttribute('transform', `translate(${offsetX} ${offsetY}) scale(${scale})`);
        let pan = null;
        svg.addEventListener('pointerdown', event => {
            if (event.target !== svg) return;
            pan = { x: event.clientX, y: event.clientY, ox: offsetX, oy: offsetY };
            svg.setPointerCapture(event.pointerId);
        });
        svg.addEventListener('pointermove', event => {
            if (!pan) return;
            offsetX = pan.ox + (event.clientX - pan.x);
            offsetY = pan.oy + (event.clientY - pan.y);
            applyTransform();
        });
        svg.addEventListener('pointerup', () => pan = null);
        svg.addEventListener('pointercancel', () => pan = null);
        svg.addEventListener('wheel', event => {
            event.preventDefault();
            const rect = svg.getBoundingClientRect();
            const px = event.clientX - rect.left;
            const py = event.clientY - rect.top;
            const next = clamp(scale * (event.deltaY < 0 ? 1.12 : 0.89), 0.45, 3.5);
            offsetX = px - (px - offsetX) * next / scale;
            offsetY = py - (py - offsetY) * next / scale;
            scale = next;
            applyTransform();
        }, { passive: false });

        const resizeObserver = previous?.resizeObserver ?? new ResizeObserver(entries => {
            const state = states.get(svg);
            const rect = entries[0]?.contentRect;
            if (!state || !rect?.width || !rect?.height) return;
            if (Math.round(rect.width) !== state.width || Math.round(rect.height) !== state.height) {
                render(svg, state.data, state.minSharedFiles, state.search, state.dotnetRef);
            }
        });
        states.set(svg, { resizeObserver, width, height, data, minSharedFiles, search, dotnetRef });
        if (!previous?.resizeObserver) resizeObserver.observe(svg);
    }

    function destroy(svg) {
        const state = states.get(svg);
        if (state?.resizeObserver) state.resizeObserver.disconnect();
        states.delete(svg);
    }

    return { render, destroy };
})();

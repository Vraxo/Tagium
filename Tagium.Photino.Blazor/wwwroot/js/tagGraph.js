window.tagGraph = (() => {
    const states = new WeakMap();

    function clamp(value, min, max) {
        return Math.max(min, Math.min(max, value));
    }

    function measure(state) {
        const rect = state.canvas.getBoundingClientRect();
        if (!rect.width || !rect.height) return false;
        const dpr = Math.min(window.devicePixelRatio || 1, 2);
        const width = Math.round(rect.width);
        const height = Math.round(rect.height);
        if (state.width !== width || state.height !== height || state.dpr !== dpr) {
            state.width = width;
            state.height = height;
            state.dpr = dpr;
            state.canvas.width = Math.round(width * dpr);
            state.canvas.height = Math.round(height * dpr);
            state.ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
            state.alpha = Math.max(state.alpha, 0.3);
            return true;
        }
        return false;
    }

    function layoutInitial(state) {
        const { nodes, width, height } = state;
        const centerX = width / 2;
        const centerY = height / 2;
        const radius = Math.max(35, Math.min(width, height) * 0.32);
        nodes.forEach((node, index) => {
            const angle = 2.399963229728653 * index;
            const spread = radius * Math.sqrt((index + 1) / Math.max(nodes.length, 1));
            node.x = centerX + Math.cos(angle) * spread;
            node.y = centerY + Math.sin(angle) * spread;
            node.vx = 0;
            node.vy = 0;
            node.r = clamp(6 + Math.sqrt(node.fileCount) * 1.5, 8, 25);
            node.pinned = false;
        });
    }

    function makeColors() {
        const style = getComputedStyle(document.documentElement);
        return {
            text: style.getPropertyValue('--text-color').trim() || '#dddddd',
            muted: style.getPropertyValue('--text-muted').trim() || '#888888',
            surface: style.getPropertyValue('--surface-bg').trim() || '#242424',
            primary: style.getPropertyValue('--primary-color').trim() || '#4b8bf4'
        };
    }

    function stepSimulation(state) {
        const nodes = state.nodes;
        const alpha = state.alpha;
        const { width, height, forces } = state;
        const repulsion = forces.repulsion * 1200;
        const linkStrength = forces.linkStrength * 0.012;

        for (let i = 0; i < nodes.length; i++) {
            const a = nodes[i];
            for (let j = i + 1; j < nodes.length; j++) {
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
                const repel = Math.min(2.4, repulsion / distanceSquared) * alpha;
                const overlap = Math.max(0, a.r + b.r + 10 - distance) * 0.035 * alpha;
                const force = repel + overlap;
                const fx = dx / distance * force;
                const fy = dy / distance * force;
                a.vx += fx;
                a.vy += fy;
                b.vx -= fx;
                b.vy -= fy;
            }
        }

        for (const edge of state.edges) {
            const dx = edge.target.x - edge.source.x;
            const dy = edge.target.y - edge.source.y;
            const distance = Math.max(1, Math.sqrt(dx * dx + dy * dy));
            const desired = edge.isHierarchy ? forces.linkDistance * 0.9 : forces.linkDistance;
            const spring = (distance - desired) * linkStrength * (edge.isHierarchy ? 1.25 : 1) * alpha;
            const fx = dx / distance * spring;
            const fy = dy / distance * spring;
            edge.source.vx += fx;
            edge.source.vy += fy;
            edge.target.vx -= fx;
            edge.target.vy -= fy;
        }

        for (const node of nodes) {
            if (node.pinned) continue;
            node.vx += (width / 2 - node.x) * 0.0015 * alpha;
            node.vy += (height / 2 - node.y) * 0.0015 * alpha;
            node.vx *= 0.82;
            node.vy *= 0.82;
            node.x = clamp(node.x + node.vx, node.r + 8, width - node.r - 8);
            node.y = clamp(node.y + node.vy, node.r + 8, height - node.r - 8);
        }

        state.alpha *= 0.985;
    }

    function draw(state) {
        const { ctx, width, height, dpr, scale, offsetX, offsetY, colors } = state;
        if (!width || !height) return;
        ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
        ctx.clearRect(0, 0, width, height);
        if (state.emptyMessage) {
            ctx.fillStyle = colors.muted;
            ctx.font = '14px system-ui, sans-serif';
            ctx.textAlign = 'center';
            ctx.textBaseline = 'middle';
            ctx.fillText(state.emptyMessage, width / 2, height / 2, width - 32);
            return;
        }
        ctx.save();
        ctx.translate(offsetX, offsetY);
        ctx.scale(scale, scale);

        for (const edge of state.edges) {
            const { source, target } = edge;
            const dx = target.x - source.x;
            const dy = target.y - source.y;
            const length = Math.max(1, Math.sqrt(dx * dx + dy * dy));
            const ux = dx / length;
            const uy = dy / length;
            const endX = target.x - ux * (target.r + (edge.isHierarchy ? 7 : 1));
            const endY = target.y - uy * (target.r + (edge.isHierarchy ? 7 : 1));
            ctx.beginPath();
            ctx.moveTo(source.x + ux * source.r, source.y + uy * source.r);
            ctx.lineTo(endX, endY);
            ctx.strokeStyle = edge.isHierarchy ? colors.primary : colors.muted;
            ctx.globalAlpha = edge.isHierarchy ? 0.9 : 0.22 + Math.min(edge.sharedFileCount, 10) * 0.035;
            ctx.lineWidth = edge.isHierarchy ? 1.8 / scale : clamp(0.8 + Math.log2(edge.sharedFileCount + 1) * 0.65, 1, 3.5) / scale;
            if (edge.isHierarchy) ctx.setLineDash([5 / scale, 4 / scale]);
            else ctx.setLineDash([]);
            ctx.stroke();
            ctx.setLineDash([]);

            if (edge.isHierarchy) {
                const size = 7 / scale;
                const angle = Math.atan2(dy, dx);
                ctx.beginPath();
                ctx.moveTo(endX, endY);
                ctx.lineTo(endX - size * Math.cos(angle - Math.PI / 6), endY - size * Math.sin(angle - Math.PI / 6));
                ctx.lineTo(endX - size * Math.cos(angle + Math.PI / 6), endY - size * Math.sin(angle + Math.PI / 6));
                ctx.closePath();
                ctx.fillStyle = colors.primary;
                ctx.fill();
            }
        }
        ctx.globalAlpha = 1;

        for (const node of state.nodes) {
            const isHovered = state.hovered === node;
            const isFocused = node.matchesSearch;
            ctx.beginPath();
            ctx.arc(node.x, node.y, node.r, 0, Math.PI * 2);
            ctx.fillStyle = node.color || '#757575';
            ctx.fill();
            ctx.lineWidth = (isHovered || isFocused ? 3 : 1.5) / scale;
            ctx.strokeStyle = isHovered || isFocused ? colors.primary : colors.surface;
            ctx.stroke();

            if (scale >= 0.65 || isHovered || isFocused) {
                const label = `${node.emoji ? `${node.emoji} ` : ''}${node.name}`;
                const fontSize = clamp(12 * scale, 10, 24);
                ctx.font = `${fontSize / scale}px system-ui, sans-serif`;
                ctx.textBaseline = 'middle';
                ctx.fillStyle = colors.text;
                ctx.fillText(label, node.x + node.r + 5 / scale, node.y, 220 / scale);
            }
        }

        ctx.restore();
    }

    function animate(state) {
        if (state.frame) return;
        const frame = () => {
            state.frame = 0;
            if (state.disposed) return;
            measure(state);
            if (state.alpha > 0.015) {
                stepSimulation(state);
                stepSimulation(state);
                draw(state);
                state.frame = requestAnimationFrame(frame);
            } else {
                state.alpha = 0;
                draw(state);
            }
        };
        state.frame = requestAnimationFrame(frame);
    }

    function requestDraw(state) {
        if (state.frame) return;
        state.frame = requestAnimationFrame(() => {
            state.frame = 0;
            if (!state.disposed) {
                measure(state);
                draw(state);
            }
        });
    }

    function reheat(state, amount = 0.85) {
        state.alpha = Math.max(state.alpha, amount);
        animate(state);
    }

    function hitTest(state, clientX, clientY) {
        const rect = state.canvas.getBoundingClientRect();
        const x = (clientX - rect.left - state.offsetX) / state.scale;
        const y = (clientY - rect.top - state.offsetY) / state.scale;
        let closest = null;
        let closestDistance = Infinity;
        for (const node of state.nodes) {
            const dx = x - node.x;
            const dy = y - node.y;
            const distanceSquared = dx * dx + dy * dy;
            const hitRadius = Math.max(node.r + 6, node.r * 1.35);
            if (distanceSquared <= hitRadius * hitRadius && distanceSquared < closestDistance) {
                closest = node;
                closestDistance = distanceSquared;
            }
        }
        return closest;
    }

    function installInteractions(state) {
        const canvas = state.canvas;
        const point = event => {
            const rect = canvas.getBoundingClientRect();
            return { x: event.clientX - rect.left, y: event.clientY - rect.top };
        };

        state.onPointerDown = event => {
            if (event.button !== 0) return;
            const position = point(event);
            const node = hitTest(state, event.clientX, event.clientY);
            canvas.setPointerCapture(event.pointerId);
            state.gesture = node
                ? { type: 'node', node, startX: position.x, startY: position.y, originX: node.x, originY: node.y, moved: false }
                : { type: 'pan', startX: position.x, startY: position.y, originX: state.offsetX, originY: state.offsetY, moved: false };
            if (node) {
                node.pinned = true;
                state.hovered = node;
            }
        };

        state.onPointerMove = event => {
            if (state.gesture) {
                const position = point(event);
                const gesture = state.gesture;
                const dx = (position.x - gesture.startX) / state.scale;
                const dy = (position.y - gesture.startY) / state.scale;
                if (Math.abs(dx) + Math.abs(dy) > 2) gesture.moved = true;
                if (gesture.type === 'node') {
                    gesture.node.x = clamp(gesture.originX + dx, gesture.node.r, state.width - gesture.node.r);
                    gesture.node.y = clamp(gesture.originY + dy, gesture.node.r, state.height - gesture.node.r);
                    gesture.node.vx = gesture.node.vy = 0;
                } else {
                    state.offsetX = gesture.originX + position.x - gesture.startX;
                    state.offsetY = gesture.originY + position.y - gesture.startY;
                }
                requestDraw(state);
                return;
            }
            const hovered = hitTest(state, event.clientX, event.clientY);
            if (hovered !== state.hovered) {
                state.hovered = hovered;
                canvas.style.cursor = hovered ? 'pointer' : 'grab';
                canvas.title = hovered ? `${hovered.name} · ${hovered.fileCount} files — click to browse` : 'Drag to pan; scroll to zoom';
                requestDraw(state);
            }
        };

        state.onPointerUp = event => {
            const gesture = state.gesture;
            if (!gesture) return;
            state.gesture = null;
            if (gesture.type === 'node') {
                gesture.node.pinned = false;
                if (!gesture.moved && state.dotnetRef) {
                    state.dotnetRef.invokeMethodAsync('BrowseTag', gesture.node.id);
                } else {
                    reheat(state, 0.45);
                }
            }
        };

        state.onWheel = event => {
            event.preventDefault();
            const position = point(event);
            const nextScale = clamp(state.scale * Math.exp(-event.deltaY * 0.0013), 0.25, 5);
            const worldX = (position.x - state.offsetX) / state.scale;
            const worldY = (position.y - state.offsetY) / state.scale;
            state.scale = nextScale;
            state.offsetX = position.x - worldX * nextScale;
            state.offsetY = position.y - worldY * nextScale;
            requestDraw(state);
        };

        state.onKeyDown = event => {
            if (event.key === '+' || event.key === '=') {
                state.scale = clamp(state.scale * 1.15, 0.25, 5);
                requestDraw(state);
                event.preventDefault();
            } else if (event.key === '-') {
                state.scale = clamp(state.scale / 1.15, 0.25, 5);
                requestDraw(state);
                event.preventDefault();
            } else if (event.key === 'Enter' && state.hovered && state.dotnetRef) {
                state.dotnetRef.invokeMethodAsync('BrowseTag', state.hovered.id);
            } else if (event.key.startsWith('Arrow')) {
                const step = 28;
                state.offsetX += event.key === 'ArrowLeft' ? step : event.key === 'ArrowRight' ? -step : 0;
                state.offsetY += event.key === 'ArrowUp' ? step : event.key === 'ArrowDown' ? -step : 0;
                requestDraw(state);
                event.preventDefault();
            }
        };

        canvas.addEventListener('pointerdown', state.onPointerDown);
        canvas.addEventListener('pointermove', state.onPointerMove);
        canvas.addEventListener('pointerup', state.onPointerUp);
        canvas.addEventListener('pointercancel', state.onPointerUp);
        canvas.addEventListener('wheel', state.onWheel, { passive: false });
        canvas.addEventListener('keydown', state.onKeyDown);
    }

    function destroyState(state) {
        if (!state) return;
        state.disposed = true;
        if (state.frame) cancelAnimationFrame(state.frame);
        if (state.resizeObserver) state.resizeObserver.disconnect();
        if (state.themeObserver) state.themeObserver.disconnect();
        const canvas = state.canvas;
        canvas.removeEventListener('pointerdown', state.onPointerDown);
        canvas.removeEventListener('pointermove', state.onPointerMove);
        canvas.removeEventListener('pointerup', state.onPointerUp);
        canvas.removeEventListener('pointercancel', state.onPointerUp);
        canvas.removeEventListener('wheel', state.onWheel);
        canvas.removeEventListener('keydown', state.onKeyDown);
    }

    function render(canvas, data, minSharedFiles, search, forces, dotnetRef) {
        if (!canvas) return;
        destroyState(states.get(canvas));

        const query = (search || '').trim().toLocaleLowerCase();
        const threshold = Number(minSharedFiles) || 1;
        const sourceNodes = data.nodes;
        const allowedEdges = data.edges.filter(edge => edge.isHierarchy || edge.sharedFileCount >= threshold);
        let visibleIds = new Set(sourceNodes.map(node => node.id));

        if (query) {
            const matches = new Set(sourceNodes.filter(node => node.name.toLocaleLowerCase().includes(query)).map(node => node.id));
            const keep = new Set(matches);
            for (const edge of allowedEdges) {
                if (matches.has(edge.source)) keep.add(edge.target);
                if (matches.has(edge.target)) keep.add(edge.source);
            }
            visibleIds = keep;
        }

        let edges = allowedEdges.filter(edge => visibleIds.has(edge.source) && visibleIds.has(edge.target));
        edges.sort((a, b) => Number(b.isHierarchy) - Number(a.isHierarchy) || b.sharedFileCount - a.sharedFileCount);
        edges = edges.slice(0, 500);
        const linkedIds = new Set();
        for (const edge of edges) {
            linkedIds.add(edge.source);
            linkedIds.add(edge.target);
        }
        const nodes = sourceNodes
            .filter(node => visibleIds.has(node.id) && (linkedIds.has(node.id) || (query && node.name.toLocaleLowerCase().includes(query))))
            .map(node => ({ ...node, matchesSearch: Boolean(query && node.name.toLocaleLowerCase().includes(query)) }));
        const nodesById = new Map(nodes.map(node => [node.id, node]));
        edges = edges
            .map(edge => ({ ...edge, source: nodesById.get(edge.source), target: nodesById.get(edge.target) }))
            .filter(edge => edge.source && edge.target);

        const state = {
            canvas,
            ctx: canvas.getContext('2d', { alpha: true, desynchronized: true }),
            width: 0,
            height: 0,
            dpr: 1,
            nodes,
            edges,
            scale: 1,
            offsetX: 0,
            offsetY: 0,
            alpha: 1,
            frame: 0,
            forces: {
                repulsion: Number(forces?.repulsion ?? 0.45),
                linkStrength: Number(forces?.linkStrength ?? 0.3),
                linkDistance: Number(forces?.linkDistance ?? 105)
            },
            colors: makeColors(),
            hovered: null,
            gesture: null,
            dotnetRef,
            disposed: false
        };
        states.set(canvas, state);
        measure(state);
        layoutInitial(state);

        if (!nodes.length) {
            state.emptyMessage = query ? 'No matching tags or relationships.' : 'No relationships match this threshold.';
            state.alpha = 0;
        }

        state.resizeObserver = new ResizeObserver(() => {
            if (measure(state)) {
                state.alpha = Math.max(state.alpha, 0.3);
                animate(state);
            } else {
                requestDraw(state);
            }
        });
        state.resizeObserver.observe(canvas);
        state.themeObserver = new MutationObserver(() => {
            state.colors = makeColors();
            requestDraw(state);
        });
        state.themeObserver.observe(document.documentElement, { attributes: true, attributeFilter: ['style'] });
        installInteractions(state);
        animate(state);
    }

    function setForces(canvas, forces) {
        const state = states.get(canvas);
        if (!state) return;
        state.forces = {
            repulsion: Number(forces.repulsion),
            linkStrength: Number(forces.linkStrength),
            linkDistance: Number(forces.linkDistance)
        };
        reheat(state, 0.9);
    }

    function destroy(canvas) {
        destroyState(states.get(canvas));
        states.delete(canvas);
    }

    return { render, setForces, destroy };
})();

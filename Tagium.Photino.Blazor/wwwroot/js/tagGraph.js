window.tagGraph = (() => {
    const states = new WeakMap();
    const { d3Force: d3, PIXI } = window.tagGraphVendors;

    function clamp(value, min, max) {
        return Math.max(min, Math.min(max, value));
    }

    function colorToNumber(value, fallback = 0x757575) {
        if (typeof value !== 'string') return fallback;
        const parsed = Number.parseInt(value.trim().replace(/^#/, ''), 16);
        return Number.isFinite(parsed) ? parsed : fallback;
    }

    function getThemeColors() {
        const style = getComputedStyle(document.documentElement);
        return {
            text: style.getPropertyValue('--text-color').trim() || '#dddddd',
            muted: style.getPropertyValue('--text-muted').trim() || '#888888',
            surface: style.getPropertyValue('--surface-bg').trim() || '#242424',
            primary: style.getPropertyValue('--primary-color').trim() || '#4b8bf4'
        };
    }

    function makeNodeLabel(node, state) {
        const label = new PIXI.Text(
            node.name,
            {
                fontFamily: 'Segoe UI, sans-serif',
                fontSize: 12,
                fill: colorToNumber(state.colors.text, 0xdddddd),
                stroke: colorToNumber(state.colors.surface, 0x242424),
                strokeThickness: 3,
                lineJoin: 'round'
            }
        );
        label.anchor.set(0, 0.5);
        label.resolution = Math.min(window.devicePixelRatio || 1, 2);
        return label;
    }

    function makeNodeEmoji(node) {
        const emoji = new PIXI.Text(node.emoji ?? '', {
            fontFamily: 'Segoe UI Emoji, Apple Color Emoji, sans-serif',
            fontSize: 24,
            fill: 0xffffff
        });
        emoji.anchor.set(0.5);
        emoji.resolution = Math.min(window.devicePixelRatio || 1, 2);
        return emoji;
    }

    function drawDashedLink(graphics, x1, y1, x2, y2, dashLength) {
        const dx = x2 - x1;
        const dy = y2 - y1;
        const length = Math.sqrt(dx * dx + dy * dy);
        if (length < 1) return;
        const ux = dx / length;
        const uy = dy / length;
        for (let start = 0; start < length; start += dashLength * 2) {
            const end = Math.min(start + dashLength, length);
            graphics.moveTo(x1 + ux * start, y1 + uy * start);
            graphics.lineTo(x1 + ux * end, y1 + uy * end);
        }
    }

    function drawScene(state) {
        if (state.disposed) return;
        const { linksGraphics, nodesGraphics, emojiBackingGraphics, emojiContainer, world, app, nodes, links, zoom } = state;
        linksGraphics.clear();

        for (const edge of links) {
            const source = edge.source;
            const target = edge.target;
            const dx = target.x - source.x;
            const dy = target.y - source.y;
            const length = Math.max(1, Math.sqrt(dx * dx + dy * dy));
            const ux = dx / length;
            const uy = dy / length;
            const tipX = target.x - ux * (target.r + (edge.isHierarchy ? 7 : 1));
            const tipY = target.y - uy * (target.r + (edge.isHierarchy ? 7 : 1));
            const startX = source.x + ux * source.r;
            const startY = source.y + uy * source.r;
            const stroke = edge.isHierarchy ? colorToNumber(state.colors.primary, 0x4b8bf4) : colorToNumber(state.colors.muted, 0x888888);
            const alpha = edge.isHierarchy ? 0.9 : 0.2 + Math.min(edge.sharedFileCount, 12) * 0.035;
            const thickness = state.settings.linkThickness;
            linksGraphics.lineStyle(thickness, stroke, alpha);

            if (edge.isHierarchy) {
                drawDashedLink(linksGraphics, startX, startY, tipX, tipY, 5);
                if (state.settings.showArrows) {
                    const angle = Math.atan2(dy, dx);
                    const size = 7;
                    const baseX = tipX - size * Math.cos(angle);
                    const baseY = tipY - size * Math.sin(angle);
                    linksGraphics.beginFill(stroke, alpha);
                    linksGraphics.moveTo(tipX, tipY);
                    linksGraphics.lineTo(baseX + size * 0.55 * Math.sin(angle), baseY - size * 0.55 * Math.cos(angle));
                    linksGraphics.lineTo(baseX - size * 0.55 * Math.sin(angle), baseY + size * 0.55 * Math.cos(angle));
                    linksGraphics.closePath();
                    linksGraphics.endFill();
                }
            } else {
                linksGraphics.moveTo(startX, startY);
                linksGraphics.lineTo(tipX, tipY);
            }
        }

        nodesGraphics.clear();
        emojiBackingGraphics.clear();
        const primary = colorToNumber(state.colors.primary, 0x4b8bf4);
        const surface = colorToNumber(state.colors.surface, 0x242424);
        for (const node of nodes) {
            const highlighted = node === state.hovered || node.matchesSearch;
            nodesGraphics.lineStyle(highlighted ? 3 : 1.5, highlighted ? primary : surface, 1);
            nodesGraphics.beginFill(colorToNumber(node.color));
            nodesGraphics.drawCircle(node.x, node.y, node.r);
            nodesGraphics.endFill();

            const label = state.labels.get(node.id);
            label.position.set(node.x + node.r + 5 / zoom, node.y);
            label.scale.set(1 / zoom);
            const fadeStart = 0.2 + state.settings.textFadeThreshold * 1.2;
            label.alpha = highlighted ? 1 : clamp((zoom - fadeStart) / 0.4, 0, 1);
            label.visible = label.alpha > 0.02;
        }

        for (const node of nodes) {
            const emoji = state.emojis.get(node.id);
            if (!emoji) continue;
            const showEmoji = node.r >= 12;
            emoji.visible = showEmoji;
            if (!showEmoji) continue;

            const badgeRadius = node.r * 0.62;
            emojiBackingGraphics.lineStyle(1, surface, 0.35);
            emojiBackingGraphics.beginFill(surface, 0.42);
            emojiBackingGraphics.drawCircle(node.x, node.y, badgeRadius);
            emojiBackingGraphics.endFill();
            emoji.position.set(node.x, node.y);
            emoji.scale.set(clamp(node.r * 1.05, 12, 30) / 24);
        }

        if (state.emptyLabel) {
            state.emptyLabel.position.set(state.width / 2, state.height / 2);
            state.emptyLabel.scale.set(1 / zoom);
        }
        world.position.set(state.offsetX, state.offsetY);
        world.scale.set(zoom);
        app.render();
    }

    function requestSceneDraw(state) {
        if (state.drawFrame || state.disposed) return;
        state.drawFrame = requestAnimationFrame(() => {
            state.drawFrame = 0;
            drawScene(state);
        });
    }

    function hitTest(state, clientX, clientY) {
        const rect = state.canvas.getBoundingClientRect();
        const x = (clientX - rect.left - state.offsetX) / state.zoom;
        const y = (clientY - rect.top - state.offsetY) / state.zoom;
        let nearest = null;
        let nearestDistance = Infinity;
        for (const node of state.nodes) {
            const dx = x - node.x;
            const dy = y - node.y;
            const distance = dx * dx + dy * dy;
            const radius = Math.max(node.r + 7, node.r * 1.4);
            if (distance <= radius * radius && distance < nearestDistance) {
                nearest = node;
                nearestDistance = distance;
            }
        }
        return nearest;
    }

    function screenToWorld(state, clientX, clientY) {
        const rect = state.canvas.getBoundingClientRect();
        return {
            x: (clientX - rect.left - state.offsetX) / state.zoom,
            y: (clientY - rect.top - state.offsetY) / state.zoom
        };
    }

    function installInteractions(state) {
        const canvas = state.canvas;
        state.onPointerDown = event => {
            if (event.button !== 0) return;
            const node = hitTest(state, event.clientX, event.clientY);
            canvas.setPointerCapture(event.pointerId);
            if (node) {
                const point = screenToWorld(state, event.clientX, event.clientY);
                state.gesture = { type: 'node', node, startX: event.clientX, startY: event.clientY, originX: node.x, originY: node.y, moved: false };
                node.fx = point.x;
                node.fy = point.y;
                node.vx = node.vy = 0;
                state.hovered = node;
                state.simulation.alphaTarget(0.28).restart();
            } else {
                state.gesture = { type: 'pan', startX: event.clientX, startY: event.clientY, originX: state.offsetX, originY: state.offsetY, moved: false };
            }
        };

        state.onPointerMove = event => {
            if (state.gesture) {
                const gesture = state.gesture;
                const dx = event.clientX - gesture.startX;
                const dy = event.clientY - gesture.startY;
                if (Math.abs(dx) + Math.abs(dy) > 2) gesture.moved = true;
                if (gesture.type === 'node') {
                    const point = screenToWorld(state, event.clientX, event.clientY);
                    gesture.node.fx = point.x;
                    gesture.node.fy = point.y;
                    gesture.node.x = point.x;
                    gesture.node.y = point.y;
                } else {
                    state.offsetX = gesture.originX + dx;
                    state.offsetY = gesture.originY + dy;
                    requestSceneDraw(state);
                }
                return;
            }

            const hovered = hitTest(state, event.clientX, event.clientY);
            if (hovered !== state.hovered) {
                state.hovered = hovered;
                canvas.style.cursor = hovered ? 'pointer' : 'grab';
                canvas.title = hovered ? `${hovered.name} · ${hovered.fileCount} files — click to browse` : 'Drag to pan; scroll to zoom';
                requestSceneDraw(state);
            }
        };

        state.onPointerUp = () => {
            const gesture = state.gesture;
            if (!gesture) return;
            state.gesture = null;
            if (gesture.type === 'node') {
                gesture.node.fx = null;
                gesture.node.fy = null;
                state.simulation.alphaTarget(0).alpha(Math.max(state.simulation.alpha(), 0.25)).restart();
                if (!gesture.moved && state.dotnetRef) {
                    state.dotnetRef.invokeMethodAsync('BrowseTag', gesture.node.id);
                }
            }
        };

        state.onWheel = event => {
            event.preventDefault();
            const rect = canvas.getBoundingClientRect();
            const px = event.clientX - rect.left;
            const py = event.clientY - rect.top;
            const nextZoom = clamp(state.zoom * Math.exp(-event.deltaY * 0.0013), 0.25, 5);
            const worldX = (px - state.offsetX) / state.zoom;
            const worldY = (py - state.offsetY) / state.zoom;
            state.zoom = nextZoom;
            state.offsetX = px - worldX * nextZoom;
            state.offsetY = py - worldY * nextZoom;
            requestSceneDraw(state);
        };

        state.onKeyDown = event => {
            if (event.key === '+' || event.key === '=') {
                state.zoom = clamp(state.zoom * 1.15, 0.25, 5);
                requestSceneDraw(state);
                event.preventDefault();
            } else if (event.key === '-') {
                state.zoom = clamp(state.zoom / 1.15, 0.25, 5);
                requestSceneDraw(state);
                event.preventDefault();
            } else if (event.key === 'Enter' && state.hovered && state.dotnetRef) {
                state.dotnetRef.invokeMethodAsync('BrowseTag', state.hovered.id);
            } else if (event.key.startsWith('Arrow')) {
                const offset = 28;
                state.offsetX += event.key === 'ArrowLeft' ? offset : event.key === 'ArrowRight' ? -offset : 0;
                state.offsetY += event.key === 'ArrowUp' ? offset : event.key === 'ArrowDown' ? -offset : 0;
                requestSceneDraw(state);
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
        if (state.drawFrame) cancelAnimationFrame(state.drawFrame);
        if (state.simulation) state.simulation.stop();
        if (state.resizeObserver) state.resizeObserver.disconnect();
        if (state.themeObserver) state.themeObserver.disconnect();
        const canvas = state.canvas;
        canvas.removeEventListener('pointerdown', state.onPointerDown);
        canvas.removeEventListener('pointermove', state.onPointerMove);
        canvas.removeEventListener('pointerup', state.onPointerUp);
        canvas.removeEventListener('pointercancel', state.onPointerUp);
        canvas.removeEventListener('wheel', state.onWheel);
        canvas.removeEventListener('keydown', state.onKeyDown);
        if (state.app) state.app.destroy(false, { children: true, texture: true, baseTexture: true });
    }

    function selectGraph(data, minSharedFiles, search) {
        const query = (search || '').trim().toLocaleLowerCase();
        const threshold = Number(minSharedFiles) || 1;
        const allowedEdges = data.edges.filter(edge => edge.isHierarchy || edge.sharedFileCount >= threshold);
        let visibleIds = new Set(data.nodes.map(node => node.id));

        if (query) {
            const matches = new Set(data.nodes.filter(node => node.name.toLocaleLowerCase().includes(query)).map(node => node.id));
            const neighborhood = new Set(matches);
            for (const edge of allowedEdges) {
                if (matches.has(edge.source)) neighborhood.add(edge.target);
                if (matches.has(edge.target)) neighborhood.add(edge.source);
            }
            visibleIds = neighborhood;
        }

        const edges = allowedEdges
            .filter(edge => visibleIds.has(edge.source) && visibleIds.has(edge.target))
            .sort((a, b) => Number(b.isHierarchy) - Number(a.isHierarchy) || b.sharedFileCount - a.sharedFileCount)
            .slice(0, 500)
            .map(edge => ({ ...edge }));
        const linkedIds = new Set();
        for (const edge of edges) {
            linkedIds.add(edge.source);
            linkedIds.add(edge.target);
        }
        const nodes = data.nodes
            .filter(node => visibleIds.has(node.id) && (linkedIds.has(node.id) || (query && node.name.toLocaleLowerCase().includes(query))))
            .map(node => ({ ...node, matchesSearch: Boolean(query && node.name.toLocaleLowerCase().includes(query)) }));
        return { nodes, edges };
    }

    function render(canvas, data, minSharedFiles, search, settings, dotnetRef) {
        if (!canvas) return;
        destroyState(states.get(canvas));
        const bounds = canvas.getBoundingClientRect();
        const width = Math.max(360, Math.round(bounds.width));
        const height = Math.max(280, Math.round(bounds.height));
        const graph = selectGraph(data, minSharedFiles, search);
        const nodes = graph.nodes;
        const links = graph.edges;

        nodes.forEach((node, index) => {
            const angle = 2.399963229728653 * index;
            const radius = Math.max(35, Math.min(width, height) * 0.32) * Math.sqrt((index + 1) / Math.max(1, nodes.length));
            node.x = width / 2 + Math.cos(angle) * radius;
            node.y = height / 2 + Math.sin(angle) * radius;
            node.r = clamp((6 + Math.sqrt(node.fileCount) * 1.5) * Number(settings?.nodeSize ?? 1), 5, 45);
        });

        const app = new PIXI.Application({
            view: canvas,
            width,
            height,
            resolution: Math.min(window.devicePixelRatio || 1, 2),
            autoDensity: true,
            antialias: true,
            backgroundAlpha: 0,
            powerPreference: 'high-performance',
            autoStart: false,
            sharedTicker: false
        });
        app.ticker.stop();

        const world = new PIXI.Container();
        const linksGraphics = new PIXI.Graphics();
        const nodesGraphics = new PIXI.Graphics();
        const emojiBackingGraphics = new PIXI.Graphics();
        const emojiContainer = new PIXI.Container();
        const labelsContainer = new PIXI.Container();
        world.addChild(linksGraphics, nodesGraphics, emojiBackingGraphics, emojiContainer, labelsContainer);
        app.stage.addChild(world);

        const state = {
            canvas,
            app,
            world,
            linksGraphics,
            nodesGraphics,
            emojiBackingGraphics,
            emojiContainer,
            labelsContainer,
            labels: new Map(),
            emojis: new Map(),
            nodes,
            links,
            simulation: null,
            width,
            height,
            zoom: 1,
            offsetX: 0,
            offsetY: 0,
            colors: getThemeColors(),
            settings: {
                nodeSize: Number(settings?.nodeSize ?? 1),
                linkThickness: Number(settings?.linkThickness ?? 2),
                textFadeThreshold: Number(settings?.textFadeThreshold ?? 0.35),
                showArrows: settings?.showArrows !== false,
                centerForce: Number(settings?.centerForce ?? 0.3),
                repelForce: Number(settings?.repelForce ?? 0.45),
                linkForce: Number(settings?.linkForce ?? 0.3),
                linkDistance: Number(settings?.linkDistance ?? 105)
            },
            hovered: null,
            gesture: null,
            dotnetRef,
            disposed: false
        };

        for (const node of nodes) {
            const label = makeNodeLabel(node, state);
            state.labels.set(node.id, label);
            labelsContainer.addChild(label);
            if (node.emoji) {
                const emoji = makeNodeEmoji(node);
                state.emojis.set(node.id, emoji);
                emojiContainer.addChild(emoji);
            }
        }
        if (!nodes.length) {
            const emptyText = query ? 'No matching tags or relationships.' : 'No relationships match this threshold.';
            state.emptyLabel = new PIXI.Text(emptyText, {
                fontFamily: 'Segoe UI, sans-serif',
                fontSize: 14,
                fill: colorToNumber(state.colors.muted, 0x888888)
            });
            state.emptyLabel.anchor.set(0.5);
            labelsContainer.addChild(state.emptyLabel);
        }

        const charge = d3.forceManyBody()
            .strength(-state.settings.repelForce * 520)
            .distanceMax(Math.max(width, height) * 1.5);
        const link = d3.forceLink(links)
            .id(node => node.id)
            .distance(state.settings.linkDistance)
            .strength(state.settings.linkForce);
        const center = d3.forceCenter(width / 2, height / 2).strength(state.settings.centerForce * 0.2);
        const xForce = d3.forceX(width / 2).strength(state.settings.centerForce * 0.04);
        const yForce = d3.forceY(height / 2).strength(state.settings.centerForce * 0.04);
        const collision = d3.forceCollide().radius(node => node.r + 8).strength(0.85).iterations(2);
        state.simulation = d3.forceSimulation(nodes)
            .alphaDecay(0.035)
            .velocityDecay(0.38)
            .force('charge', charge)
            .force('link', link)
            .force('center', center)
            .force('x', xForce)
            .force('y', yForce)
            .force('collision', collision)
            .stop();
        state.chargeForce = charge;
        state.linkForce = link;
        state.centerForce = center;
        state.collisionForce = collision;
        state.xForce = xForce;
        state.yForce = yForce;

        state.simulation.tick(90);
        state.simulation.on('tick', () => drawScene(state));
        states.set(canvas, state);
        installInteractions(state);
        drawScene(state);
        if (nodes.length) state.simulation.alpha(0.8).restart();

        state.resizeObserver = new ResizeObserver(entries => {
            const rect = entries[0]?.contentRect;
            if (!rect?.width || !rect?.height || state.disposed) return;
            const nextWidth = Math.round(rect.width);
            const nextHeight = Math.round(rect.height);
            if (nextWidth === state.width && nextHeight === state.height) return;
            state.width = nextWidth;
            state.height = nextHeight;
            state.app.renderer.resize(nextWidth, nextHeight);
            state.centerForce.x(nextWidth / 2).y(nextHeight / 2);
            state.xForce.x(nextWidth / 2);
            state.yForce.y(nextHeight / 2);
            state.simulation.alpha(0.4).restart();
        });
        state.resizeObserver.observe(canvas);

        state.themeObserver = new MutationObserver(() => {
            state.colors = getThemeColors();
            for (const label of state.labels.values()) {
                label.style = {
                    fontFamily: 'Segoe UI, sans-serif',
                    fontSize: 12,
                    fill: colorToNumber(state.colors.text, 0xdddddd),
                    stroke: colorToNumber(state.colors.surface, 0x242424),
                    strokeThickness: 3,
                    lineJoin: 'round'
                };
            }
            drawScene(state);
        });
        state.themeObserver.observe(document.documentElement, { attributes: true, attributeFilter: ['style'] });
    }

    function setSettings(canvas, settings) {
        const state = states.get(canvas);
        if (!state) return;
        state.settings = {
            nodeSize: Number(settings.nodeSize),
            linkThickness: Number(settings.linkThickness),
            textFadeThreshold: Number(settings.textFadeThreshold),
            showArrows: Boolean(settings.showArrows),
            centerForce: Number(settings.centerForce),
            repelForce: Number(settings.repelForce),
            linkForce: Number(settings.linkForce),
            linkDistance: Number(settings.linkDistance)
        };
        for (const node of state.nodes) {
            node.r = clamp((6 + Math.sqrt(node.fileCount) * 1.5) * state.settings.nodeSize, 5, 45);
        }
        state.chargeForce.strength(-state.settings.repelForce * 520);
        state.linkForce.strength(state.settings.linkForce).distance(state.settings.linkDistance);
        state.centerForce.strength(state.settings.centerForce * 0.2);
        state.xForce.strength(state.settings.centerForce * 0.04);
        state.yForce.strength(state.settings.centerForce * 0.04);
        state.collisionForce.radius(node => node.r + 8);
        state.simulation.alpha(0.75).restart();
        drawScene(state);
    }

    function destroy(canvas) {
        destroyState(states.get(canvas));
        states.delete(canvas);
    }

    return { render, setSettings, destroy };
})();

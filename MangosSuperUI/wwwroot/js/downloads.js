// MangosSuperUI — Downloads & Uploads JS

$(function () {

    var TYPE_NAMES = {
        0: 'Door', 1: 'Button', 2: 'Quest Giver', 3: 'Chest', 5: 'Generic',
        6: 'Trap', 7: 'Chair', 8: 'Spell Focus', 9: 'Text', 10: 'Goober',
        11: 'Transport', 13: 'Camera', 15: 'MO Transport', 17: 'Fishing Node',
        18: 'Ritual', 19: 'Mailbox', 20: 'Auction House', 22: 'Spell Caster',
        23: 'Meeting Stone', 24: 'Flag Stand', 25: 'Fishing Hole', 26: 'Flag Drop',
        29: 'Capture Point', 30: 'Aura Generator', 31: 'Dungeon Difficulty'
    };

    // ===================== LOAD ADDONS =====================

    function loadAddons() {
        $.getJSON('/Downloads/AddonList', function (data) {
            if (!data.addons || data.addons.length === 0) {
                $('#addonCards').html(
                    '<div class="dl-placeholder">' +
                    '<i class="fa-solid fa-puzzle-piece"></i>' +
                    'No addons found. Place addon folders in <code>wwwroot/addons/</code> with a matching .zip file.' +
                    '</div>'
                );
                return;
            }

            var html = '';
            data.addons.forEach(function (addon) {
                var isPlacer = addon.folder === 'MangosSuperUI_Placer';

                html += '<div class="dl-card" data-addon="' + esc(addon.folder) + '">';
                html += '<div class="dl-card-header">';
                html += '<div class="dl-card-icon addon"><i class="fa-solid ' + (isPlacer ? 'fa-cubes' : 'fa-puzzle-piece') + '"></i></div>';
                html += '<div>';
                html += '<div class="dl-card-title">' + esc(addon.title) + '</div>';
                html += '<div class="dl-card-version">';
                if (addon.version) html += 'v' + esc(addon.version);
                if (addon.author) html += ' &middot; ' + esc(addon.author);
                html += ' &middot; ' + addon.luaFiles + ' file' + (addon.luaFiles !== 1 ? 's' : '');
                html += '</div></div></div>';

                if (addon.notes) {
                    html += '<div class="dl-card-desc">' + esc(addon.notes) + '</div>';
                }

                // README content
                if (addon.readme) {
                    html += '<div class="dl-readme">' + renderMarkdown(addon.readme) + '</div>';
                }

                // Placer-specific: catalog stats
                if (isPlacer) {
                    html += '<div class="dl-stats" id="placerStats">';
                    html += '<div class="dl-stat">Objects: <span class="dl-stat-value" id="statObjectCount">—</span></div>';
                    html += '<div class="dl-stat">Spawns: <span class="dl-stat-value" id="statSpawnCount">—</span></div>';
                    html += '</div>';
                    html += '<div class="dl-type-pills" id="placerTypePills"></div>';
                }

                html += '<div class="dl-actions">';
                html += '<button class="dl-btn primary btn-download-addon" data-name="' + esc(addon.folder) + '">';
                html += '<i class="fa-solid fa-download"></i> Download';
                html += '</button>';
                html += '</div>';

                // Install steps (collapsible)
                if (isPlacer) {
                    html += buildPlacerInstallGuide();
                    html += buildPlacerCommandRef();
                }

                html += '</div>';
            });

            $('#addonCards').html(html);

            // Load Placer-specific info if present
            if (data.addons.some(function (a) { return a.folder === 'MangosSuperUI_Placer'; })) {
                loadPlacerInfo();
            }
        }).fail(function () {
            $('#addonCards').html('<div class="dl-placeholder"><i class="fa-solid fa-circle-exclamation"></i>Failed to load addon list.</div>');
        });
    }

    function buildPlacerInstallGuide() {
        return '<div class="dl-install-steps">' +
            '<div class="dl-install-toggle" id="installToggle">' +
            '<i class="fa-solid fa-chevron-right" id="installChevron"></i> Installation Guide</div>' +
            '<div class="dl-install-body" id="installBody">' +
            '<div class="dl-step"><div class="dl-step-num">1</div><div>Click <strong>Download</strong> to get the ZIP file.</div></div>' +
            '<div class="dl-step"><div class="dl-step-num">2</div><div>Extract into your WoW client\'s <code>Interface/AddOns/</code> folder.</div></div>' +
            '<div class="dl-step"><div class="dl-step-num">3</div><div>Log into WoW. The addon prints how many custom objects are in the catalog.</div></div>' +
            '<div class="dl-step"><div class="dl-step-num">4</div><div>Type <code>/msui</code> to open the placer window.</div></div>' +
            '<div class="dl-step"><div class="dl-step-num">5</div><div>After creating new objects, visit the Downloads page and click <strong>Download</strong> again. The catalog is rebuilt from the database every time. Replace the addon folder and <code>/reload</code> in-game.</div></div>' +
            '</div></div>';
    }

    function buildPlacerCommandRef() {
        return '<div class="dl-commands">' +
            '<div class="dl-commands-toggle" id="commandsToggle">' +
            '<i class="fa-solid fa-chevron-right" id="commandsChevron"></i> Slash Commands</div>' +
            '<div class="dl-commands-body" id="commandsBody">' +
            '<div class="dl-cmd-row"><span class="dl-cmd-name">/msui</span><span class="dl-cmd-desc">Open/close the placer window</span></div>' +
            '<div class="dl-cmd-row"><span class="dl-cmd-name">/msui place &lt;entry&gt;</span><span class="dl-cmd-desc">Spawn an object at your feet</span></div>' +
            '<div class="dl-cmd-row"><span class="dl-cmd-name">/msui near [dist]</span><span class="dl-cmd-desc">Scan nearby objects with GUIDs</span></div>' +
            '<div class="dl-cmd-row"><span class="dl-cmd-name">/msui select &lt;guid&gt;</span><span class="dl-cmd-desc">Set target GUID for delete/move/turn</span></div>' +
            '<div class="dl-cmd-row"><span class="dl-cmd-name">/msui delete [guid]</span><span class="dl-cmd-desc">Remove object by GUID</span></div>' +
            '<div class="dl-cmd-row"><span class="dl-cmd-name">/msui move [guid]</span><span class="dl-cmd-desc">Move object to your position</span></div>' +
            '<div class="dl-cmd-row"><span class="dl-cmd-name">/msui turn [guid] &lt;deg&gt;</span><span class="dl-cmd-desc">Rotate targeted object</span></div>' +
            '</div></div>';
    }

    // ===================== PLACER INFO =====================

    function loadPlacerInfo() {
        $.getJSON('/Downloads/PlacerInfo', function (data) {
            $('#statObjectCount').text(data.objectCount.toLocaleString());
            $('#statSpawnCount').text(data.spawnCount.toLocaleString());

            if (data.typeCounts && data.typeCounts.length > 0) {
                var html = '';
                data.typeCounts.forEach(function (tc) {
                    var name = TYPE_NAMES[tc.type] || ('Type ' + tc.type);
                    html += '<span class="dl-type-pill">' + esc(name) + '<span class="pill-count">' + tc.cnt + '</span></span>';
                });
                $('#placerTypePills').html(html);
            } else {
                $('#placerTypePills').html('<span style="font-size:12px;color:var(--text-muted);">No custom objects yet.</span>');
            }
        });
    }

    // ===================== EVENTS =====================

    $(document).on('click', '.btn-download-addon', function () {
        var name = $(this).data('name');
        window.location.href = '/Downloads/Addon?name=' + encodeURIComponent(name);
    });

    $(document).on('click', '#installToggle', function () {
        $('#installBody').toggleClass('open');
        $('#installChevron').toggleClass('fa-chevron-right fa-chevron-down');
    });

    $(document).on('click', '#commandsToggle', function () {
        $('#commandsBody').toggleClass('open');
        $('#commandsChevron').toggleClass('fa-chevron-right fa-chevron-down');
    });

    // ===================== HELPERS =====================

    function renderMarkdown(md) {
        if (!md) return '';
        var lines = md.split('\n');
        var html = '';
        var inList = false;

        for (var i = 0; i < lines.length; i++) {
            var line = lines[i];

            // Headers
            if (line.match(/^### /)) { if (inList) { html += '</ul>'; inList = false; } html += '<h4 class="dl-readme-h">' + inlineFormat(line.substring(4)) + '</h4>'; continue; }
            if (line.match(/^## /)) { if (inList) { html += '</ul>'; inList = false; } html += '<h3 class="dl-readme-h">' + inlineFormat(line.substring(3)) + '</h3>'; continue; }
            if (line.match(/^# /)) { if (inList) { html += '</ul>'; inList = false; } html += '<h3 class="dl-readme-h">' + inlineFormat(line.substring(2)) + '</h3>'; continue; }

            // List items
            if (line.match(/^[-*] /)) {
                if (!inList) { html += '<ul class="dl-readme-list">'; inList = true; }
                html += '<li>' + inlineFormat(line.substring(2)) + '</li>';
                continue;
            }

            // Blank line
            if (line.trim() === '') {
                if (inList) { html += '</ul>'; inList = false; }
                continue;
            }

            // Paragraph
            if (inList) { html += '</ul>'; inList = false; }
            html += '<p class="dl-readme-p">' + inlineFormat(line) + '</p>';
        }
        if (inList) html += '</ul>';
        return html;
    }

    function inlineFormat(text) {
        text = esc(text);
        text = text.replace(/`([^`]+)`/g, '<code class="dl-readme-code">$1</code>');
        text = text.replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>');
        text = text.replace(/\*([^*]+)\*/g, '<em>$1</em>');
        return text;
    }

    function esc(text) {
        if (!text && text !== 0) return '';
        var d = document.createElement('div');
        d.textContent = text;
        return d.innerHTML;
    }

    // ===================== INIT =====================
    loadAddons();

});
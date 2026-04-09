// MangosSuperUI — Config Editor Page JS (Enhanced)

$(function () {

    // ===================== STATE =====================
    var metadata = [];          // from config-metadata.json
    var liveValues = {};        // key → { value, line, isQuoted }
    var pendingChanges = {};    // key → { newValue, oldValue, isQuoted }
    var activeSection = null;
    var searchTimer = null;

    // ===================== INIT =====================
    $.getJSON('/data/config-metadata.json', function (meta) {
        metadata = meta;
        loadConfValues();
    });

    function loadConfValues() {
        $.getJSON('/Config/Load', function (data) {
            if (!data.success) {
                $('#sectionContent').html('<div class="text-center" style="padding: 60px; color: var(--status-error);"><i class="fa-solid fa-triangle-exclamation" style="font-size: 36px; margin-bottom: 12px; display: block;"></i>' + escapeHtml(data.error) + '</div>');
                return;
            }

            $('#confPath').text(data.path);

            liveValues = {};
            for (var i = 0; i < data.settings.length; i++) {
                var s = data.settings[i];
                liveValues[s.key] = { value: s.value, line: s.line, isQuoted: s.isQuoted };
            }

            renderTabs();

            if (metadata.length > 0) {
                showSection(metadata[0].id);
            }
        });
    }

    // ===================== TABS =====================
    function renderTabs() {
        var $tabs = $('#sectionTabs');
        $tabs.empty();

        for (var i = 0; i < metadata.length; i++) {
            var sec = metadata[i];
            var count = countSectionValues(sec);
            var html = '<button class="cfg-tab" data-section="' + sec.id + '">';
            html += '<i class="fa-solid ' + sec.icon + '"></i>';
            html += escapeHtml(sec.title);
            if (count > 0) html += '<span class="cfg-tab-badge">' + count + '</span>';
            html += '</button>';
            $tabs.append(html);
        }

        var unmapped = getUnmappedSettings();
        if (unmapped.length > 0) {
            $tabs.append('<button class="cfg-tab" data-section="_unmapped"><i class="fa-solid fa-code"></i>Other<span class="cfg-tab-badge">' + unmapped.length + '</span></button>');
        }
    }

    function countSectionValues(section) {
        var count = 0;
        for (var i = 0; i < section.settings.length; i++) {
            if (liveValues[section.settings[i].key]) count++;
        }
        return count;
    }

    $(document).on('click', '.cfg-tab', function () {
        var sectionId = $(this).data('section');
        $('#cfgSearch').val('');
        $('#searchResults').hide();
        showSection(sectionId);
    });

    // ===================== SECTION RENDER =====================
    function showSection(sectionId) {
        activeSection = sectionId;
        $('.cfg-tab').removeClass('active');
        $('.cfg-tab[data-section="' + sectionId + '"]').addClass('active');

        var $content = $('#sectionContent');
        $content.empty().show();

        if (sectionId === '_unmapped') {
            renderUnmappedSection($content);
            return;
        }

        var section = metadata.find(function (s) { return s.id === sectionId; });
        if (!section) return;

        $content.append('<div class="cfg-section-desc">' + escapeHtml(section.description) + '</div>');

        var $card = $('<div class="card"><div class="card-body" style="padding: 0;"></div></div>');
        var $body = $card.find('.card-body');

        for (var i = 0; i < section.settings.length; i++) {
            var meta = section.settings[i];
            var live = liveValues[meta.key];
            if (!live) continue;

            $body.append(renderSettingRow(meta, live, ''));
        }

        $content.append($card);
    }

    function renderUnmappedSection($content) {
        var unmapped = getUnmappedSettings();
        $content.append('<div class="cfg-section-desc">Settings found in the conf file that aren\'t mapped to a named section. These use their raw key names.</div>');

        var $card = $('<div class="card"><div class="card-body" style="padding: 0;"></div></div>');
        var $body = $card.find('.card-body');

        for (var i = 0; i < unmapped.length; i++) {
            var key = unmapped[i];
            var live = liveValues[key];
            var fakeMeta = { key: key, label: key, desc: '', type: 'string' };
            $body.append(renderSettingRow(fakeMeta, live, '', true));
        }

        $content.append($card);
    }

    function getUnmappedSettings() {
        var mapped = {};
        for (var i = 0; i < metadata.length; i++) {
            for (var j = 0; j < metadata[i].settings.length; j++) {
                mapped[metadata[i].settings[j].key] = true;
            }
        }

        var unmapped = [];
        for (var key in liveValues) {
            if (!mapped[key]) unmapped.push(key);
        }
        return unmapped.sort();
    }

    // ===================== HELPERS =====================

    /** Determine if a setting should render as a slider */
    function isSliderCandidate(meta) {
        if (meta.type === 'bool' || meta.type === 'select' || meta.type === 'string' || meta.type === 'path') return false;
        return (meta.min !== undefined && meta.max !== undefined);
    }

    /** Build a human-readable default string */
    function formatDefault(meta) {
        if (meta.default === undefined) return null;
        if (meta.type === 'bool') return meta.default === 1 || meta.default === '1' ? 'Enabled' : 'Disabled';
        if (meta.type === 'select' && meta.options) {
            var label = meta.options[String(meta.default)];
            return label ? label + ' (' + meta.default + ')' : String(meta.default);
        }
        var s = String(meta.default);
        if (meta.unit) s += ' ' + meta.unit;
        return s;
    }

    /** Check if current value differs from default */
    function isNonDefault(meta, currentValue) {
        if (meta.default === undefined) return false;
        // Normalize: strip trailing semicolons, quotes, whitespace
        var cur = String(currentValue).replace(/;$/, '').trim();
        var def = String(meta.default).trim();
        return cur !== def;
    }

    /** Check if a value is outside min/max bounds */
    function isOutOfRange(meta, value) {
        if (meta.type === 'bool' || meta.type === 'select' || meta.type === 'string' || meta.type === 'path') return false;
        var num = parseFloat(value);
        if (isNaN(num)) return false;
        if (meta.min !== undefined && num < meta.min) return true;
        if (meta.max !== undefined && num > meta.max) return true;
        return false;
    }

    /** Slider step: use 0.1 for floats, 1 for ints */
    function getSliderStep(meta) {
        if (meta.default !== undefined && String(meta.default).indexOf('.') >= 0) return 0.1;
        if (meta.key && (meta.key.indexOf('Rate.') === 0 || meta.key.indexOf('Chance') >= 0)) return 0.1;
        return 1;
    }

    // ===================== SETTING ROW =====================
    function renderSettingRow(meta, live, query, isUnmapped) {
        var isModified = !!pendingChanges[meta.key];
        var displayValue = isModified ? pendingChanges[meta.key].newValue : live.value;
        var modClass = isModified ? ' modified' : '';
        var nonDefault = isNonDefault(meta, displayValue);
        var outOfRange = isOutOfRange(meta, displayValue);
        if (nonDefault) modClass += ' non-default';
        if (outOfRange) modClass += ' out-of-range';

        var html = '<div class="cfg-row' + modClass + '" data-key="' + escapeAttr(meta.key) + '">';

        // ---- Label column ----
        html += '<div class="cfg-label-col">';

        // Label line with badges
        var label = escapeHtml(meta.label);
        if (query) label = highlightMatch(label, query);
        if (isUnmapped) {
            html += '<div class="cfg-label">' + label + ' <span class="cfg-unmapped-label">(unmapped)</span></div>';
        } else {
            html += '<div class="cfg-label">' + label;
            // Restart badge
            if (meta.restart) {
                html += ' <span class="cfg-badge cfg-badge-restart" title="Requires server restart to take effect"><i class="fa-solid fa-rotate"></i> restart</span>';
            }
            html += '</div>';
        }

        // Key + line number
        html += '<div class="cfg-key">' + escapeHtml(meta.key) + ' <span style="color: var(--text-muted); font-size: 10px;">L' + live.line + '</span></div>';

        // Description
        if (meta.desc) {
            var desc = escapeHtml(meta.desc);
            if (query) desc = highlightMatch(desc, query);
            html += '<div class="cfg-desc">' + desc + '</div>';
        }

        // Warning
        if (meta.warn) {
            html += '<div class="cfg-warn"><i class="fa-solid fa-triangle-exclamation"></i> ' + escapeHtml(meta.warn) + '</div>';
        }

        // Default + Range info line
        var infoChips = [];
        var defStr = formatDefault(meta);
        if (defStr) {
            var defClass = nonDefault ? 'cfg-chip cfg-chip-nondefault' : 'cfg-chip';
            infoChips.push('<span class="' + defClass + '" title="Default value">default: ' + escapeHtml(defStr) + '</span>');
        }
        if (meta.min !== undefined && meta.max !== undefined) {
            infoChips.push('<span class="cfg-chip" title="Valid range">range: ' + meta.min + ' – ' + meta.max + '</span>');
        } else if (meta.min !== undefined) {
            infoChips.push('<span class="cfg-chip" title="Minimum value">min: ' + meta.min + '</span>');
        }
        if (infoChips.length > 0) {
            html += '<div class="cfg-info-chips">' + infoChips.join('') + '</div>';
        }

        // Out-of-range warning
        if (outOfRange) {
            html += '<div class="cfg-warn"><i class="fa-solid fa-circle-exclamation"></i> Value is outside recommended range';
            if (meta.min !== undefined && meta.max !== undefined) html += ' (' + meta.min + ' – ' + meta.max + ')';
            html += '</div>';
        }

        // Impact tags (shown on hover via CSS or always in search results)
        if (meta.impact && meta.impact.length > 0 && !isUnmapped) {
            html += '<div class="cfg-impacts">';
            for (var t = 0; t < meta.impact.length; t++) {
                var tag = meta.impact[t].replace(/_/g, ' ');
                if (query) tag = highlightMatch(tag, query);
                html += '<span class="cfg-impact">' + tag + '</span>';
            }
            html += '</div>';
        }

        html += '</div>'; // end label-col

        // ---- Value column ----
        html += '<div class="cfg-value-col">';
        if (meta.type === 'bool') {
            html += '<select class="cfg-input' + (isModified ? ' changed' : '') + '" data-key="' + escapeAttr(meta.key) + '" data-original="' + escapeAttr(live.value) + '" data-quoted="' + (live.isQuoted ? '1' : '0') + '">';
            html += '<option value="0"' + (displayValue === '0' ? ' selected' : '') + '>Disabled (0)</option>';
            html += '<option value="1"' + (displayValue === '1' ? ' selected' : '') + '>Enabled (1)</option>';
            html += '</select>';
        } else if (meta.type === 'select' && meta.options) {
            html += '<select class="cfg-input' + (isModified ? ' changed' : '') + '" data-key="' + escapeAttr(meta.key) + '" data-original="' + escapeAttr(live.value) + '" data-quoted="' + (live.isQuoted ? '1' : '0') + '">';
            for (var val in meta.options) {
                var sel = (String(displayValue) === String(val)) ? ' selected' : '';
                html += '<option value="' + escapeAttr(val) + '"' + sel + '>' + escapeHtml(meta.options[val]) + ' (' + val + ')</option>';
            }
            if (meta.options[displayValue] === undefined) {
                html += '<option value="' + escapeAttr(displayValue) + '" selected>' + escapeHtml(displayValue) + ' (custom)</option>';
            }
            html += '</select>';
        } else if (isSliderCandidate(meta)) {
            // Slider + text input combo
            var step = getSliderStep(meta);
            html += '<div class="cfg-slider-wrap">';
            html += '<input type="range" class="cfg-slider" data-key="' + escapeAttr(meta.key) + '" min="' + meta.min + '" max="' + meta.max + '" step="' + step + '" value="' + escapeAttr(displayValue) + '" />';
            html += '<input type="text" class="cfg-input cfg-input-narrow' + (isModified ? ' changed' : '') + '" data-key="' + escapeAttr(meta.key) + '" data-original="' + escapeAttr(live.value) + '" data-quoted="' + (live.isQuoted ? '1' : '0') + '" value="' + escapeAttr(displayValue) + '" />';
            if (meta.unit) html += '<span class="cfg-unit">' + meta.unit + '</span>';
            html += '</div>';
        } else {
            html += '<input type="text" class="cfg-input' + (isModified ? ' changed' : '') + '" data-key="' + escapeAttr(meta.key) + '" data-original="' + escapeAttr(live.value) + '" data-quoted="' + (live.isQuoted ? '1' : '0') + '" value="' + escapeAttr(displayValue) + '" />';
            if (meta.unit) html += '<span class="cfg-unit">' + meta.unit + '</span>';
        }
        html += '</div>'; // end value-col

        // ---- Action column ----
        html += '<div class="cfg-action-col">';
        // Reset to default button
        if (meta.default !== undefined && nonDefault) {
            html += '<button class="cfg-default-btn" data-key="' + escapeAttr(meta.key) + '" data-default="' + escapeAttr(String(meta.default)) + '" title="Reset to default: ' + escapeAttr(String(meta.default)) + '"><i class="fa-solid fa-arrow-rotate-left"></i></button>';
        }
        // Revert button (only visible when modified)
        html += '<button class="cfg-revert" data-key="' + escapeAttr(meta.key) + '" title="Revert to saved value"><i class="fa-solid fa-rotate-left"></i></button>';
        html += '</div>';

        html += '</div>';
        return html;
    }

    // ===================== SEARCH =====================
    $('#cfgSearch').on('input', function () {
        clearTimeout(searchTimer);
        var query = $(this).val().trim().toLowerCase();

        if (!query) {
            $('#searchResults').hide();
            $('#sectionTabs').show();
            if (activeSection) showSection(activeSection);
            else $('#sectionContent').show();
            return;
        }

        searchTimer = setTimeout(function () { doSearch(query); }, 150);
    });

    function doSearch(query) {
        $('#sectionTabs').show();
        $('#sectionContent').hide();
        var $results = $('#searchResults').empty().show();

        var totalFound = 0;

        for (var i = 0; i < metadata.length; i++) {
            var section = metadata[i];
            var matches = [];

            for (var j = 0; j < section.settings.length; j++) {
                var meta = section.settings[j];
                var live = liveValues[meta.key];
                if (!live) continue;

                // Build search haystack: label, key, desc, warn, AND impact tags
                var haystack = (meta.label + ' ' + meta.key + ' ' + meta.desc + ' ' + (meta.warn || ''));
                if (meta.impact) haystack += ' ' + meta.impact.join(' ').replace(/_/g, ' ');
                haystack = haystack.toLowerCase();

                if (haystack.indexOf(query) >= 0) {
                    matches.push({ meta: meta, live: live });
                }
            }

            if (matches.length > 0) {
                $results.append('<div class="cfg-search-section"><i class="fa-solid ' + section.icon + '"></i> ' + escapeHtml(section.title) + ' (' + matches.length + ')</div>');
                var $card = $('<div class="card mb-3"><div class="card-body" style="padding: 0;"></div></div>');
                var $body = $card.find('.card-body');
                for (var k = 0; k < matches.length; k++) {
                    $body.append(renderSettingRow(matches[k].meta, matches[k].live, query));
                }
                $results.append($card);
                totalFound += matches.length;
            }
        }

        // Search unmapped
        var unmapped = getUnmappedSettings();
        var unmappedMatches = [];
        for (var u = 0; u < unmapped.length; u++) {
            if (unmapped[u].toLowerCase().indexOf(query) >= 0) {
                unmappedMatches.push(unmapped[u]);
            }
        }
        if (unmappedMatches.length > 0) {
            $results.append('<div class="cfg-search-section"><i class="fa-solid fa-code"></i> Other (' + unmappedMatches.length + ')</div>');
            var $ucard = $('<div class="card mb-3"><div class="card-body" style="padding: 0;"></div></div>');
            var $ubody = $ucard.find('.card-body');
            for (var v = 0; v < unmappedMatches.length; v++) {
                var ukey = unmappedMatches[v];
                $ubody.append(renderSettingRow({ key: ukey, label: ukey, desc: '', type: 'string' }, liveValues[ukey], query, true));
            }
            $results.append($ucard);
            totalFound += unmappedMatches.length;
        }

        if (totalFound === 0) {
            $results.html('<div class="text-center text-muted" style="padding: 40px;">No settings match "' + escapeHtml(query) + '"</div>');
        } else {
            $results.prepend('<div class="cfg-search-count">' + totalFound + ' setting(s) found</div>');
        }
    }

    // ===================== SLIDER SYNC =====================
    // Slider → text input
    $(document).on('input', '.cfg-slider', function () {
        var key = $(this).data('key');
        var val = $(this).val();
        var $row = $(this).closest('.cfg-row');
        var $input = $row.find('.cfg-input[data-key="' + key + '"]');
        $input.val(val).trigger('input');
    });

    // Text input → slider (for slider rows)
    $(document).on('input', '.cfg-input-narrow', function () {
        var key = $(this).data('key');
        var val = $(this).val();
        var $row = $(this).closest('.cfg-row');
        var $slider = $row.find('.cfg-slider[data-key="' + key + '"]');
        if ($slider.length) {
            $slider.val(val);
        }
    });

    // ===================== EDIT TRACKING =====================
    $(document).on('input change', '.cfg-input', function () {
        var key = $(this).data('key');
        var original = String($(this).data('original'));
        var isQuoted = $(this).data('quoted') === 1 || $(this).data('quoted') === '1';
        var newVal = $(this).val();
        var $row = $(this).closest('.cfg-row');

        if (newVal !== original) {
            pendingChanges[key] = { newValue: newVal, oldValue: original, isQuoted: isQuoted };
            $(this).addClass('changed');
            $row.addClass('modified');
        } else {
            delete pendingChanges[key];
            $(this).removeClass('changed');
            $row.removeClass('modified');
        }

        // Update non-default / out-of-range states
        var meta = findMeta(key);
        if (meta) {
            $row.toggleClass('non-default', isNonDefault(meta, newVal));
            $row.toggleClass('out-of-range', isOutOfRange(meta, newVal));
        }

        updateSaveButton();
    });

    $(document).on('click', '.cfg-revert', function () {
        var key = $(this).data('key');
        var $row = $(this).closest('.cfg-row');
        var $input = $row.find('.cfg-input');
        var original = $input.data('original');
        $input.val(original).removeClass('changed');
        $row.removeClass('modified');
        delete pendingChanges[key];

        // Sync slider
        var $slider = $row.find('.cfg-slider');
        if ($slider.length) $slider.val(original);

        // Re-check non-default
        var meta = findMeta(key);
        if (meta) {
            $row.toggleClass('non-default', isNonDefault(meta, original));
            $row.toggleClass('out-of-range', isOutOfRange(meta, original));
        }

        updateSaveButton();
    });

    // Reset to default
    $(document).on('click', '.cfg-default-btn', function () {
        var key = $(this).data('key');
        var defVal = String($(this).data('default'));
        var $row = $(this).closest('.cfg-row');
        var $input = $row.find('.cfg-input[data-key="' + key + '"]');
        $input.val(defVal).trigger('input');

        // Sync slider
        var $slider = $row.find('.cfg-slider[data-key="' + key + '"]');
        if ($slider.length) $slider.val(defVal);
    });

    function findMeta(key) {
        for (var i = 0; i < metadata.length; i++) {
            for (var j = 0; j < metadata[i].settings.length; j++) {
                if (metadata[i].settings[j].key === key) return metadata[i].settings[j];
            }
        }
        return null;
    }

    function updateSaveButton() {
        var count = Object.keys(pendingChanges).length;
        if (count > 0) {
            $('#changeCount').text(count);
            $('#btnSaveAll').show();
        } else {
            $('#btnSaveAll').hide();
        }
    }

    // ===================== SAVE =====================
    $('#btnSaveAll').on('click', function () {
        var changes = [];
        for (var key in pendingChanges) {
            changes.push({ key: key, value: pendingChanges[key].newValue, forceQuote: pendingChanges[key].isQuoted });
        }
        if (changes.length === 0) return;

        var $btn = $(this);
        $btn.prop('disabled', true).html('<i class="fa-solid fa-spinner fa-spin"></i> Saving...');

        $.ajax({
            url: '/Config/Save',
            type: 'POST',
            contentType: 'application/json',
            data: JSON.stringify({ changes: changes }),
            success: function (data) {
                if (data.success) {
                    cfgOutput('Config saved — ' + data.appliedCount + ' setting(s) updated. Backup: ' + data.backupFile, 'ok');
                    if (data.changes) {
                        for (var i = 0; i < data.changes.length; i++) {
                            var c = data.changes[i];
                            cfgOutput('  L' + c.line + ' ' + c.key + ': ' + c.oldValue + ' → ' + c.newValue, 'sys');
                        }
                    }
                    showToast('Config saved (' + data.appliedCount + ' changes)', 'success');
                    pendingChanges = {};
                    updateSaveButton();
                    loadConfValues();
                    cfgOutput('Click "Reload Server" to apply changes to the running server.', 'sys');
                } else {
                    cfgOutput('Save failed: ' + (data.error || 'Unknown'), 'err');
                    showToast('Save failed', 'error');
                }
            },
            error: function (xhr) { cfgOutput('Request failed: ' + xhr.statusText, 'err'); },
            complete: function () {
                $btn.prop('disabled', false).html('<i class="fa-solid fa-floppy-disk"></i> Save <span id="changeCount">0</span> Change(s)');
            }
        });
    });

    // ===================== RELOAD =====================
    $('#btnReload').on('click', function () {
        var $btn = $(this);
        $btn.prop('disabled', true).html('<i class="fa-solid fa-spinner fa-spin"></i> Reloading...');

        $.ajax({
            url: '/Config/Reload',
            type: 'POST',
            success: function (data) {
                if (data.success) {
                    cfgOutput('.reload config → ' + (data.response || 'OK'), 'ok');
                    showToast('Config reloaded on server', 'success');
                } else {
                    cfgOutput('.reload config failed: ' + (data.response || 'Error'), 'err');
                }
            },
            error: function (xhr) { cfgOutput('Reload failed: ' + xhr.statusText, 'err'); },
            complete: function () { $btn.prop('disabled', false).html('<i class="fa-solid fa-arrows-rotate"></i> Reload Server'); }
        });
    });

    // ===================== OUTPUT =====================
    function cfgOutput(text, type) {
        $('#outputCard').show();
        var colors = { cmd: '#7aa2f7', ok: '#c8d0da', err: '#f7768e', sys: '#9ece6a' };
        var $el = $('#cfgOutput');
        $el.append('<div style="color: ' + (colors[type] || colors.ok) + ';">' + escapeHtml(text) + '</div>');
        while ($el[0].children.length > 200) $el[0].removeChild($el[0].children[0]);
        $el.scrollTop($el[0].scrollHeight);
    }
    $('#btnClearOutput').on('click', function () { $('#cfgOutput').empty(); });

    // ===================== TOAST =====================
    function showToast(msg, type) {
        var $t = $('<div class="cfg-toast cfg-toast-' + type + '">' + escapeHtml(msg) + '</div>');
        $('body').append($t);
        setTimeout(function () { $t.fadeOut(300, function () { $(this).remove(); }); }, 3000);
    }

    // ===================== UTILITY =====================
    function escapeHtml(t) { if (!t) return ''; var d = document.createElement('div'); d.textContent = t; return d.innerHTML; }
    function escapeAttr(t) { return String(t).replace(/"/g, '&quot;').replace(/'/g, '&#39;'); }
    function highlightMatch(html, q) {
        if (!q) return html;
        var re = new RegExp('(' + q.replace(/[.*+?^${}()|[\]\\]/g, '\\$&') + ')', 'gi');
        return html.replace(re, '<span class="cfg-hl">$1</span>');
    }

    $('#cfgSearch').focus();
});
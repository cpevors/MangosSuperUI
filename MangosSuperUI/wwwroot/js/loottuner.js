// MangosSuperUI — Loot Tuner JS (v2 — chip-based filters)

$(function () {

    var previewData = null;
    var currentMultiplier = 2.0;

    var QUALITY_NAMES = ['Poor', 'Common', 'Uncommon', 'Rare', 'Epic', 'Legendary', 'Artifact'];

    // ===================== BASELINE INTEGRATION =====================

    BaselineSystem.checkStatus(function (status) {
        BaselineSystem.renderWarningBanner('#baselineWarning');
    });

    // Categorized instances with level ranges
    var DUNGEONS = [
        { id: 389, name: 'Ragefire Chasm', level: '13-18' },
        { id: 36, name: 'Deadmines', level: '17-21' },
        { id: 43, name: 'Wailing Caverns', level: '17-24' },
        { id: 34, name: 'The Stockade', level: '22-30' },
        { id: 48, name: 'Blackfathom Deeps', level: '24-32' },
        { id: 33, name: 'Shadowfang Keep', level: '22-30' },
        { id: 47, name: 'Razorfen Kraul', level: '29-38' },
        { id: 90, name: 'Gnomeregan', level: '29-38' },
        { id: 189, name: 'Scarlet Monastery', level: '28-45' },
        { id: 129, name: 'Razorfen Downs', level: '37-46' },
        { id: 70, name: 'Uldaman', level: '41-51' },
        { id: 209, name: 'Zul\'Farrak', level: '44-54' },
        { id: 349, name: 'Maraudon', level: '46-55' },
        { id: 109, name: 'Sunken Temple', level: '50-56' },
        { id: 230, name: 'Blackrock Depths', level: '52-60' },
        { id: 229, name: 'Blackrock Spire', level: '55-60' },
        { id: 429, name: 'Dire Maul', level: '55-60' },
        { id: 329, name: 'Stratholme', level: '58-60' },
        { id: 289, name: 'Scholomance', level: '58-60' }
    ];

    var RAIDS = [
        { id: 249, name: 'Onyxia\'s Lair', level: '60' },
        { id: 409, name: 'Molten Core', level: '60' },
        { id: 469, name: 'Blackwing Lair', level: '60' },
        { id: 309, name: 'Zul\'Gurub', level: '60' },
        { id: 509, name: 'Ruins of Ahn\'Qiraj', level: '60' },
        { id: 531, name: 'Temple of Ahn\'Qiraj', level: '60' },
        { id: 533, name: 'Naxxramas', level: '60' }
    ];

    // ===================== INIT =====================

    buildInstancePicker();
    updateCreatureVisibility();

    // ===================== INSTANCE PICKER =====================

    function buildInstancePicker() {
        var h = '<div class="instance-category">Dungeons</div>';
        DUNGEONS.forEach(function (d) {
            h += '<button class="instance-chip" data-map="' + d.id + '">' +
                esc(d.name) + ' <span class="inst-level">' + d.level + '</span></button>';
        });
        h += '<div class="instance-category">Raids</div>';
        RAIDS.forEach(function (r) {
            h += '<button class="instance-chip" data-map="' + r.id + '">' +
                esc(r.name) + ' <span class="inst-level">' + r.level + '</span></button>';
        });
        $('#instancePicker').html(h);
    }

    // ===================== FILTER COLLECTION =====================

    function collectFilters() {
        var filter = {};

        // Source (single select)
        var src = $('#sourceChips .source-chip.active').data('value');
        if (src !== undefined && src !== '') filter.lootSource = String(src);

        // Qualities (multi toggle)
        var quals = [];
        $('[data-quality].active').each(function () { quals.push(parseInt($(this).data('quality'))); });
        if (quals.length > 0) filter.qualities = quals;

        // Item class (multi toggle)
        var classes = [];
        $('[data-itemclass].active').each(function () { classes.push(parseInt($(this).data('itemclass'))); });
        if (classes.length > 0) filter.itemClasses = classes;

        // Creature rank (multi toggle)
        var ranks = [];
        $('[data-rank].active').each(function () { ranks.push(parseInt($(this).data('rank'))); });
        if (ranks.length > 0) filter.creatureRanks = ranks;

        // Maps (multi toggle)
        var maps = [];
        $('.instance-chip.active').each(function () { maps.push(parseInt($(this).data('map'))); });
        if (maps.length > 0) filter.mapIds = maps;

        // Level range
        var lvlMin = parseInt($('#filterLevelMin').val());
        var lvlMax = parseInt($('#filterLevelMax').val());
        if (lvlMin > 0) filter.itemLevelMin = lvlMin;
        if (lvlMax > 0) filter.itemLevelMax = lvlMax;

        // Chance range
        var chMin = parseFloat($('#filterChanceMin').val());
        var chMax = parseFloat($('#filterChanceMax').val());
        if (!isNaN(chMin) && chMin >= 0) filter.chanceMin = chMin;
        if (!isNaN(chMax) && chMax > 0) filter.chanceMax = chMax;

        // Guaranteed
        filter.includeGuaranteed = $('#filterIncludeGuaranteed').is(':checked');

        return filter;
    }

    function resetFilters() {
        // Source → default
        $('.source-chip').removeClass('active');
        $('.source-chip[data-value=""]').addClass('active');

        // Quality → Rare + Epic
        $('[data-quality]').removeClass('active');
        $('[data-quality="3"], [data-quality="4"]').addClass('active');

        // Clear all other toggles
        $('[data-itemclass], [data-rank]').removeClass('active');
        $('.instance-chip').removeClass('active');

        // Clear inputs
        $('#filterLevelMin, #filterLevelMax, #filterChanceMin, #filterChanceMax').val('');
        $('#filterIncludeGuaranteed').prop('checked', false);

        updateCreatureVisibility();
        previewData = null;
        $('#summaryCard').hide();
        $('#previewContainer').html(
            '<div class="text-center p-4 text-muted">' +
            '<i class="fa-solid fa-sliders" style="font-size: 28px; margin-bottom: 10px; display: block; color: var(--border);"></i>' +
            'Configure filters, then <strong>Preview Changes</strong></div>'
        );
        $('#previewInfo').text('Set filters and click Preview');
    }

    function updateCreatureVisibility() {
        var src = $('#sourceChips .source-chip.active').data('value');
        var show = src === undefined || src === '' || src === 'creature' || src === 'all';
        if (show) {
            $('.creature-filter').addClass('visible');
        } else {
            $('.creature-filter').removeClass('visible');
        }
    }

    // ===================== PREVIEW =====================

    function runPreview() {
        var filter = collectFilters();

        if (!filter.qualities || filter.qualities.length === 0) {
            showToast('Select at least one item quality', 'error');
            return;
        }

        $('#btnPreview').prop('disabled', true).html('<i class="fa-solid fa-spinner fa-spin"></i> Querying...');
        $('#previewContainer').html('<div class="text-center p-4"><i class="fa-solid fa-spinner fa-spin"></i> Searching loot tables...</div>');

        $.ajax({
            url: '/LootTuner/Preview',
            method: 'POST',
            contentType: 'application/json',
            data: JSON.stringify(filter),
            success: function (data) {
                $('#btnPreview').prop('disabled', false).html('<i class="fa-solid fa-magnifying-glass"></i> Preview Changes');
                previewData = data;
                renderPreview(data);
            },
            error: function () {
                $('#btnPreview').prop('disabled', false).html('<i class="fa-solid fa-magnifying-glass"></i> Preview Changes');
                showToast('Preview query failed', 'error');
            }
        });
    }

    function renderPreview(data) {
        // Summary
        $('#sumEntries').text(data.totalEntries.toLocaleString());
        $('#sumItems').text(data.uniqueItems.toLocaleString());
        $('#sumSources').text(data.uniqueSources.toLocaleString());
        updateApplyLabel();

        // Table breakdown
        if (data.tableBreakdown && data.tableBreakdown.length > 0) {
            var bd = data.tableBreakdown.map(function (t) {
                return '<span class="breakdown-tag"><strong>' + t.count + '</strong> ' + esc(t.table) + '</span>';
            }).join('');
            $('#tableBreakdown').html(bd);
        } else {
            $('#tableBreakdown').html('');
        }

        $('#truncateWarning').toggle(!!data.truncated);
        $('#summaryCard').show();
        $('#previewInfo').text(data.totalEntries.toLocaleString() + ' entries found');

        if (data.rows.length === 0) {
            $('#previewContainer').html('<div class="text-center p-4 text-muted">No loot entries match your filters</div>');
            return;
        }

        // Build table
        var mult = currentMultiplier;
        var h = '<table class="loot-table"><thead><tr>' +
            '<th>Item</th><th>Source</th><th>Table</th>' +
            '<th class="chance-col">Current</th>' +
            '<th class="chance-arrow"></th>' +
            '<th class="chance-col">New</th>' +
            '</tr></thead><tbody>';

        data.rows.forEach(function (row) {
            var iconPath = data.icons[row.displayId] || '/icons/inv_misc_questionmark.png';
            var qualityClass = 'quality-' + row.itemQuality;
            var abs = Math.abs(row.currentChance);
            var isQuest = row.currentChance < 0;
            var newAbs = Math.min(100, abs * mult);
            newAbs = Math.round(newAbs * 10000) / 10000;
            var capped = (abs * mult) > 100;

            var newClass = mult > 1 ? 'increased' : (mult < 1 ? 'decreased' : '');
            if (capped) newClass = 'capped';

            h += '<tr>' +
                '<td><div class="item-cell">' +
                '<img src="' + esc(iconPath) + '" loading="lazy" />' +
                '<span class="' + qualityClass + '">' + esc(row.itemName) + '</span>' +
                '</div></td>' +
                '<td class="source-cell" title="' + escAttr(row.sourceName) + '">' + esc(row.sourceName) + '</td>' +
                '<td><span class="table-tag">' + esc(row.tableKey) + '</span></td>' +
                '<td class="chance-col">' + formatChance(abs) + '%' +
                (isQuest ? ' <span title="Quest chance" style="color:var(--accent);font-size:10px;">Q</span>' : '') +
                '</td>' +
                '<td class="chance-arrow">→</td>' +
                '<td class="chance-col chance-new ' + newClass + '">' + formatChance(newAbs) + '%' +
                (capped ? ' <i class="fa-solid fa-circle-exclamation" title="Capped at 100%" style="font-size:9px;"></i>' : '') +
                '</td>' +
                '</tr>';
        });

        h += '</tbody></table>';
        $('#previewContainer').html(h);
    }

    // ===================== APPLY =====================

    function showApplyConfirm() {
        if (!previewData || previewData.totalEntries === 0) {
            showToast('Run a preview first', 'error');
            return;
        }
        var inverse = Math.round((1 / currentMultiplier) * 10000) / 10000;
        $('#confirmMult').text(currentMultiplier + '×');
        $('#confirmCount').text(previewData.totalEntries.toLocaleString());
        $('#confirmInverse').text(inverse + '×');
        new bootstrap.Modal($('#applyConfirmModal')[0]).show();
    }

    function applyChanges() {
        bootstrap.Modal.getInstance($('#applyConfirmModal')[0]).hide();

        var payload = {
            filter: collectFilters(),
            multiplier: currentMultiplier
        };

        $('#btnApply').prop('disabled', true).html('<i class="fa-solid fa-spinner fa-spin"></i> Applying...');

        $.ajax({
            url: '/LootTuner/Apply',
            method: 'POST',
            contentType: 'application/json',
            data: JSON.stringify(payload),
            success: function (result) {
                $('#btnApply').prop('disabled', false);
                updateApplyButton();
                if (result.success) {
                    showToast(result.totalUpdated.toLocaleString() + ' entries updated with ' + result.multiplier + '× multiplier', 'success');
                    runPreview(); // Refresh to show new values
                } else {
                    showToast('Apply failed: ' + (result.error || 'Unknown error'), 'error');
                }
            },
            error: function () {
                $('#btnApply').prop('disabled', false);
                updateApplyButton();
                showToast('Apply failed — server error', 'error');
            }
        });
    }

    function updateApplyButton() {
        var count = previewData ? previewData.totalEntries : 0;
        $('#btnApply').html('<i class="fa-solid fa-bolt"></i> <span>Apply <strong>' + currentMultiplier + '×</strong> to <strong>' + count.toLocaleString() + '</strong> entries</span>');
    }

    function updateApplyLabel() {
        $('#applyMultLabel').text(currentMultiplier + '×');
        if (previewData) $('#applyCountLabel').text(previewData.totalEntries.toLocaleString());
    }

    // ===================== MULTIPLIER =====================

    function setMultiplier(val) {
        currentMultiplier = val;
        $('#multiplierValue').val(val);
        $('#multiplierSlider').val(Math.min(10, Math.max(0.1, val)));
        $('.mult-btn').removeClass('active');
        $('.mult-btn').each(function () {
            if (parseFloat($(this).data('mult')) === val) $(this).addClass('active');
        });
        updateApplyLabel();
        if (previewData) renderPreview(previewData);
    }

    // ===================== PRESETS =====================

    var PRESETS = {
        blues3x: function () {
            resetFilters();
            $('[data-quality]').removeClass('active');
            $('[data-quality="3"]').addClass('active');
            setMultiplier(3);
        },
        epics5x: function () {
            resetFilters();
            $('[data-quality]').removeClass('active');
            $('[data-quality="4"]').addClass('active');
            setMultiplier(5);
        },
        bossloot2x: function () {
            resetFilters();
            $('.source-chip').removeClass('active');
            $('.source-chip[data-value="creature"]').addClass('active');
            updateCreatureVisibility();
            // All qualities
            $('[data-quality]').addClass('active');
            $('[data-rank="3"]').addClass('active');
            setMultiplier(2);
        },
        dungeon3x: function () {
            resetFilters();
            $('.source-chip').removeClass('active');
            $('.source-chip[data-value="creature"]').addClass('active');
            updateCreatureVisibility();
            // Select all dungeon maps
            DUNGEONS.forEach(function (d) {
                $('.instance-chip[data-map="' + d.id + '"]').addClass('active');
            });
            setMultiplier(3);
        },
        consumables2x: function () {
            resetFilters();
            $('[data-quality]').addClass('active');
            $('[data-itemclass="0"]').addClass('active');
            setMultiplier(2);
        },
        endgame3x: function () {
            resetFilters();
            $('#filterLevelMin').val(55);
            $('#filterLevelMax').val(60);
            setMultiplier(3);
        }
    };

    // ===================== HELPERS =====================

    function formatChance(val) {
        if (val === 0) return '0';
        if (val >= 10) return val.toFixed(1);
        if (val >= 1) return val.toFixed(2);
        if (val >= 0.1) return val.toFixed(3);
        return val.toFixed(4);
    }

    function esc(text) {
        if (text == null) return '';
        var div = document.createElement('div');
        div.textContent = String(text);
        return div.innerHTML;
    }

    function escAttr(text) {
        if (text == null) return '';
        return String(text).replace(/&/g, '&amp;').replace(/"/g, '&quot;');
    }

    function showToast(msg, type) {
        var el = $('<div class="loot-toast ' + type + '">' + esc(msg) + '</div>');
        $('body').append(el);
        setTimeout(function () { el.fadeOut(300, function () { el.remove(); }); }, 4000);
    }

    // ===================== EVENTS =====================

    // Source chips — single select (radio behavior)
    $(document).on('click', '.source-chip', function () {
        $('.source-chip').removeClass('active');
        $(this).addClass('active');
        updateCreatureVisibility();
    });

    // Toggle chips — multi select (checkbox behavior)
    $(document).on('click', '.toggle-chip', function () {
        $(this).toggleClass('active');
    });

    // Instance chips — multi select
    $(document).on('click', '.instance-chip', function () {
        $(this).toggleClass('active');
    });

    // Select All Dungeons
    $('#btnSelectAllDungeons').on('click', function () {
        DUNGEONS.forEach(function (d) {
            $('.instance-chip[data-map="' + d.id + '"]').addClass('active');
        });
    });

    // Select All Raids
    $('#btnSelectAllRaids').on('click', function () {
        RAIDS.forEach(function (r) {
            $('.instance-chip[data-map="' + r.id + '"]').addClass('active');
        });
    });

    // Clear maps
    $('#btnClearMaps').on('click', function () {
        $('.instance-chip').removeClass('active');
    });

    // Multiplier preset buttons
    $('.mult-btn').on('click', function () {
        setMultiplier(parseFloat($(this).data('mult')));
    });

    // Multiplier text input
    $('#multiplierValue').on('input', function () {
        var val = parseFloat($(this).val());
        if (!isNaN(val) && val > 0) {
            currentMultiplier = val;
            $('#multiplierSlider').val(Math.min(10, Math.max(0.1, val)));
            $('.mult-btn').removeClass('active');
            $('.mult-btn').each(function () {
                if (parseFloat($(this).data('mult')) === val) $(this).addClass('active');
            });
            updateApplyLabel();
            if (previewData) renderPreview(previewData);
        }
    });

    // Multiplier slider
    $('#multiplierSlider').on('input', function () {
        var val = parseFloat($(this).val());
        // Snap to nice values
        var snaps = [0.25, 0.5, 1, 1.5, 2, 3, 5, 10];
        for (var i = 0; i < snaps.length; i++) {
            if (Math.abs(val - snaps[i]) < 0.15) { val = snaps[i]; break; }
        }
        val = Math.round(val * 10) / 10;
        currentMultiplier = val;
        $('#multiplierValue').val(val);
        $('.mult-btn').removeClass('active');
        $('.mult-btn').each(function () {
            if (parseFloat($(this).data('mult')) === val) $(this).addClass('active');
        });
        updateApplyLabel();
        if (previewData) renderPreview(previewData);
    });

    // Preview
    $('#btnPreview').on('click', runPreview);

    // Reset
    $('#btnResetFilters').on('click', resetFilters);

    // Apply
    $('#btnApply').on('click', showApplyConfirm);
    $('#btnConfirmApply').on('click', applyChanges);

    // Presets
    $(document).on('click', '.preset-btn', function () {
        var key = $(this).data('preset');
        if (PRESETS[key]) {
            PRESETS[key]();
            setTimeout(runPreview, 100);
        }
    });

    // ===================== CHANGELOG =====================

    $('#btnViewChangelog').on('click', function () {
        var modal = new bootstrap.Modal($('#changelogModal')[0]);
        $('#changelogBody').html('<div class="text-center p-4"><i class="fa-solid fa-spinner fa-spin"></i> Loading changelog...</div>');
        modal.show();

        $.ajax({
            url: '/LootTuner/Changelog',
            method: 'GET',
            success: function (data) {
                renderChangelog(data);
            },
            error: function () {
                $('#changelogBody').html('<div class="changelog-empty"><i class="fa-solid fa-circle-exclamation"></i>Failed to load changelog</div>');
            }
        });
    });

    function renderChangelog(data) {
        if (!data.initialized) {
            $('#changelogBody').html(
                '<div class="changelog-empty">' +
                '<i class="fa-solid fa-database"></i>' +
                'OG baseline has not been initialized yet.<br>' +
                '<span style="font-size:12px;">Run baseline initialization from the Settings page first.</span></div>'
            );
            return;
        }

        if (data.totalChanged === 0) {
            $('#changelogBody').html(
                '<div class="changelog-empty">' +
                '<i class="fa-solid fa-check-circle" style="color: var(--status-online);"></i>' +
                'All loot tables match the original baseline.<br>' +
                '<span style="font-size:12px;">No drop rates have been modified.</span></div>'
            );
            return;
        }

        // Summary
        var h = '<div class="changelog-summary">' +
            '<div><div class="changelog-count">' + data.totalChanged.toLocaleString() + '</div>' +
            '<div class="changelog-count-label">Modified Entries</div></div>' +
            '<div class="changelog-breakdown">';

        if (data.tableBreakdown) {
            data.tableBreakdown.forEach(function (t) {
                h += '<span class="breakdown-tag"><strong>' + t.count + '</strong> ' + esc(t.table) + '</span>';
            });
        }
        h += '</div></div>';

        // Table
        h += '<div id="changelogTableBody"><table class="loot-table"><thead><tr>' +
            '<th>Item</th><th>Table</th>' +
            '<th class="chance-col">Original</th>' +
            '<th class="chance-arrow"></th>' +
            '<th class="chance-col">Current</th>' +
            '<th class="chance-col">Delta</th>' +
            '</tr></thead><tbody>';

        data.changes.forEach(function (row) {
            var iconPath = data.icons[row.displayId] || '/icons/inv_misc_questionmark.png';
            var qualityClass = 'quality-' + row.itemQuality;
            var origAbs = Math.abs(row.originalChance);
            var curAbs = Math.abs(row.currentChance);
            var delta = curAbs - origAbs;
            var deltaClass = delta > 0 ? 'delta-up' : 'delta-down';
            var deltaSign = delta > 0 ? '+' : '';
            var mult = origAbs > 0 ? (curAbs / origAbs) : 0;
            var multStr = origAbs > 0 ? ' (' + mult.toFixed(2) + '×)' : '';

            h += '<tr>' +
                '<td><div class="item-cell">' +
                '<img src="' + esc(iconPath) + '" loading="lazy" />' +
                '<span class="' + qualityClass + '">' + esc(row.itemName) + '</span>' +
                '</div></td>' +
                '<td><span class="table-tag">' + esc(row.tableKey) + '</span></td>' +
                '<td class="chance-col">' + formatChance(origAbs) + '%</td>' +
                '<td class="chance-arrow">→</td>' +
                '<td class="chance-col" style="font-weight:700;">' + formatChance(curAbs) + '%</td>' +
                '<td class="chance-col ' + deltaClass + '">' + deltaSign + formatChance(Math.abs(delta)) + '%' +
                '<span style="font-size:10px;opacity:0.7;">' + multStr + '</span></td>' +
                '</tr>';
        });

        h += '</tbody></table></div>';
        $('#changelogBody').html(h);
    }

    // ===================== RESET TO BASELINE =====================

    $('#btnResetBaseline').on('click', function () {
        new bootstrap.Modal($('#resetBaselineModal')[0]).show();
    });

    $('#btnConfirmReset').on('click', function () {
        bootstrap.Modal.getInstance($('#resetBaselineModal')[0]).hide();

        $('#btnResetBaseline').prop('disabled', true).html('<i class="fa-solid fa-spinner fa-spin"></i> Resetting...');

        $.ajax({
            url: '/LootTuner/ResetToBaseline',
            method: 'POST',
            contentType: 'application/json',
            data: '{}',
            success: function (result) {
                $('#btnResetBaseline').prop('disabled', false).html('<i class="fa-solid fa-rotate-left"></i> Reset All to Original');
                if (result.success) {
                    showToast(result.totalRestored.toLocaleString() + ' rows restored to original across ' + result.tables.length + ' tables', 'success');
                    // Clear preview since data changed
                    previewData = null;
                    $('#summaryCard').hide();
                    $('#previewContainer').html(
                        '<div class="text-center p-4 text-muted">' +
                        '<i class="fa-solid fa-check-circle" style="font-size: 28px; margin-bottom: 10px; display: block; color: var(--status-online);"></i>' +
                        'All loot tables reset to original values</div>'
                    );
                    $('#previewInfo').text('Loot tables reset');
                } else {
                    showToast('Reset failed: ' + (result.error || 'Unknown error'), 'error');
                }
            },
            error: function () {
                $('#btnResetBaseline').prop('disabled', false).html('<i class="fa-solid fa-rotate-left"></i> Reset All to Original');
                showToast('Reset failed — server error', 'error');
            }
        });
    });

});
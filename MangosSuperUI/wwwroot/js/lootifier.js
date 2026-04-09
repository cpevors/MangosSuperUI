// MangosSuperUI — ARPG Lootifier JS (v3 — tier-quota + prefix/suffix + spell-effect items)

$(function () {

    var meta = null;
    var selectedCreature = null;
    var lootTreeData = null;
    var previewData = null;
    var selectedItems = {};
    var rollbackCreature = 0;
    var batchData = null;
    var batchSelectedItems = {}; // creatureEntry → { itemEntry: true }
    var currentMode = 'single'; // 'single' or 'batch'

    var RANK_NAMES = { 0: 'Normal', 1: 'Elite', 2: 'Rare Elite', 3: 'Boss', 4: 'Rare' };
    var QUALITY_NAMES = ['Poor', 'Common', 'Uncommon', 'Rare', 'Epic', 'Legendary'];

    // ===================== INIT =====================

    BaselineSystem.checkStatus(function () {
        BaselineSystem.renderWarningBanner('#baselineWarning');
    });

    $.getJSON('/Lootifier/Meta', function (data) {
        meta = data;
        renderNamingTiers();
        buildBatchFilters();
    });

    // ===================== MODE TABS =====================

    function switchMode(mode) {
        currentMode = mode;
        $('.lf-mode-tab').removeClass('active');
        $('.lf-mode-tab[data-mode="' + mode + '"]').addClass('active');

        if (mode === 'single') {
            $('#singlePanel').show();
            $('#batchPanel').hide();
        } else {
            $('#singlePanel').hide();
            $('#batchPanel').show();
        }

        // Reset preview
        previewData = null;
        batchData = null;
        $('#previewContainer').html('<div class="lf-empty-state"><i class="fa-solid fa-dragon"></i>' +
            (mode === 'single' ? 'Search for a creature, select items, then generate variants' : 'Configure batch filters and scan for items') +
            '</div>');
        $('#previewInfo').text(mode === 'single' ? 'Select a creature and items' : 'Configure filters and scan');
        $('#commitPanel').hide();
        $('#batchSamplePanel').hide();
        $('#batchSampleContainer').hide().html('');
    }

    $(document).on('click', '.lf-mode-tab', function () {
        switchMode($(this).data('mode'));
    });

    // ===================== CREATURE SEARCH =====================

    function searchCreature() {
        var q = $('#creatureSearch').val().trim();
        if (q.length < 2) return;

        $('#btnSearchCreature').prop('disabled', true).html('<i class="fa-solid fa-spinner fa-spin"></i>');

        $.getJSON('/Lootifier/SearchCreature?q=' + encodeURIComponent(q), function (data) {
            $('#btnSearchCreature').prop('disabled', false).html('<i class="fa-solid fa-magnifying-glass"></i>');

            if (!data.results || data.results.length === 0) {
                $('#searchResults').show().html('<div class="lf-empty-state" style="padding:12px;font-size:12px;">No creatures found</div>');
                return;
            }

            var h = '';
            data.results.forEach(function (c) {
                var rankName = RANK_NAMES[c.rank] || 'Unknown';
                h += '<div class="lf-search-item" data-entry="' + c.entry + '">' +
                    '<div><span class="lf-sr-name">' + esc(c.name) + '</span></div>' +
                    '<div class="lf-sr-meta">' + rankName + ' &middot; Lv ' + c.level_min + '-' + c.level_max + '</div>' +
                    '</div>';
            });
            $('#searchResults').show().html(h);
        });
    }

    function selectCreature(entry) {
        $('#searchResults').hide();
        $('#selectedCreature').show();
        $('#lootTreeContainer').show().find('#lootTree').html('<div class="text-center p-4"><i class="fa-solid fa-spinner fa-spin"></i> Loading loot tree...</div>');
        $('#rulesetPanel').show();
        $('#btnGenerate').prop('disabled', true);

        $.getJSON('/Lootifier/LootTree?creatureEntry=' + entry, function (data) {
            if (!data.success) {
                showToast('Failed to load loot tree: ' + (data.error || ''), 'error');
                return;
            }

            lootTreeData = data;
            selectedCreature = data.creature;
            selectedItems = {};

            var c = data.creature;
            $('#selCreatureName').text(c.name);
            var rankName = RANK_NAMES[c.rank] || 'Unknown';
            var rankClass = c.rank === 3 ? 'boss' : (c.rank === 1 ? 'elite' : 'normal');
            $('#selCreatureRank').text(rankName).attr('class', 'lf-rank-badge ' + rankClass);
            $('#selCreatureLevel').text('Lv ' + c.level_min + '-' + c.level_max);
            $('#selCreatureLootId').text(c.loot_id);

            renderLootTree(data);
        });
    }

    // ===================== LOOT TREE RENDER =====================

    function renderLootTree(data) {
        var h = '';
        var icons = data.icons || {};

        if (data.directItems && data.directItems.length > 0) {
            h += '<div class="lf-loot-section">Direct Drops</div>';
            data.directItems.forEach(function (item) {
                h += renderLootRow(item, icons, true);
            });
        }

        if (data.referenceGroups && data.referenceGroups.length > 0) {
            data.referenceGroups.forEach(function (rg) {
                h += '<div class="lf-loot-section">Reference #' + rg.refEntry +
                    ' <span style="float:right;font-weight:400;">' + formatChance(rg.pointerChance) + '% roll</span></div>';
                rg.items.forEach(function (item) {
                    h += renderLootRow(item, icons, false);
                });
            });
        }

        if (h === '') {
            h = '<div class="lf-empty-state" style="padding:16px;">No loot data found</div>';
        }

        $('#lootTree').html(h);
        updateGenerateButton();

        // Populate legendary item picker with selectable items
        populateLegendaryPicker();
    }

    function renderLootRow(item, icons, isDirect) {
        var iconPath = icons[item.displayId] || '/icons/inv_misc_questionmark.png';
        var qualClass = 'quality-' + item.quality;
        var hasStats = item.totalStats > 0;
        var hasSpells = item.hasSpellEffects;
        var isLootifiable = hasStats || hasSpells;
        var noStatsClass = isLootifiable ? '' : ' no-stats';
        var isSelected = selectedItems[item.itemEntry] ? ' selected' : '';

        var chanceStr = item.chance === 0 ? 'equal' : formatChance(Math.abs(item.chance)) + '%';

        var familyBadge;
        if (hasStats) {
            familyBadge = '<span class="lf-item-family">' + esc(item.detectedFamily) + '</span>';
        } else if (hasSpells) {
            familyBadge = '<span class="lf-item-family" style="color:var(--accent);"><i class="fa-solid fa-bolt" style="font-size:8px;"></i> spell</span>';
        } else {
            familyBadge = '<span class="lf-item-family" style="color:var(--status-error);">no stats</span>';
        }

        var budgetStr = hasStats ? Math.round(item.weightedBudget) + 'wp' : (hasSpells ? 'spell' : '—');

        return '<div class="lf-loot-row' + noStatsClass + isSelected + '" data-item="' + item.itemEntry + '" data-has-stats="' + (isLootifiable ? '1' : '0') + '">' +
            (isLootifiable && !isDirect ? '<input type="checkbox" class="lf-loot-check" ' + (isSelected ? 'checked' : '') + ' />' : '<span style="width:14px;"></span>') +
            '<img src="' + esc(iconPath) + '" />' +
            '<span class="lf-item-name ' + qualClass + '">' + esc(item.itemName) + '</span>' +
            familyBadge +
            '<span class="lf-item-budget">' + budgetStr + '</span>' +
            '<span class="lf-item-chance">' + chanceStr + '</span>' +
            '</div>';
    }

    // ===================== NAMING TIERS =====================

    function renderNamingTiers() {
        if (!meta || !meta.defaultNamingTiers) return;
        var h = '';
        meta.defaultNamingTiers.forEach(function (t, i) {
            h += '<div class="lf-tier-row">' +
                '<span class="lf-tier-range">' + t.minPct + '–' + t.maxPct + '%</span>' +
                '<select class="form-input lf-tier-position" data-tier="' + i + '" style="width:72px;padding:3px 6px;font-size:11px;">' +
                '<option value="prefix"' + (t.position === 'prefix' ? ' selected' : '') + '>Prefix</option>' +
                '<option value="suffix"' + (t.position === 'suffix' ? ' selected' : '') + '>Suffix</option>' +
                '</select>' +
                '<input type="text" class="form-input lf-tier-input" data-tier="' + i + '" value="' + esc(t.label) + '" />' +
                '</div>';
        });
        // Render into both single and batch panels
        $('#namingTiers').html(h);
        $('#batchNamingTiers').html(h.replace(/data-tier="/g, 'data-batch-tier="'));
    }

    function collectRuleset() {
        // Determine which panel is active to read tier inputs from
        var tierPrefix = currentMode === 'batch' ? 'batch-tier' : 'tier';
        var tiers = [];
        if (meta && meta.defaultNamingTiers) {
            meta.defaultNamingTiers.forEach(function (t, i) {
                var labelInput = $('input.lf-tier-input[data-' + tierPrefix + '="' + i + '"]');
                var posSelect = $('select.lf-tier-position[data-' + tierPrefix + '="' + i + '"]');
                // Fallback to single mode inputs if batch inputs not found
                if (labelInput.length === 0) {
                    labelInput = $('input.lf-tier-input[data-tier="' + i + '"]');
                    posSelect = $('select.lf-tier-position[data-tier="' + i + '"]');
                }
                tiers.push({
                    minPct: t.minPct,
                    maxPct: t.maxPct,
                    label: labelInput.val() || '',
                    position: posSelect.val() || 'suffix'
                });
            });
        }

        // Read from correct panel: batch shared inputs sync to single IDs
        var budgetCeiling, variantsPerItem, allowNew, maxAffix;
        if (currentMode === 'batch') {
            budgetCeiling = parseFloat($('.lf-rs-shared[data-target="rsBudgetCeiling"]').val()) || 35;
            variantsPerItem = parseInt($('.lf-rs-shared[data-target="rsVariantsPerItem"]').val()) || 10;
            allowNew = $('.lf-rs-shared-check[data-target="rsAllowNewAffixes"]').is(':checked');
            maxAffix = parseInt($('.lf-rs-shared[data-target="rsMaxAffixChange"]').val()) || 1;
        } else {
            budgetCeiling = parseFloat($('#rsBudgetCeiling').val()) || 35;
            variantsPerItem = parseInt($('#rsVariantsPerItem').val()) || 10;
            allowNew = $('#rsAllowNewAffixes').is(':checked');
            maxAffix = parseInt($('#rsMaxAffixChange').val()) || 1;
        }

        return {
            budgetCeilingPct: budgetCeiling,
            variantsPerItem: variantsPerItem,
            allowNewAffixes: allowNew,
            maxAffixCountChange: maxAffix,
            dropChanceStrategy: 'preserve',
            namingTiers: tiers,
            // Legendary
            generateLegendary: currentMode === 'batch'
                ? $('.lf-batch-legendary-toggle').is(':checked')
                : $('#rsLegendaryToggle').is(':checked'),
            legendaryDropPct: currentMode === 'batch'
                ? (parseFloat($('.lf-batch-leg-drop').val()) || 0.2)
                : (parseFloat($('#rsLegendaryDropPct').val()) || 0.2),
            legendarySuffixMelee: currentMode === 'batch'
                ? ($('.lf-batch-leg-melee').val() || 'of Destruction')
                : ($('#rsLegSuffixMelee').val() || 'of Destruction'),
            legendarySuffixRanged: currentMode === 'batch'
                ? ($('.lf-batch-leg-ranged').val() || 'of the Hunt')
                : ($('#rsLegSuffixRanged').val() || 'of the Hunt'),
            legendarySuffixCaster: currentMode === 'batch'
                ? ($('.lf-batch-leg-caster').val() || 'of Arcana')
                : ($('#rsLegSuffixCaster').val() || 'of Arcana'),
            legendaryItemEntry: parseInt($('#rsLegendaryItem').val()) || 0
        };
    }

    // ===================== BATCH FILTERS =====================

    function buildBatchFilters() {
        if (!meta) return;

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
        $('#batchInstancePicker').html(h);
    }

    function collectBatchFilters() {
        var filter = {};

        var quals = [];
        $('#batchPanel [data-quality].active').each(function () { quals.push(parseInt($(this).data('quality'))); });
        if (quals.length > 0) filter.qualities = quals;

        var ranks = [];
        $('#batchPanel [data-rank].active').each(function () { ranks.push(parseInt($(this).data('rank'))); });
        if (ranks.length > 0) filter.creatureRanks = ranks;

        var maps = [];
        $('#batchPanel .instance-chip.active').each(function () { maps.push(parseInt($(this).data('map'))); });
        if (maps.length > 0) filter.mapIds = maps;

        var lvlMin = parseInt($('#batchLevelMin').val());
        var lvlMax = parseInt($('#batchLevelMax').val());
        if (lvlMin > 0) filter.levelMin = lvlMin;
        if (lvlMax > 0) filter.levelMax = lvlMax;

        filter.ruleset = collectRuleset();
        return filter;
    }

    // ===================== BATCH SCAN =====================

    function batchScan() {
        var filter = collectBatchFilters();

        if (!filter.qualities || filter.qualities.length === 0) {
            showToast('Select at least one quality', 'error');
            return;
        }

        $('#btnBatchScan').prop('disabled', true).html('<i class="fa-solid fa-spinner fa-spin"></i> Scanning...');
        $('#previewContainer').html('<div class="text-center p-4"><i class="fa-solid fa-spinner fa-spin"></i> Scanning loot tables...</div>');
        $('#commitPanel').hide();

        $.ajax({
            url: '/Lootifier/BatchPreview',
            method: 'POST',
            contentType: 'application/json',
            data: JSON.stringify(filter),
            success: function (data) {
                $('#btnBatchScan').prop('disabled', false).html('<i class="fa-solid fa-magnifying-glass"></i> Scan Loot Tables');

                if (!data.success) {
                    showToast('Scan failed: ' + (data.error || ''), 'error');
                    return;
                }

                batchData = data;
                batchSelectedItems = {};

                data.creatures.forEach(function (c) {
                    batchSelectedItems[c.creatureEntry] = {};
                    c.items.forEach(function (it) {
                        batchSelectedItems[c.creatureEntry][it.itemEntry] = true;
                    });
                });

                renderBatchPreview(data);
            },
            error: function () {
                $('#btnBatchScan').prop('disabled', false).html('<i class="fa-solid fa-magnifying-glass"></i> Scan Loot Tables');
                showToast('Scan failed', 'error');
            }
        });
    }

    function renderBatchPreview(data) {
        if (!data.creatures || data.creatures.length === 0) {
            $('#previewContainer').html('<div class="lf-empty-state">No matching items found</div>');
            $('#previewInfo').text('No results');
            return;
        }

        var truncNote = data.truncated ? '<div style="padding:8px 14px;font-size:11px;color:var(--status-warning);"><i class="fa-solid fa-triangle-exclamation"></i> Showing first 500 rows — results truncated</div>' : '';

        var h = truncNote;
        var totalItems = 0;

        data.creatures.forEach(function (c) {
            var rankName = RANK_NAMES[c.creatureRank] || '';
            var icons = data.icons || {};

            h += '<div class="lf-batch-creature">';
            h += '<div class="lf-batch-creature-header" data-creature="' + c.creatureEntry + '">' +
                '<input type="checkbox" class="lf-batch-creature-check" data-creature="' + c.creatureEntry + '" checked />' +
                '<span class="lf-batch-creature-name">' + esc(c.creatureName) + '</span>' +
                '<span class="lf-rank-badge ' + (c.creatureRank === 3 ? 'boss' : (c.creatureRank === 1 ? 'elite' : 'normal')) + '">' + rankName + '</span>' +
                '<span class="text-muted" style="font-size:11px;">Lv ' + c.levelMin + '-' + c.levelMax + '</span>' +
                '<span class="lf-batch-item-count">' + c.items.length + ' items</span>' +
                '</div>';

            c.items.forEach(function (it) {
                var iconPath = icons[it.displayId] || '/icons/inv_misc_questionmark.png';
                var qualClass = 'quality-' + it.quality;
                totalItems++;

                h += '<div class="lf-batch-item" data-creature="' + c.creatureEntry + '" data-item="' + it.itemEntry + '">' +
                    '<input type="checkbox" class="lf-batch-item-check" data-creature="' + c.creatureEntry + '" data-item="' + it.itemEntry + '" checked />' +
                    '<img src="' + esc(iconPath) + '" style="width:18px;height:18px;image-rendering:pixelated;border-radius:2px;" />' +
                    '<span class="' + qualClass + '" style="flex:1;font-size:12px;">' + esc(it.itemName) + '</span>' +
                    '<span style="font-family:monospace;font-size:11px;color:var(--text-muted);">Lv' + it.requiredLevel + '</span>' +
                    '</div>';
            });

            h += '</div>';
        });

        $('#previewContainer').html(h);
        $('#previewInfo').text(totalItems + ' items across ' + data.creatures.length + ' creatures');

        var variantsPerItem = parseInt($('#rsVariantsPerItem').val()) || 10;
        $('#commitItemCount').text(totalItems * variantsPerItem);
        $('#commitLootRows').text('~' + totalItems * variantsPerItem);
        $('#commitBaseItems').text(totalItems);

        // Show sample preview option AND commit — both available immediately
        $('#batchSamplePanel').show();
        $('#batchSampleContainer').html('');
        $('#commitPanel').show();
    }

    // ===================== BATCH SAMPLE PREVIEW =====================

    function batchSamplePreview() {
        if (!batchData || !batchData.creatures) return;

        // Pick up to 3 representative items: try for one physical, one caster, one spell-effect
        var allItems = [];
        batchData.creatures.forEach(function (c) {
            var sel = batchSelectedItems[c.creatureEntry];
            if (!sel) return;
            c.items.forEach(function (it) {
                if (sel[it.itemEntry]) allItems.push(it);
            });
        });

        if (allItems.length === 0) {
            showToast('No items selected', 'error');
            return;
        }

        // Pick diverse samples (up to 3)
        var samples = pickSampleItems(allItems, 3);
        var sampleEntries = samples.map(function (it) { return it.itemEntry; });

        $('#btnBatchSample').prop('disabled', true).html('<i class="fa-solid fa-spinner fa-spin"></i> Generating sample...');

        // Pick a creature entry for legendary preview context (first creature in scan)
        var sampleCreatureEntry = batchData.creatures.length > 0 ? batchData.creatures[0].creatureEntry : 0;

        $.ajax({
            url: '/Lootifier/BatchSamplePreview',
            method: 'POST',
            contentType: 'application/json',
            data: JSON.stringify({ creatureEntry: sampleCreatureEntry, itemEntries: sampleEntries, ruleset: collectRuleset() }),
            success: function (data) {
                $('#btnBatchSample').prop('disabled', false).html('<i class="fa-solid fa-eye"></i> Preview Sample Variants');

                if (!data.success || !data.items || data.items.length === 0) {
                    showToast('Sample preview failed', 'error');
                    return;
                }

                renderBatchSamplePreview(data);
            },
            error: function () {
                $('#btnBatchSample').prop('disabled', false).html('<i class="fa-solid fa-eye"></i> Preview Sample Variants');
                showToast('Sample preview failed', 'error');
            }
        });
    }

    function pickSampleItems(allItems, maxSamples) {
        // Try to get variety: different quality levels, different item types
        var byQuality = {};
        allItems.forEach(function (it) {
            if (!byQuality[it.quality]) byQuality[it.quality] = [];
            byQuality[it.quality].push(it);
        });

        var picked = [];
        var qualKeys = Object.keys(byQuality).sort(function (a, b) { return b - a; }); // highest quality first

        // Pick one from each quality tier
        for (var q = 0; q < qualKeys.length && picked.length < maxSamples; q++) {
            var pool = byQuality[qualKeys[q]];
            var idx = Math.floor(Math.random() * pool.length);
            picked.push(pool[idx]);
        }

        // Fill remaining slots randomly
        while (picked.length < maxSamples && picked.length < allItems.length) {
            var idx = Math.floor(Math.random() * allItems.length);
            var candidate = allItems[idx];
            if (!picked.find(function (p) { return p.itemEntry === candidate.itemEntry; })) {
                picked.push(candidate);
            }
        }

        return picked;
    }

    function renderBatchSamplePreview(data) {
        var h = '<div style="padding:10px 14px;font-size:12px;color:var(--accent);font-weight:600;border-bottom:1px solid var(--border-light);">' +
            '<i class="fa-solid fa-flask"></i> Sample Preview — ' + data.items.length + ' representative items' +
            '</div>';

        data.items.forEach(function (itemGroup) {
            var base = itemGroup.baseItem;
            var analysis = itemGroup.analysis;
            var variants = itemGroup.variants;

            var iconPath = base.iconPath || '/icons/inv_misc_questionmark.png';
            var qualClass = 'quality-' + base.quality;

            var spellBadge = '';
            if (analysis.hasSpellEffects && analysis.spellEffects.length > 0) {
                var spellNames = analysis.spellEffects.map(function (se) { return se.triggerName + ' #' + se.spellId; });
                spellBadge = ' <span class="lf-spell-badge"><i class="fa-solid fa-bolt"></i> ' + spellNames.join(', ') + '</span>';
            }

            var analysisStr = analysis.totalStats > 0
                ? 'Base: ' + analysis.totalStats + ' stats / ' + Math.round(analysis.weightedBudget) + 'wp / ' + esc(analysis.detectedFamily)
                : 'Spell-effect item';

            h += '<div class="lf-preview-group">';
            h += '<div class="lf-preview-header">' +
                '<img src="' + esc(iconPath) + '" />' +
                '<span class="' + qualClass + '">' + esc(base.name) + '</span>' +
                spellBadge +
                '<span class="lf-preview-analysis">' + analysisStr + '</span>' +
                '</div>';

            h += '<table class="lf-variant-table"><thead><tr>' +
                '<th>#</th><th>Name</th><th>Budget</th><th>Tier</th><th>Stats</th>' +
                '</tr></thead><tbody>';

            variants.forEach(function (v, idx) {
                var tierClass = getTierClass(v.tierLabel);
                var budgetColor = getBudgetColor(v.budgetPct);

                h += '<tr>' +
                    '<td style="color:var(--text-muted);font-size:11px;">' + (idx + 1) + '</td>' +
                    '<td style="font-weight:500;">' + esc(v.name) + '</td>' +
                    '<td><span class="lf-budget-bar"><span class="lf-budget-fill" style="width:' + Math.min(100, v.budgetPct) + '%;background:' + budgetColor + ';"></span></span>' +
                    '<span style="font-family:monospace;font-size:11px;">' + v.budgetPct + '%</span></td>' +
                    '<td><span class="lf-tier-badge ' + tierClass + '">' + esc(v.tierLabel || '—') + '</span></td>' +
                    '<td>' + renderStatPills(v.stats, analysis.presentStatTypes) + '</td>' +
                    '</tr>';
            });

            h += '</tbody></table></div>';
        });

        // Legendary preview card
        if (data.legendary) {
            h += renderLegendaryCard(data.legendary);
        }

        $('#batchSampleContainer').html(h).show();
    }

    // ===================== GENERATE PREVIEW (single) =====================

    function generatePreview() {
        var entries = Object.keys(selectedItems).filter(function (k) { return selectedItems[k]; }).map(Number);
        if (entries.length === 0) {
            showToast('Select at least one item', 'error');
            return;
        }

        var ruleset = collectRuleset();

        $('#btnGenerate').prop('disabled', true).html('<i class="fa-solid fa-spinner fa-spin"></i> Generating...');
        $('#previewContainer').html('<div class="text-center p-4"><i class="fa-solid fa-spinner fa-spin"></i> Rolling variants...</div>');
        $('#commitPanel').hide();

        $.ajax({
            url: '/Lootifier/GeneratePreview',
            method: 'POST',
            contentType: 'application/json',
            data: JSON.stringify({ creatureEntry: selectedCreature ? selectedCreature.entry : 0, itemEntries: entries, ruleset: ruleset }),
            success: function (data) {
                $('#btnGenerate').prop('disabled', false).html('<i class="fa-solid fa-dice-d20"></i> Generate Variants Preview');

                if (!data.success) {
                    showToast('Generation failed: ' + (data.error || ''), 'error');
                    return;
                }

                previewData = data;
                renderSinglePreview(data);
            },
            error: function () {
                $('#btnGenerate').prop('disabled', false).html('<i class="fa-solid fa-dice-d20"></i> Generate Variants Preview');
                showToast('Generation failed', 'error');
            }
        });
    }

    function renderSinglePreview(data) {
        if (!data.items || data.items.length === 0) {
            $('#previewContainer').html('<div class="lf-empty-state">No variants generated. Selected items may have no rollable stats or spell effects.</div>');
            return;
        }

        var totalVariants = 0;
        var h = '';

        data.items.forEach(function (itemGroup) {
            var base = itemGroup.baseItem;
            var analysis = itemGroup.analysis;
            var variants = itemGroup.variants;
            totalVariants += variants.length;

            var iconPath = lootTreeData && lootTreeData.icons ? (lootTreeData.icons[base.displayId] || '/icons/inv_misc_questionmark.png') : '/icons/inv_misc_questionmark.png';
            var qualClass = 'quality-' + base.quality;

            // Show spell effects in header if present
            var spellBadge = '';
            if (analysis.hasSpellEffects && analysis.spellEffects.length > 0) {
                var spellNames = analysis.spellEffects.map(function (se) { return se.triggerName + ' #' + se.spellId; });
                spellBadge = ' <span class="lf-spell-badge"><i class="fa-solid fa-bolt"></i> ' + spellNames.join(', ') + '</span>';
            }

            var analysisStr = analysis.totalStats > 0
                ? 'Base: ' + analysis.totalStats + ' stats / ' + Math.round(analysis.weightedBudget) + 'wp / ' + esc(analysis.detectedFamily)
                : 'Spell-effect item';

            h += '<div class="lf-preview-group">';
            h += '<div class="lf-preview-header">' +
                '<img src="' + esc(iconPath) + '" />' +
                '<span class="' + qualClass + '">' + esc(base.name) + '</span>' +
                spellBadge +
                '<span class="lf-preview-analysis">' + analysisStr + '</span>' +
                '</div>';

            h += '<table class="lf-variant-table"><thead><tr>' +
                '<th>#</th><th>Name</th><th>Budget</th><th>Tier</th><th>Stats</th>' +
                '</tr></thead><tbody>';

            variants.forEach(function (v, idx) {
                var tierClass = getTierClass(v.tierLabel);
                var budgetColor = getBudgetColor(v.budgetPct);

                h += '<tr>' +
                    '<td style="color:var(--text-muted);font-size:11px;">' + (idx + 1) + '</td>' +
                    '<td style="font-weight:500;">' + esc(v.name) + '</td>' +
                    '<td><span class="lf-budget-bar"><span class="lf-budget-fill" style="width:' + Math.min(100, v.budgetPct) + '%;background:' + budgetColor + ';"></span></span>' +
                    '<span style="font-family:monospace;font-size:11px;">' + v.budgetPct + '%</span></td>' +
                    '<td><span class="lf-tier-badge ' + tierClass + '">' + esc(v.tierLabel || '—') + '</span></td>' +
                    '<td>' + renderStatPills(v.stats, analysis.presentStatTypes) + '</td>' +
                    '</tr>';
            });

            h += '</tbody></table></div>';
        });

        // Legendary preview card
        if (data.legendary) {
            h += renderLegendaryCard(data.legendary);
            totalVariants += 1;
        }

        $('#previewContainer').html(h);
        var legendaryNote = data.legendary ? ' (includes 1 legendary)' : '';
        $('#previewInfo').text(totalVariants + ' variants across ' + data.items.length + ' items' + legendaryNote);

        $('#commitItemCount').text(totalVariants);
        $('#commitLootRows').text('~' + totalVariants);
        $('#commitBaseItems').text(data.items.length);
        $('#commitPanel').show();
    }

    function renderStatPills(stats, baseTypes) {
        var baseSet = {};
        if (baseTypes) baseTypes.forEach(function (t) { baseSet[t] = true; });

        var h = '';
        stats.forEach(function (s) {
            var isNew = !baseSet[s.statType];
            h += '<span class="lf-stat-pill' + (isNew ? ' new' : '') + '">+' + s.statValue + ' ' + esc(s.name) + '</span>';
        });
        return h;
    }

    function getTierClass(label) {
        if (!label) return 'variation';
        var s = label.toLowerCase();
        if (s.indexOf('gods') >= 0) return 'gods';
        if (s.indexOf('glory') >= 0) return 'glory';
        if (s.indexOf('power') >= 0) return 'power';
        return 'variation';
    }

    function getBudgetColor(pct) {
        if (pct >= 98) return '#ff8000';
        if (pct >= 90) return '#a335ee';
        if (pct >= 80) return 'var(--accent)';
        return 'var(--text-muted)';
    }

    function renderLegendaryCard(legendary) {
        if (!legendary) return '';

        var iconPath = legendary.iconPath || '/icons/inv_misc_questionmark.png';
        var h = '<div class="lf-legendary-card">';
        h += '<div class="lf-legendary-card-header">' +
            '<i class="fa-solid fa-crown" style="color:#ff8000;font-size:14px;"></i>' +
            '<span style="color:#ff8000;font-weight:700;font-size:13px;margin-left:6px;">Boss Legendary Preview</span>' +
            '<span style="color:var(--text-muted);font-size:11px;margin-left:auto;">Drop: ' + legendary.dropPct + '%</span>' +
            '</div>';

        h += '<div class="lf-legendary-card-body">' +
            '<img src="' + esc(iconPath) + '" style="width:28px;height:28px;border-radius:4px;border:1px solid #ff8000;" />' +
            '<div style="flex:1;min-width:0;">' +
            '<div class="quality-5" style="font-weight:700;font-size:13px;">' + esc(legendary.legendaryName) + '</div>' +
            '<div style="font-size:11px;color:var(--text-muted);">Base: <span class="quality-' + legendary.baseItemQuality + '">' + esc(legendary.baseItemName) + '</span>' +
            ' &middot; Boss: ' + esc(legendary.bossName) +
            ' &middot; Budget: <span style="color:#ff8000;font-weight:600;">150%</span></div>' +
            '</div></div>';

        h += '<div class="lf-legendary-card-stats">';
        legendary.stats.forEach(function (s) {
            h += '<span class="lf-stat-pill" style="border-color:#ff8000;background:rgba(255,128,0,0.08);">+' + s.statValue + ' ' + esc(s.name) + '</span>';
        });
        h += '</div></div>';

        return h;
    }

    // ===================== COMMIT =====================

    function doCommit() {
        if (currentMode === 'single') {
            doSingleCommit();
        } else {
            doBatchCommit();
        }
    }

    function doSingleCommit() {
        if (!previewData || !selectedCreature) return;

        var commitPayload = {
            creatureEntry: selectedCreature.entry,
            ruleset: collectRuleset(),
            variants: previewData.items.map(function (itemGroup) {
                return {
                    baseItemEntry: itemGroup.baseItem.entry,
                    rolls: itemGroup.variants.map(function (v) {
                        return {
                            budgetPct: v.budgetPct,
                            tierLabel: v.tierLabel || '',
                            tierPosition: v.tierPosition || 'suffix',
                            stats: v.stats.map(function (s) {
                                return { statType: s.statType, statValue: s.statValue };
                            })
                        };
                    })
                };
            })
        };

        $('#btnCommit').prop('disabled', true).html('<i class="fa-solid fa-spinner fa-spin"></i> Committing...');

        $.ajax({
            url: '/Lootifier/Commit',
            method: 'POST',
            contentType: 'application/json',
            data: JSON.stringify(commitPayload),
            success: function (result) {
                $('#btnCommit').prop('disabled', false).html('<i class="fa-solid fa-bolt"></i> <span>Commit to Database</span>');
                if (result.success) {
                    showToast(result.totalItemsCreated + ' items created + ' + result.totalLootRowsCreated + ' loot rows added', 'success');
                    if (selectedCreature) selectCreature(selectedCreature.entry);
                } else {
                    showToast('Commit failed: ' + (result.error || ''), 'error');
                }
            },
            error: function () {
                $('#btnCommit').prop('disabled', false).html('<i class="fa-solid fa-bolt"></i> <span>Commit to Database</span>');
                showToast('Commit failed — server error', 'error');
            }
        });
    }

    function doBatchCommit() {
        if (!batchData) return;

        var creatures = [];
        batchData.creatures.forEach(function (c) {
            var sel = batchSelectedItems[c.creatureEntry];
            if (!sel) return;
            var items = [];
            c.items.forEach(function (it) {
                if (sel[it.itemEntry]) items.push(it.itemEntry);
            });
            if (items.length > 0) {
                creatures.push({ creatureEntry: c.creatureEntry, itemEntries: items });
            }
        });

        if (creatures.length === 0) {
            showToast('No items selected', 'error');
            return;
        }

        var totalItems = creatures.reduce(function (sum, c) { return sum + c.itemEntries.length; }, 0);

        $('#btnCommit').prop('disabled', true).html('<i class="fa-solid fa-spinner fa-spin"></i> Committing ' + totalItems + ' items...');

        $.ajax({
            url: '/Lootifier/BatchCommit',
            method: 'POST',
            contentType: 'application/json',
            data: JSON.stringify({ creatures: creatures, ruleset: collectRuleset() }),
            success: function (result) {
                $('#btnCommit').prop('disabled', false).html('<i class="fa-solid fa-bolt"></i> <span>Commit to Database</span>');
                if (result.success) {
                    showToast(result.totalItemsCreated + ' items + ' + result.totalLootRowsCreated + ' loot rows across ' + result.creaturesProcessed + ' creatures', 'success');
                    $('#commitPanel').hide();
                } else {
                    showToast('Batch commit failed: ' + (result.error || ''), 'error');
                }
            },
            error: function () {
                $('#btnCommit').prop('disabled', false).html('<i class="fa-solid fa-bolt"></i> <span>Commit to Database</span>');
                showToast('Batch commit failed', 'error');
            }
        });
    }

    // ===================== ROLLBACK =====================

    function showRollbackModal(creatureEntry) {
        rollbackCreature = creatureEntry || 0;
        var desc = creatureEntry > 0
            ? 'This will remove all lootifier-generated items and loot entries for creature #' + creatureEntry + '.'
            : 'This will remove ALL lootifier-generated items and restore ALL modified loot tables.';
        $('#rollbackDesc').text(desc);
        new bootstrap.Modal($('#rollbackModal')[0]).show();
    }

    function doRollback() {
        bootstrap.Modal.getInstance($('#rollbackModal')[0]).hide();

        $.ajax({
            url: '/Lootifier/Rollback',
            method: 'POST',
            contentType: 'application/json',
            data: JSON.stringify({ creatureEntry: rollbackCreature }),
            success: function (result) {
                if (result.success) {
                    showToast('Rolled back: ' + result.itemsRemoved + ' items removed, ' + result.lootRowsFixed + ' loot entries restored', 'success');
                    if (selectedCreature) selectCreature(selectedCreature.entry);
                } else {
                    showToast('Rollback failed: ' + (result.error || ''), 'error');
                }
            },
            error: function () {
                showToast('Rollback failed', 'error');
            }
        });
    }

    // ===================== STATUS =====================

    function showStatus() {
        var modal = new bootstrap.Modal($('#statusModal')[0]);
        $('#statusBody').html('<div class="text-center p-4"><i class="fa-solid fa-spinner fa-spin"></i></div>');
        modal.show();

        $.getJSON('/Lootifier/Status', function (data) {
            if (!data.active) {
                $('#statusBody').html('<div class="lf-empty-state" style="padding:20px;"><i class="fa-solid fa-check-circle" style="color:var(--status-online);"></i>No lootifier data. Database is clean.</div>');
                return;
            }

            var h = '<div style="text-align:center;margin-bottom:16px;">' +
                '<div style="font-size:28px;font-weight:700;color:var(--accent);">' + data.totalItems + '</div>' +
                '<div style="font-size:11px;color:var(--text-muted);text-transform:uppercase;">Generated Items</div></div>';

            if (data.creatures && data.creatures.length > 0) {
                h += '<table class="table-clean"><thead><tr><th>Creature</th><th>Variants</th><th>Actions</th></tr></thead><tbody>';
                data.creatures.forEach(function (c) {
                    h += '<tr><td>Creature #' + c.creatureEntry + '</td>' +
                        '<td>' + c.variantCount + '</td>' +
                        '<td><button class="btn-micro lf-rollback-one" data-creature="' + c.creatureEntry + '">Rollback</button></td></tr>';
                });
                h += '</tbody></table>';
            }

            $('#statusBody').html(h);
        });
    }

    // ===================== HELPERS =====================

    function formatChance(val) {
        if (val === 0) return '0';
        if (val >= 10) return val.toFixed(1);
        if (val >= 1) return val.toFixed(2);
        return val.toFixed(3);
    }

    function esc(text) {
        if (text == null) return '';
        var div = document.createElement('div');
        div.textContent = String(text);
        return div.innerHTML;
    }

    function showToast(msg, type) {
        var el = $('<div class="lf-toast ' + type + '">' + esc(msg) + '</div>');
        $('body').append(el);
        setTimeout(function () { el.fadeOut(300, function () { el.remove(); }); }, 4000);
    }

    function updateGenerateButton() {
        var count = Object.keys(selectedItems).filter(function (k) { return selectedItems[k]; }).length;
        $('#btnGenerate').prop('disabled', count === 0);
    }

    function populateLegendaryPicker() {
        var sel = $('#rsLegendaryItem');
        sel.html('<option value="0">Random (any selected item)</option>');

        if (!lootTreeData) return;

        var items = [];
        if (lootTreeData.directItems) items = items.concat(lootTreeData.directItems);
        if (lootTreeData.referenceGroups) {
            lootTreeData.referenceGroups.forEach(function (rg) {
                items = items.concat(rg.items);
            });
        }

        items.forEach(function (item) {
            if (item.totalStats > 0 || item.hasSpellEffects) {
                var qualName = QUALITY_NAMES[item.quality] || '';
                sel.append('<option value="' + item.itemEntry + '">' + esc(item.itemName) + ' (' + qualName + ')</option>');
            }
        });
    }

    // ===================== EVENTS =====================

    // Search
    $('#btnSearchCreature').on('click', searchCreature);
    $('#creatureSearch').on('keydown', function (e) { if (e.key === 'Enter') searchCreature(); });

    // Select creature from results
    $(document).on('click', '.lf-search-item', function () {
        selectCreature(parseInt($(this).data('entry')));
    });

    // Toggle item in single-source loot tree
    $(document).on('click', '.lf-loot-row', function (e) {
        if ($(this).hasClass('no-stats')) return;
        if ($(e.target).is('input')) return;

        var entry = parseInt($(this).data('item'));
        var check = $(this).find('.lf-loot-check');
        if (check.length === 0) return;

        var isSelected = !selectedItems[entry];
        selectedItems[entry] = isSelected;
        check.prop('checked', isSelected);
        $(this).toggleClass('selected', isSelected);
        updateGenerateButton();
        previewData = null;
        $('#commitPanel').hide();
    });

    $(document).on('change', '.lf-loot-check', function (e) {
        e.stopPropagation();
        var row = $(this).closest('.lf-loot-row');
        var entry = parseInt(row.data('item'));
        selectedItems[entry] = $(this).is(':checked');
        row.toggleClass('selected', selectedItems[entry]);
        updateGenerateButton();
        previewData = null;
        $('#commitPanel').hide();
    });

    // Batch: toggle chips
    $(document).on('click', '#batchPanel .toggle-chip', function () {
        $(this).toggleClass('active');
    });

    $(document).on('click', '#batchPanel .instance-chip', function () {
        $(this).toggleClass('active');
    });

    // Batch: creature-level checkbox
    $(document).on('change', '.lf-batch-creature-check', function () {
        var ce = parseInt($(this).data('creature'));
        var checked = $(this).is(':checked');
        $(this).closest('.lf-batch-creature').find('.lf-batch-item-check[data-creature="' + ce + '"]').prop('checked', checked);
        if (!batchSelectedItems[ce]) batchSelectedItems[ce] = {};
        if (checked && batchData) {
            var c = batchData.creatures.find(function (cr) { return cr.creatureEntry === ce; });
            if (c) c.items.forEach(function (it) { batchSelectedItems[ce][it.itemEntry] = true; });
        } else {
            batchSelectedItems[ce] = {};
        }
    });

    // Batch: item-level checkbox
    $(document).on('change', '.lf-batch-item-check', function () {
        var ce = parseInt($(this).data('creature'));
        var ie = parseInt($(this).data('item'));
        if (!batchSelectedItems[ce]) batchSelectedItems[ce] = {};
        batchSelectedItems[ce][ie] = $(this).is(':checked');
    });

    // Generate (single)
    $('#btnGenerate').on('click', generatePreview);

    // Batch scan
    $('#btnBatchScan').on('click', batchScan);

    // Batch sample preview
    $('#btnBatchSample').on('click', batchSamplePreview);

    // Commit
    $('#btnCommit').on('click', doCommit);

    // Rollback
    $('#btnRollbackAll').on('click', function () { showRollbackModal(0); });
    $('#btnConfirmRollback').on('click', doRollback);
    $(document).on('click', '.lf-rollback-one', function () {
        showRollbackModal(parseInt($(this).data('creature')));
    });

    // Status
    $('#btnViewStatus').on('click', showStatus);

    // Legendary toggle show/hide
    $('#rsLegendaryToggle').on('change', function () {
        $('#legendaryConfig').toggle($(this).is(':checked'));
    });
    $(document).on('change', '.lf-batch-legendary-toggle', function () {
        $('.lf-batch-legendary-config').toggle($(this).is(':checked'));
    });

    // Reset ruleset
    $('#btnResetRuleset').on('click', function () {
        $('#rsBudgetCeiling').val(35);
        $('#rsVariantsPerItem').val(10);
        $('#rsAllowNewAffixes').prop('checked', true);
        $('#rsMaxAffixChange').val(1);
        renderNamingTiers();
    });

});
// MangosSuperUI — OG Baseline System (shared)
// Provides: BaselineSystem global object used by items.js, instances.js, loottuner.js

var BaselineSystem = (function () {

    var _status = null;
    var _initialized = null; // null = unchecked, true/false after check

    // ===================== STATUS CHECK =====================

    function checkStatus(callback) {
        $.getJSON('/Baseline/Status', function (data) {
            _status = data;
            _initialized = data.initialized;
            if (callback) callback(data);
        }).fail(function () {
            _initialized = false;
            if (callback) callback({ initialized: false, tables: [] });
        });
    }

    function isInitialized() {
        return _initialized === true;
    }

    // ===================== WARNING BANNER =====================

    function renderWarningBanner(containerId) {
        var $container = $(containerId || '#baselineWarning');
        if ($container.length === 0) return;

        if (_initialized) {
            $container.hide();
            return;
        }

        var html =
            '<div class="baseline-warning">' +
            '<div class="baseline-warning-icon"><i class="fa-solid fa-triangle-exclamation"></i></div>' +
            '<div class="baseline-warning-body">' +
            '<div class="baseline-warning-title">Content Baseline Not Initialized</div>' +
            '<div class="baseline-warning-text">' +
            'Before editing content, snapshot the original database values. This creates a backup for change tracking and resetting to original values.' +
            '</div>' +
            '<button class="baseline-init-btn" id="btnInitBaseline">' +
            '<i class="fa-solid fa-database"></i> Initialize Baseline' +
            '</button>' +
            '<div id="baselineProgress" style="display:none; margin-top:10px;"></div>' +
            '</div>' +
            '</div>';

        $container.html(html).show();
    }

    // ===================== INITIALIZE =====================

    function runInitialize(callback) {
        var $btn = $('#btnInitBaseline');
        var $prog = $('#baselineProgress');

        $btn.prop('disabled', true).html('<i class="fa-solid fa-spinner fa-spin"></i> Initializing...');
        $prog.show().html('<div class="baseline-progress-text">Creating snapshot tables and copying data...</div>' +
            '<div class="baseline-progress-bar"><div class="baseline-progress-fill" style="width:0%;"></div></div>');

        // Animate progress bar while waiting
        var pctEl = $prog.find('.baseline-progress-fill');
        var fakePct = 0;
        var interval = setInterval(function () {
            fakePct = Math.min(fakePct + 5, 90);
            pctEl.css('width', fakePct + '%');
        }, 300);

        $.ajax({
            url: '/Baseline/Initialize',
            method: 'POST',
            contentType: 'application/json',
            data: '{}',
            success: function (result) {
                clearInterval(interval);
                pctEl.css('width', '100%');

                if (result.success) {
                    _initialized = true;

                    // Show table results
                    var rhtml = '<div class="baseline-results">';
                    result.tables.forEach(function (t) {
                        var icon = t.status === 'created' ? 'fa-circle-check' : (t.status === 'skipped' ? 'fa-forward' : 'fa-circle-xmark');
                        var cls = t.status === 'created' ? 'created' : (t.status === 'skipped' ? 'skipped' : 'error');
                        rhtml += '<div class="baseline-table-result ' + cls + '">' +
                            '<i class="fa-solid ' + icon + '"></i> ' +
                            '<span class="baseline-table-name">' + esc(t.table) + '</span>' +
                            '<span class="baseline-table-count">' + (t.rowCount || 0).toLocaleString() + ' rows</span>' +
                            '<span class="baseline-table-status">' + esc(t.status) + '</span>' +
                            '</div>';
                    });
                    rhtml += '</div>';

                    $prog.html(rhtml);
                    $btn.hide();

                    // After a brief pause, hide the warning
                    setTimeout(function () {
                        $('#baselineWarning').slideUp(300);
                        if (callback) callback(true);
                    }, 2500);
                } else {
                    $btn.prop('disabled', false).html('<i class="fa-solid fa-database"></i> Initialize Baseline');
                    $prog.html('<div style="color:var(--status-error);">Initialization failed</div>');
                    if (callback) callback(false);
                }
            },
            error: function () {
                clearInterval(interval);
                $btn.prop('disabled', false).html('<i class="fa-solid fa-database"></i> Initialize Baseline');
                $prog.html('<div style="color:var(--status-error);">Server error during initialization</div>');
                if (callback) callback(false);
            }
        });
    }

    // ===================== DIFF RENDERING — ITEM =====================

    function loadItemDiff(entry, containerId, callback) {
        if (!_initialized) {
            if (callback) callback(null);
            return;
        }

        var $container = $(containerId);

        $.getJSON('/Baseline/DiffItem', { entry: entry }, function (data) {
            if (!data.available || !data.hasOriginal) {
                // Custom item or no original
                if (entry >= 900000) {
                    $container.html(
                        '<div class="changelog-empty">' +
                        '<i class="fa-solid fa-star" style="color:var(--status-online);"></i> Custom item — no original baseline' +
                        '</div>'
                    );
                } else {
                    $container.html('');
                }
                if (callback) callback(data);
                return;
            }

            if (!data.isModified) {
                $container.html(
                    '<div class="changelog-empty">' +
                    '<i class="fa-solid fa-circle-check" style="color:var(--status-online);"></i> No changes from original' +
                    '</div>'
                );
                if (callback) callback(data);
                return;
            }

            // Render field-level diff
            var html = '';
            data.changes.forEach(function (ch) {
                html += '<div class="changelog-row">' +
                    '<span class="changelog-field">' + esc(ch.field) + '</span>' +
                    '<span class="changelog-old" title="Original">' + esc(truncVal(ch.original)) + '</span>' +
                    '<i class="fa-solid fa-arrow-right changelog-arrow"></i>' +
                    '<span class="changelog-new" title="Current">' + esc(truncVal(ch.current)) + '</span>' +
                    '</div>';
            });

            $container.html(html);
            if (callback) callback(data);
        });
    }

    // ===================== DIFF RENDERING — SPELL =====================

    function loadSpellDiff(entry, containerId, callback) {
        if (!_initialized) {
            if (callback) callback(null);
            return;
        }

        var $container = $(containerId);

        $.getJSON('/Baseline/DiffSpell', { entry: entry }, function (data) {
            if (!data.available || !data.hasOriginal) {
                $container.html('');
                if (callback) callback(data);
                return;
            }

            if (!data.isModified) {
                $container.html(
                    '<div class="changelog-empty">' +
                    '<i class="fa-solid fa-circle-check" style="color:var(--status-online);"></i> No changes from original' +
                    '</div>'
                );
                if (callback) callback(data);
                return;
            }

            var html = '';
            data.changes.forEach(function (ch) {
                html += '<div class="changelog-row">' +
                    '<span class="changelog-field">' + esc(ch.field) + '</span>' +
                    '<span class="changelog-old" title="Original">' + esc(truncVal(ch.original)) + '</span>' +
                    '<i class="fa-solid fa-arrow-right changelog-arrow"></i>' +
                    '<span class="changelog-new" title="Current">' + esc(truncVal(ch.current)) + '</span>' +
                    '</div>';
            });

            $container.html(html);
            if (callback) callback(data);
        });
    }

    // ===================== DIFF RENDERING — GAME OBJECT =====================

    function loadGameObjectDiff(entry, containerId, callback) {
        if (!_initialized) {
            if (callback) callback(null);
            return;
        }

        var $container = $(containerId);

        $.getJSON('/Baseline/DiffGameObject', { entry: entry }, function (data) {
            if (!data.available || !data.hasOriginal) {
                if (entry >= 900000) {
                    $container.html(
                        '<div class="changelog-empty">' +
                        '<i class="fa-solid fa-star" style="color:var(--status-online);"></i> Custom object — no original baseline' +
                        '</div>'
                    );
                } else {
                    $container.html('');
                }
                if (callback) callback(data);
                return;
            }

            if (!data.isModified) {
                $container.html(
                    '<div class="changelog-empty">' +
                    '<i class="fa-solid fa-circle-check" style="color:var(--status-online);"></i> No changes from original' +
                    '</div>'
                );
                if (callback) callback(data);
                return;
            }

            var html = '';
            data.changes.forEach(function (ch) {
                html += '<div class="changelog-row">' +
                    '<span class="changelog-field">' + esc(ch.field) + '</span>' +
                    '<span class="changelog-old" title="Original">' + esc(truncVal(ch.original)) + '</span>' +
                    '<i class="fa-solid fa-arrow-right changelog-arrow"></i>' +
                    '<span class="changelog-new" title="Current">' + esc(truncVal(ch.current)) + '</span>' +
                    '</div>';
            });

            $container.html(html);
            if (callback) callback(data);
        });
    }

    // ===================== DIFF RENDERING — CREATURE LOOT =====================

    function loadCreatureLootDiff(creatureEntry, containerId, callback) {
        if (!_initialized) {
            if (callback) callback(null);
            return;
        }

        var $container = $(containerId);

        $.getJSON('/Baseline/DiffCreatureLoot', { creatureEntry: creatureEntry }, function (data) {
            if (!data.available || !data.hasLoot) {
                $container.html('');
                if (callback) callback(data);
                return;
            }

            if (!data.isModified) {
                $container.html(
                    '<div class="changelog-empty">' +
                    '<i class="fa-solid fa-circle-check" style="color:var(--status-online);"></i> No loot changes from original' +
                    '</div>'
                );
                if (callback) callback(data);
                return;
            }

            // Render loot changes
            var html = '';

            if (data.directChanges && data.directChanges.length > 0) {
                data.directChanges.forEach(function (ch) {
                    html += renderLootChange(ch, 'Direct');
                });
            }

            if (data.refChanges && data.refChanges.length > 0) {
                data.refChanges.forEach(function (rg) {
                    rg.changes.forEach(function (ch) {
                        html += renderLootChange(ch, 'Pool #' + rg.refEntry);
                    });
                });
            }

            $container.html(html);
            if (callback) callback(data);
        });
    }

    function renderLootChange(ch, source) {
        if (ch.type === 'modified') {
            var parts = [];
            if (Math.abs(ch.ogChance - ch.curChance) > 0.0001) {
                parts.push(formatChance(ch.ogChance) + '% → ' + formatChance(ch.curChance) + '%');
            }
            if (ch.ogMaxCount !== ch.curMaxCount) {
                parts.push('×' + ch.ogMaxCount + ' → ×' + ch.curMaxCount);
            }

            return '<div class="changelog-row loot-change modified">' +
                '<span class="changelog-badge modified">MOD</span>' +
                '<span class="changelog-field">Item #' + ch.item + '</span>' +
                '<span class="changelog-detail">' + esc(parts.join(', ')) + '</span>' +
                '<span class="changelog-source">' + esc(source) + '</span>' +
                '</div>';
        } else if (ch.type === 'added') {
            return '<div class="changelog-row loot-change added">' +
                '<span class="changelog-badge added">NEW</span>' +
                '<span class="changelog-field">Item #' + ch.item + '</span>' +
                '<span class="changelog-source">' + esc(source) + '</span>' +
                '</div>';
        } else if (ch.type === 'deleted') {
            return '<div class="changelog-row loot-change deleted">' +
                '<span class="changelog-badge deleted">DEL</span>' +
                '<span class="changelog-field">Item #' + ch.item + '</span>' +
                '<span class="changelog-source">' + esc(source) + '</span>' +
                '</div>';
        }
        return '';
    }

    // ===================== RESET HELPERS =====================

    // Pending reset action — stored when modal opens, executed on confirm
    var _pendingReset = null;

    function _showResetModal(title, body, action) {
        _pendingReset = action;
        $('#resetModalTitle').text(title);
        $('#resetModalBody').html(body);
        new bootstrap.Modal($('#resetConfirmModal')[0]).show();
    }

    function resetItem(entry, callback) {
        _showResetModal(
            'Reset Item #' + entry,
            'Reset this item to its original baseline values?<br>All edits you\'ve made will be reverted.',
            function () {
                $.post('/Baseline/ResetItem', { entry: entry }, function (result) {
                    if (result.success) {
                        showToast('Item #' + entry + ' reset to original', 'success');
                        if (callback) callback(true);
                    } else {
                        showToast('Reset failed: ' + (result.error || 'Unknown'), 'error');
                        if (callback) callback(false);
                    }
                }).fail(function () {
                    showToast('Reset failed — server error', 'error');
                    if (callback) callback(false);
                });
            }
        );
    }

    function resetCreatureLoot(creatureEntry, creatureName, callback) {
        _showResetModal(
            'Reset Loot — ' + (creatureName || 'Creature #' + creatureEntry),
            'Reset all loot for <strong>' + esc(creatureName || 'this creature') + '</strong> to original baseline values?<br>This includes direct drops and shared loot pools.',
            function () {
                $.post('/Baseline/ResetCreatureLoot', { creatureEntry: creatureEntry }, function (result) {
                    if (result.success) {
                        showToast((result.creatureName || 'Creature') + ' loot reset (' + result.totalRestored + ' rows)', 'success');
                        if (callback) callback(true);
                    } else {
                        showToast('Reset failed: ' + (result.error || 'Unknown'), 'error');
                        if (callback) callback(false);
                    }
                }).fail(function () {
                    showToast('Reset failed — server error', 'error');
                    if (callback) callback(false);
                });
            }
        );
    }

    function resetInstance(mapId, callback) {
        _showResetModal(
            'Reset Entire Instance',
            'Reset <strong>ALL</strong> loot for every creature in this instance to original baseline values?<br>This affects every boss and trash mob in the instance.',
            function () {
                $.post('/Baseline/ResetInstance', { mapId: mapId }, function (result) {
                    if (result.success) {
                        showToast('Instance reset: ' + result.creaturesReset + ' creatures, ' + result.totalRestored + ' rows', 'success');
                        if (callback) callback(true);
                    } else {
                        showToast('Reset failed: ' + (result.error || 'Unknown'), 'error');
                        if (callback) callback(false);
                    }
                }).fail(function () {
                    showToast('Reset failed — server error', 'error');
                    if (callback) callback(false);
                });
            }
        );
    }

    function resetSpell(entry, spellName, callback) {
        _showResetModal(
            'Reset Spell #' + entry,
            'Reset <strong>' + esc(spellName || 'Spell #' + entry) + '</strong> to its original baseline values?<br>All edits will be reverted. Server restart required for changes to take effect.',
            function () {
                $.post('/Baseline/ResetSpell', { entry: entry }, function (result) {
                    if (result.success) {
                        showToast((result.spellName || 'Spell') + ' reset to original. Restart required.', 'success');
                        if (callback) callback(true);
                    } else {
                        showToast('Reset failed: ' + (result.error || 'Unknown'), 'error');
                        if (callback) callback(false);
                    }
                }).fail(function () {
                    showToast('Reset failed — server error', 'error');
                    if (callback) callback(false);
                });
            }
        );
    }

    function resetGameObject(entry, objName, callback) {
        _showResetModal(
            'Reset Game Object #' + entry,
            'Reset <strong>' + esc(objName || 'Game Object #' + entry) + '</strong> to its original baseline values?<br>All edits will be reverted.',
            function () {
                $.post('/Baseline/ResetGameObject', { entry: entry }, function (result) {
                    if (result.success) {
                        showToast((result.objName || 'Game object') + ' reset to original', 'success');
                        if (callback) callback(true);
                    } else {
                        showToast('Reset failed: ' + (result.error || 'Unknown'), 'error');
                        if (callback) callback(false);
                    }
                }).fail(function () {
                    showToast('Reset failed — server error', 'error');
                    if (callback) callback(false);
                });
            }
        );
    }

    function resetAll(callback) {
        _showResetModal(
            'Reset Everything',
            '<strong style="color: var(--status-error);">Nuclear option.</strong> This resets ALL item edits and ALL loot changes across every table back to the original baseline.<br><br>This cannot be undone.',
            function () {
                $.post('/Baseline/ResetAll', {}, function (result) {
                    if (result.success) {
                        showToast('Full baseline reset complete: ' + result.totalRestored + ' rows restored', 'success');
                        if (callback) callback(true);
                    } else {
                        showToast('Reset failed', 'error');
                        if (callback) callback(false);
                    }
                }).fail(function () {
                    showToast('Reset failed — server error', 'error');
                    if (callback) callback(false);
                });
            }
        );
    }

    // ===================== UTILITIES =====================

    function truncVal(val) {
        if (val == null) return '(null)';
        var s = String(val);
        return s.length > 40 ? s.substring(0, 37) + '...' : s;
    }

    function formatChance(val) {
        if (val === 0) return '0';
        var abs = Math.abs(val);
        if (abs >= 10) return val.toFixed(1);
        if (abs >= 1) return val.toFixed(2);
        if (abs >= 0.1) return val.toFixed(3);
        return val.toFixed(4);
    }

    function esc(text) {
        if (text == null) return '';
        var div = document.createElement('div');
        div.textContent = String(text);
        return div.innerHTML;
    }

    function showToast(msg, type) {
        var el = $('<div class="baseline-toast ' + (type || '') + '">' + esc(msg) + '</div>');
        $('body').append(el);
        setTimeout(function () { el.fadeOut(300, function () { el.remove(); }); }, 3500);
    }

    // ===================== INIT EVENT =====================

    $(document).on('click', '#btnInitBaseline', function () {
        runInitialize(function (success) {
            if (success) {
                $(document).trigger('baseline:initialized');
            }
        });
    });

    // Reset modal confirm button
    $(document).on('click', '#btnConfirmReset', function () {
        bootstrap.Modal.getInstance($('#resetConfirmModal')[0]).hide();
        if (_pendingReset) {
            _pendingReset();
            _pendingReset = null;
        }
    });

    // Clear pending reset if modal is dismissed
    $('#resetConfirmModal').on('hidden.bs.modal', function () {
        _pendingReset = null;
    });

    // ===================== PUBLIC API =====================

    return {
        checkStatus: checkStatus,
        isInitialized: isInitialized,
        renderWarningBanner: renderWarningBanner,
        loadItemDiff: loadItemDiff,
        loadSpellDiff: loadSpellDiff,
        loadGameObjectDiff: loadGameObjectDiff,
        loadCreatureLootDiff: loadCreatureLootDiff,
        resetItem: resetItem,
        resetSpell: resetSpell,
        resetGameObject: resetGameObject,
        resetCreatureLoot: resetCreatureLoot,
        resetInstance: resetInstance,
        resetAll: resetAll,
        formatChance: formatChance
    };

})();
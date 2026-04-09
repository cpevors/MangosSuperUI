// MangosSuperUI — Realm Page JS

$(function () {

    // ===================== INIT =====================
    loadRealms();

    // ===================== LOAD =====================
    function loadRealms() {
        $.getJSON('/Realm/List', function (data) {
            // Stats
            $('#statOnlinePlayers').text(data.stats.onlinePlayers);
            $('#statOnlineAccounts').text(data.stats.onlineAccounts);
            $('#statTotalAccounts').text(data.stats.totalAccounts);

            // Realm cards
            var $list = $('#realmList');
            $list.empty();

            if (!data.realms || data.realms.length === 0) {
                $list.html('<div class="text-center text-muted" style="padding: 60px;"><i class="fa-solid fa-globe" style="font-size: 36px; margin-bottom: 12px; display: block;"></i><div>No realms found in realmlist table</div></div>');
                return;
            }

            for (var i = 0; i < data.realms.length; i++) {
                $list.append(renderRealmCard(data.realms[i]));
            }
        });
    }

    function renderRealmCard(r) {
        var isOffline = (r.realmFlags & 0x02) !== 0;
        var statusClass = isOffline ? 'realm-status-offline' : 'realm-status-online';
        var statusText = isOffline ? 'Offline' : 'Online';

        var flagChips = '';
        if (r.flagNames && r.flagNames.length > 0) {
            for (var f = 0; f < r.flagNames.length; f++) {
                flagChips += '<span class="realm-flag-chip">' + escapeHtml(r.flagNames[f]) + '</span>';
            }
        }

        var html = '<div class="realm-card" data-realm-id="' + r.id + '">';

        // Header
        html += '<div class="realm-card-header">';
        html += '<div class="realm-card-title">';
        html += '<i class="fa-solid fa-globe" style="color: var(--accent);"></i>';
        html += escapeHtml(r.name);
        html += ' <span class="realm-type-badge">' + escapeHtml(r.iconName) + '</span>';
        html += '</div>';
        html += '<span class="realm-status ' + statusClass + '">' + statusText + '</span>';
        html += '</div>';

        // Body — editable fields
        html += '<div class="realm-card-body">';
        html += '<div class="realm-fields">';

        // Row 1: Name, Address, Port
        html += fieldInput('Name', 'name', r.name, r.id);
        html += fieldInput('Address', 'address', r.address, r.id);
        html += fieldInput('Port', 'port', r.port, r.id, 'number');

        // Row 2: Type, Timezone, Population
        html += fieldSelect('Type', 'icon', r.icon, r.id, [
            { v: 0, l: 'Normal' }, { v: 1, l: 'PvP' }, { v: 6, l: 'RP' }, { v: 8, l: 'RP-PvP' }
        ]);
        html += fieldSelect('Timezone', 'timezone', r.timezone, r.id, [
            { v: 0, l: 'Any' }, { v: 1, l: 'US — Dev' }, { v: 2, l: 'US — English' },
            { v: 3, l: 'US — Oceanic' }, { v: 4, l: 'US — Latin America' },
            { v: 8, l: 'EU — English' }, { v: 9, l: 'EU — German' },
            { v: 10, l: 'EU — French' }, { v: 11, l: 'EU — Spanish' }, { v: 12, l: 'EU — Russian' }
        ]);
        html += fieldSelect('Population Display', 'population', r.population, r.id, [
            { v: 0, l: 'Offline (0)' }, { v: 0.5, l: 'Low (0.5)' },
            { v: 1.0, l: 'Medium (1.0)' }, { v: 2.0, l: 'High (2.0)' }, { v: 3.0, l: 'Full (3.0)' }
        ]);

        html += '</div>'; // realm-fields

        // Flags (read-only display + raw value)
        html += '<div style="margin-top: 16px;">';
        html += '<label style="font-size: 11.5px; font-weight: 600; text-transform: uppercase; letter-spacing: 0.06em; color: var(--text-muted); margin-bottom: 6px; display: block;">Flags</label>';
        html += '<div class="d-flex align-items-center gap-3">';
        html += '<div class="realm-flags">' + flagChips + '</div>';
        html += '<div style="flex-shrink: 0;">';
        html += '<input type="number" class="form-input realm-edit" data-realm="' + r.id + '" data-field="realmFlags" value="' + r.realmFlags + '" style="width: 80px; font-size: 12px;" title="Raw flag value" />';
        html += '</div>';
        html += '</div>';
        html += '</div>';

        // Realm builds (read-only)
        if (r.realmBuilds) {
            html += '<div style="margin-top: 12px;">';
            html += '<label style="font-size: 11.5px; font-weight: 600; text-transform: uppercase; letter-spacing: 0.06em; color: var(--text-muted); margin-bottom: 4px; display: block;">Allowed Builds</label>';
            html += '<div class="realm-field-readonly" style="font-family: Consolas, monospace; font-size: 12.5px; color: var(--text-secondary);">' + escapeHtml(r.realmBuilds) + '</div>';
            html += '</div>';
        }

        html += '</div>'; // realm-card-body

        // Actions
        html += '<div class="realm-actions">';
        html += '<button class="btn-outline-subtle realm-cancel" data-realm="' + r.id + '"><i class="fa-solid fa-rotate-left"></i> Reset</button>';
        html += '<button class="btn-accent realm-save" data-realm="' + r.id + '"><i class="fa-solid fa-floppy-disk"></i> Save Changes</button>';
        html += '</div>';

        html += '</div>'; // realm-card

        return html;
    }

    function fieldInput(label, field, value, realmId, type) {
        type = type || 'text';
        var html = '<div class="realm-field">';
        html += '<label>' + label + '</label>';
        html += '<input type="' + type + '" class="form-input realm-edit" data-realm="' + realmId + '" data-field="' + field + '" value="' + escapeAttr(String(value)) + '" />';
        html += '</div>';
        return html;
    }

    function fieldSelect(label, field, value, realmId, options) {
        var html = '<div class="realm-field">';
        html += '<label>' + label + '</label>';
        html += '<select class="form-input realm-edit" data-realm="' + realmId + '" data-field="' + field + '">';
        for (var i = 0; i < options.length; i++) {
            var sel = (parseFloat(options[i].v) === parseFloat(value)) ? ' selected' : '';
            html += '<option value="' + options[i].v + '"' + sel + '>' + escapeHtml(options[i].l) + '</option>';
        }
        html += '</select></div>';
        return html;
    }

    // ===================== SAVE =====================
    $(document).on('click', '.realm-save', function () {
        var realmId = $(this).data('realm');
        var $card = $('[data-realm-id="' + realmId + '"]');

        var payload = { id: realmId };

        $card.find('.realm-edit[data-realm="' + realmId + '"]').each(function () {
            var field = $(this).data('field');
            var val = $(this).val();

            if (field === 'port' || field === 'icon' || field === 'timezone' || field === 'realmFlags') {
                payload[field] = parseInt(val) || 0;
            } else if (field === 'population') {
                payload[field] = parseFloat(val) || 0;
            } else {
                payload[field] = val;
            }
        });

        var $btn = $(this);
        $btn.prop('disabled', true).html('<i class="fa-solid fa-spinner fa-spin"></i> Saving...');

        $.ajax({
            url: '/Realm/Update',
            type: 'POST',
            contentType: 'application/json',
            data: JSON.stringify(payload),
            success: function (data) {
                if (data.success) {
                    showToast('Realm updated' + (data.changesCount > 0 ? ' (' + data.changesCount + ' changes)' : ' (no changes)'), 'success');
                    // Reload to reflect changes
                    loadRealms();
                } else {
                    showToast('Error: ' + (data.error || 'Unknown error'), 'error');
                }
            },
            error: function (xhr) {
                showToast('Request failed: ' + xhr.statusText, 'error');
            },
            complete: function () {
                $btn.prop('disabled', false).html('<i class="fa-solid fa-floppy-disk"></i> Save Changes');
            }
        });
    });

    // Reset button — just reload
    $(document).on('click', '.realm-cancel', function () {
        loadRealms();
    });

    // ===================== TOAST =====================
    function showToast(message, type) {
        var $toast = $('<div class="realm-toast realm-toast-' + type + '">' + escapeHtml(message) + '</div>');
        $('body').append($toast);
        setTimeout(function () { $toast.fadeOut(300, function () { $(this).remove(); }); }, 3000);
    }

    // ===================== UTILITY =====================
    function escapeHtml(text) {
        var div = document.createElement('div');
        div.textContent = text;
        return div.innerHTML;
    }

    function escapeAttr(text) {
        return String(text).replace(/"/g, '&quot;').replace(/'/g, '&#39;');
    }

});

// MangosSuperUI — Activity Log Page JS

$(function () {

    // ===================== STATE =====================
    var currentCategory = '';
    var currentSearch = '';
    var currentPage = 1;
    var failuresOnly = false;
    var searchTimer = null;

    // ===================== INIT =====================
    loadSummary();
    loadEntries();

    // ===================== SUMMARY / CHIPS =====================
    function loadSummary() {
        $.getJSON('/Activity/Summary', function (data) {
            // Today count badge
            $('#alTodayCount').text(data.todayCount + ' today');

            // Failures badge
            if (data.recentFailures > 0) {
                $('#alFailCount').text(data.recentFailures + ' failed').show();
            }

            // Build category chips
            var $chips = $('#alChips');
            $chips.empty();
            $chips.append('<button class="al-chip active" data-category="">All <span class="al-chip-count">' + data.total + '</span></button>');

            var icons = {
                system: 'fa-server', character: 'fa-user', account: 'fa-id-badge',
                ban: 'fa-ban', guild: 'fa-people-group', bot: 'fa-robot',
                config: 'fa-gear', query: 'fa-magnifying-glass', command: 'fa-terminal'
            };

            for (var i = 0; i < data.categories.length; i++) {
                var c = data.categories[i];
                var icon = icons[c.category] || 'fa-circle';
                $chips.append(
                    '<button class="al-chip" data-category="' + escapeAttr(c.category) + '">' +
                    '<i class="fa-solid ' + icon + '" style="margin-right: 4px; font-size: 10px;"></i>' +
                    capitalize(c.category) +
                    ' <span class="al-chip-count">' + c.count + '</span></button>'
                );
            }
        });
    }

    // Chip click
    $('#alChips').on('click', '.al-chip', function () {
        $('.al-chip').removeClass('active');
        $(this).addClass('active');
        currentCategory = $(this).data('category') || '';
        currentPage = 1;
        loadEntries();
    });

    // ===================== SEARCH =====================
    $('#alSearch').on('input', function () {
        clearTimeout(searchTimer);
        var q = $(this).val().trim();
        searchTimer = setTimeout(function () {
            currentSearch = q;
            currentPage = 1;
            loadEntries();
        }, 300);
    });

    // Failures toggle
    $('#alFailuresOnly').on('change', function () {
        failuresOnly = $(this).is(':checked');
        currentPage = 1;
        loadEntries();
    });

    // ===================== LOAD ENTRIES =====================
    function loadEntries() {
        var params = { page: currentPage, pageSize: 50 };
        if (currentCategory) params.category = currentCategory;
        if (currentSearch) params.search = currentSearch;
        if (failuresOnly) params.successOnly = false;

        $.getJSON('/Activity/Entries', params, function (data) {
            var $container = $('#alEntries');
            $container.empty();

            if (data.entries.length === 0) {
                $('#alEmpty').show();
                $('#alPagination').hide();
                return;
            }

            $('#alEmpty').hide();

            for (var i = 0; i < data.entries.length; i++) {
                $container.append(renderEntry(data.entries[i]));
            }

            // Pagination
            if (data.totalPages > 1) {
                var start = (data.page - 1) * data.pageSize + 1;
                var end = Math.min(data.page * data.pageSize, data.total);
                $('#alPageInfo').text('Showing ' + start + '–' + end + ' of ' + data.total);
                $('#alPrev').prop('disabled', data.page <= 1);
                $('#alNext').prop('disabled', data.page >= data.totalPages);
                $('#alPagination').show();
            } else {
                $('#alPagination').hide();
            }
        });
    }

    // ===================== RENDER ENTRY =====================
    function renderEntry(e) {
        var catClass = 'al-entry-cat-' + e.category;
        var failClass = e.success ? '' : ' al-fail';
        var time = formatTime(e.timestamp);
        var actionLabel = e.action.replace(/_/g, ' ');

        var html = '<div class="al-entry' + failClass + '" data-id="' + e.id + '">';
        html += '<div class="al-entry-header">';
        html += '<span class="al-entry-time">' + time + '</span>';
        html += '<span class="al-entry-cat ' + catClass + '">' + escapeHtml(e.category) + '</span>';
        html += '<span class="al-entry-action">' + escapeHtml(actionLabel) + '</span>';

        if (e.targetName) {
            html += '<span class="al-entry-target">';
            if (e.targetType) html += escapeHtml(e.targetType) + ': ';
            html += '<strong>' + escapeHtml(e.targetName) + '</strong>';
            html += '</span>';
        }

        html += '<span class="al-entry-status ' + (e.success ? 'al-entry-status-ok' : 'al-entry-status-fail') + '">';
        html += e.success ? '<i class="fa-solid fa-check"></i>' : '<i class="fa-solid fa-xmark"></i> Failed';
        html += '</span>';
        html += '</div>';

        // Detail (hidden until expanded)
        html += '<div class="al-detail">';

        if (e.raCommand) {
            html += detailRow('Command', '<code>' + escapeHtml(e.raCommand) + '</code>');
        }
        if (e.raResponse) {
            html += detailRow('Response', '<code>' + escapeHtml(truncate(e.raResponse, 200)) + '</code>');
        }
        if (e.operator) {
            html += detailRow('Operator', escapeHtml(e.operator) + (e.operatorIp ? ' (' + escapeHtml(e.operatorIp) + ')' : ''));
        }
        if (e.notes) {
            html += detailRow('Notes', escapeHtml(e.notes));
        }
        if (e.stateBefore) {
            html += '<div class="al-detail-row"><span class="al-detail-label">State Before</span></div>';
            html += '<div class="al-detail-json">' + formatJson(e.stateBefore) + '</div>';
        }
        if (e.stateAfter) {
            html += '<div class="al-detail-row"><span class="al-detail-label">State After</span></div>';
            html += '<div class="al-detail-json">' + formatJson(e.stateAfter) + '</div>';
        }
        if (e.isReversible) {
            html += detailRow('Reversible', '<i class="fa-solid fa-rotate-left" style="color: var(--accent);"></i> Yes');
        }

        html += '</div></div>';
        return html;
    }

    function detailRow(label, value) {
        return '<div class="al-detail-row"><span class="al-detail-label">' + label + '</span><span class="al-detail-value">' + value + '</span></div>';
    }

    // Toggle expand
    $('#alEntries').on('click', '.al-entry', function (e) {
        if ($(e.target).closest('code').length) return; // allow text selection
        $(this).toggleClass('al-expanded');
    });

    // ===================== PAGINATION =====================
    $('#alPrev').on('click', function () {
        if (currentPage > 1) { currentPage--; loadEntries(); }
    });
    $('#alNext').on('click', function () {
        currentPage++;
        loadEntries();
    });

    // ===================== HELPERS =====================
    function formatTime(ts) {
        if (!ts) return '—';
        var d = new Date(ts);
        var pad = function (n) { return n < 10 ? '0' + n : n; };
        return d.getFullYear() + '-' + pad(d.getMonth() + 1) + '-' + pad(d.getDate()) + ' ' +
               pad(d.getHours()) + ':' + pad(d.getMinutes()) + ':' + pad(d.getSeconds());
    }

    function formatJson(str) {
        if (!str) return '';
        try {
            var obj = typeof str === 'string' ? JSON.parse(str) : str;
            return escapeHtml(JSON.stringify(obj, null, 2));
        } catch (e) {
            return escapeHtml(str);
        }
    }

    function truncate(str, len) {
        return str && str.length > len ? str.substring(0, len) + '...' : str;
    }

    function capitalize(s) {
        return s ? s.charAt(0).toUpperCase() + s.slice(1) : s;
    }

    function escapeHtml(text) {
        if (!text) return '';
        var div = document.createElement('div');
        div.textContent = text;
        return div.innerHTML;
    }

    function escapeAttr(text) {
        return String(text).replace(/"/g, '&quot;').replace(/'/g, '&#39;');
    }

});

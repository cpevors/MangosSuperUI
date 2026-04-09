// MangosSuperUI — Server Logs Page JS

$(function () {

    // ===================== STATE =====================
    var activeTab = 'characters';
    var currentType = '';
    var currentSearch = '';
    var currentPage = 1;
    var searchTimer = null;

    // ===================== TAB CONFIG =====================
    var tabConfig = {
        characters: {
            endpoint: '/ServerLogs/Characters',
            searchPlaceholder: 'Search by name or IP...',
            types: ['Login', 'Logout', 'Create', 'Delete', 'LostSocket'],
            render: renderCharacter
        },
        chat: {
            endpoint: '/ServerLogs/Chat',
            searchPlaceholder: 'Search messages or channels...',
            types: ['Say', 'Whisp', 'Group', 'Guild', 'Officer', 'Raid', 'BG', 'Chan'],
            render: renderChat
        },
        trades: {
            endpoint: '/ServerLogs/Trades',
            searchPlaceholder: '',
            types: ['AuctionBid', 'AuctionBuyout', 'SellItem', 'GM', 'Mail', 'QuestMaxLevel', 'Quest', 'Loot', 'Trade'],
            render: renderTrade
        },
        transactions: {
            endpoint: '/ServerLogs/Transactions',
            searchPlaceholder: '',
            types: ['Bid', 'Buyout', 'PlaceAuction', 'Trade', 'Mail', 'MailCOD'],
            render: renderTransaction
        },
        warden: {
            endpoint: '/ServerLogs/Warden',
            searchPlaceholder: '',
            types: [],
            render: renderWarden
        },
        spam: {
            endpoint: '/ServerLogs/Spam',
            searchPlaceholder: '',
            types: [],
            render: renderSpam
        },
        behavior: {
            endpoint: '/ServerLogs/Behavior',
            searchPlaceholder: '',
            types: [],
            render: renderBehavior
        },
        battlegrounds: {
            endpoint: '/ServerLogs/Battlegrounds',
            searchPlaceholder: '',
            types: [],
            render: renderBattleground
        }
    };

    // ===================== INIT =====================
    loadOverview();
    loadTabData();

    // ===================== OVERVIEW =====================
    function loadOverview() {
        $.getJSON('/ServerLogs/Overview', function (data) {
            setStatValue('slCountCharacters', data['logs_characters']);
            setStatValue('slCountChat', data['logs_chat']);
            setStatValue('slCountTrades', data['logs_trade']);
            setStatValue('slCountWarden', data['logs_warden']);
        });
    }

    function setStatValue(id, val) {
        if (val === undefined || val === null || val === -1) {
            $('#' + id).text('—');
        } else {
            $('#' + id).text(val.toLocaleString());
        }
    }

    // Click stat card to switch tab
    $('.sl-stat-card').on('click', function () {
        var tab = $(this).data('tab');
        if (tab) switchTab(tab);
    });

    // ===================== TAB SWITCHING =====================
    $('#slTabs').on('click', '.sl-tab', function () {
        var tab = $(this).data('tab');
        switchTab(tab);
    });

    function switchTab(tab) {
        activeTab = tab;
        currentType = '';
        currentSearch = '';
        currentPage = 1;

        $('.sl-tab').removeClass('active');
        $('.sl-tab[data-tab="' + tab + '"]').addClass('active');

        // Update filter bar
        var config = tabConfig[tab];
        if (config) {
            if (config.searchPlaceholder) {
                $('#slSearch').val('').attr('placeholder', config.searchPlaceholder).closest('.al-search-wrap').show();
            } else {
                $('#slSearch').closest('.al-search-wrap').hide();
            }
            buildTypeChips(config.types);
        }

        loadTabData();
    }

    function buildTypeChips(types) {
        var $chips = $('#slTypeChips');
        $chips.empty();

        if (types.length === 0) return;

        $chips.append('<button class="al-chip active" data-type="">All</button>');
        for (var i = 0; i < types.length; i++) {
            $chips.append('<button class="al-chip" data-type="' + escapeAttr(types[i]) + '">' + types[i] + '</button>');
        }
    }

    // Type chip click
    $('#slTypeChips').on('click', '.al-chip', function () {
        $('#slTypeChips .al-chip').removeClass('active');
        $(this).addClass('active');
        currentType = $(this).data('type') || '';
        currentPage = 1;
        loadTabData();
    });

    // Search
    $('#slSearch').on('input', function () {
        clearTimeout(searchTimer);
        var q = $(this).val().trim();
        searchTimer = setTimeout(function () {
            currentSearch = q;
            currentPage = 1;
            loadTabData();
        }, 300);
    });

    // ===================== LOAD TAB DATA =====================
    function loadTabData() {
        var config = tabConfig[activeTab];
        if (!config) return;

        var params = { page: currentPage, pageSize: 50 };
        if (currentType) params.type = currentType;
        if (currentSearch) params.search = currentSearch;

        $.getJSON(config.endpoint, params, function (data) {
            var $results = $('#slResults');
            $results.empty();

            if (!data.rows || data.rows.length === 0) {
                $results.html(
                    '<div class="card"><div class="card-body" style="text-align: center; padding: 48px; color: var(--text-muted);">' +
                    '<i class="fa-solid fa-inbox" style="font-size: 32px; margin-bottom: 12px;"></i>' +
                    '<div style="font-size: 14px; font-weight: 500;">No log entries found</div></div></div>'
                );
                $('#slPagination').hide();
                return;
            }

            for (var i = 0; i < data.rows.length; i++) {
                $results.append(config.render(data.rows[i]));
            }

            // Pagination
            if (data.totalPages > 1) {
                var start = (data.page - 1) * data.pageSize + 1;
                var end = Math.min(data.page * data.pageSize, data.total);
                $('#slPageInfo').text('Showing ' + start + '–' + end + ' of ' + data.total);
                $('#slPrev').prop('disabled', data.page <= 1);
                $('#slNext').prop('disabled', data.page >= data.totalPages);
                $('#slPagination').show();
            } else {
                $('#slPagination').hide();
            }
        });
    }

    // ===================== RENDER FUNCTIONS =====================

    function renderCharacter(r) {
        var badgeClass = 'sl-badge-' + (r.type || '').toLowerCase();
        return '<div class="sl-entry">' +
            '<div class="sl-entry-header">' +
            '<span class="sl-entry-time">' + formatTime(r.time) + '</span>' +
            '<span class="sl-entry-badge ' + badgeClass + '">' + escapeHtml(r.type) + '</span>' +
            '<strong style="color: var(--text-primary);">' + escapeHtml(r.name) + '</strong>' +
            '<span style="color: var(--text-muted); font-size: 12px;">GUID ' + r.guid + ' · Account ' + r.account + '</span>' +
            '<span style="color: var(--text-muted); font-size: 11.5px; margin-left: auto;">' + escapeHtml(r.ip) + '</span>' +
            '</div></div>';
    }

    function renderChat(r) {
        var badgeClass = 'sl-badge-' + (r.type || '').toLowerCase();
        var channel = r.channelName ? ' in <strong>' + escapeHtml(r.channelName) + '</strong>' : '';
        var target = r.target && r.target > 0 ? ' → GUID ' + r.target : '';

        return '<div class="sl-entry">' +
            '<div class="sl-entry-header">' +
            '<span class="sl-entry-time">' + formatTime(r.time) + '</span>' +
            '<span class="sl-entry-badge ' + badgeClass + '">' + escapeHtml(r.type) + '</span>' +
            '<span style="font-size: 12.5px; color: var(--text-secondary);">GUID ' + r.guid + target + channel + '</span>' +
            '</div>' +
            '<div class="sl-chat-msg">' + escapeHtml(r.message) + '</div>' +
            '</div>';
    }

    function renderTrade(r) {
        var gold = Math.floor(Math.abs(r.amount) / 10000);
        var silver = Math.floor((Math.abs(r.amount) % 10000) / 100);
        var copper = Math.abs(r.amount) % 100;
        var moneyStr = gold + 'g ' + silver + 's ' + copper + 'c';

        return '<div class="sl-entry">' +
            '<div class="sl-entry-header">' +
            '<span class="sl-entry-time">' + formatTime(r.time) + '</span>' +
            '<span class="sl-entry-badge sl-badge-default">' + escapeHtml(r.type) + '</span>' +
            '<span style="font-size: 12.5px; color: var(--text-secondary);">' +
            'Sender GUID ' + r.sender + ' → Receiver GUID ' + r.receiver + '</span>' +
            '<span style="margin-left: auto; font-weight: 600; color: #c9a054; font-size: 12.5px;">' + moneyStr + '</span>' +
            '</div></div>';
    }

    function renderTransaction(r) {
        var items1 = r.items1 || '—';
        var items2 = r.items2 || '—';

        return '<div class="sl-entry">' +
            '<div class="sl-entry-header">' +
            '<span class="sl-entry-time">' + formatTime(r.time) + '</span>' +
            '<span class="sl-entry-badge sl-badge-default">' + escapeHtml(r.type) + '</span>' +
            '</div>' +
            '<div class="sl-entry-body">' +
            '<strong>GUID ' + r.guid1 + '</strong>: ' + formatMoney(r.money1) +
            (items1 !== '—' ? ' · Items: ' + escapeHtml(items1) : '') +
            ' ↔ <strong>GUID ' + r.guid2 + '</strong>: ' + formatMoney(r.money2) +
            (items2 !== '—' ? ' · Items: ' + escapeHtml(items2) : '') +
            '</div></div>';
    }

    function renderWarden(r) {
        var actionLabels = { 0: 'Log', 1: 'Kick', 2: 'Ban' };
        var actionLabel = actionLabels[r.action] || 'Action ' + r.action;
        var badgeClass = r.action >= 2 ? 'sl-badge-delete' : r.action >= 1 ? 'sl-badge-lostsocket' : 'sl-badge-default';

        return '<div class="sl-entry">' +
            '<div class="sl-entry-header">' +
            '<span class="sl-entry-time">' + formatTime(r.date) + '</span>' +
            '<span class="sl-entry-badge ' + badgeClass + '">' + actionLabel + '</span>' +
            '<span style="font-size: 12.5px; color: var(--text-secondary);">' +
            'Check #' + r.check + ' · Account ' + r.account + ' · GUID ' + r.guid + '</span>' +
            '</div>' +
            (r.map !== null ? '<div class="sl-entry-body">Map ' + r.map +
            ' (' + (r.posX || 0).toFixed(1) + ', ' + (r.posY || 0).toFixed(1) + ', ' + (r.posZ || 0).toFixed(1) + ')</div>' : '') +
            '</div>';
    }

    function renderSpam(r) {
        return '<div class="sl-entry">' +
            '<div class="sl-entry-header">' +
            '<span class="sl-entry-time">' + formatTime(r.time) + '</span>' +
            '<span class="sl-entry-badge sl-badge-lostsocket">Spam</span>' +
            '<span style="font-size: 12.5px; color: var(--text-secondary);">' +
            'Account ' + r.accountId + ' · GUID ' + r.guid + '</span>' +
            '</div>' +
            '<div class="sl-chat-msg">' + escapeHtml(r.message) + '</div>' +
            '<div class="sl-entry-body" style="margin-top: 4px;">Reason: ' + escapeHtml(r.reason) + '</div>' +
            '</div>';
    }

    function renderBehavior(r) {
        return '<div class="sl-entry">' +
            '<div class="sl-entry-header">' +
            '<span class="sl-entry-badge sl-badge-lostsocket">Detection</span>' +
            '<span style="font-size: 12.5px; color: var(--text-secondary);">' +
            'Account ' + r.account + '</span>' +
            '</div>' +
            '<div class="sl-entry-body">' +
            '<strong>' + escapeHtml(r.detection) + '</strong>: ' + escapeHtml(r.data) +
            '</div></div>';
    }

    function renderBattleground(r) {
        var bgNames = { 1: 'Alterac Valley', 2: 'Warsong Gulch', 3: 'Arathi Basin' };
        var bgName = bgNames[r.bgtype] || 'BG Type ' + r.bgtype;
        var team = r.team === 0 ? 'Alliance' : 'Horde';
        var duration = r.bgduration ? Math.round(r.bgduration / 60) + ' min' : '—';

        return '<div class="sl-entry">' +
            '<div class="sl-entry-header">' +
            '<span class="sl-entry-time">' + formatTime(r.time) + '</span>' +
            '<span class="sl-entry-badge sl-badge-bg">' + escapeHtml(bgName) + '</span>' +
            '<span style="font-size: 12.5px; color: var(--text-secondary);">' +
            'GUID ' + r.playerGuid + ' · ' + team + ' · ' + duration + '</span>' +
            '</div>' +
            '<div class="sl-entry-body">' +
            'Deaths: ' + r.deaths + ' · HKs: ' + r.honorableKills + ' · Honor: ' + r.honorBonus +
            '</div></div>';
    }

    // ===================== PAGINATION =====================
    $('#slPrev').on('click', function () {
        if (currentPage > 1) { currentPage--; loadTabData(); }
    });
    $('#slNext').on('click', function () {
        currentPage++;
        loadTabData();
    });

    // ===================== HELPERS =====================
    function formatTime(ts) {
        if (!ts) return '—';
        var d = new Date(ts);
        var pad = function (n) { return n < 10 ? '0' + n : n; };
        return d.getFullYear() + '-' + pad(d.getMonth() + 1) + '-' + pad(d.getDate()) + ' ' +
               pad(d.getHours()) + ':' + pad(d.getMinutes()) + ':' + pad(d.getSeconds());
    }

    function formatMoney(amount) {
        if (!amount) return '0g';
        var g = Math.floor(amount / 10000);
        var s = Math.floor((amount % 10000) / 100);
        var c = amount % 100;
        return g + 'g ' + s + 's ' + c + 'c';
    }

    function escapeHtml(text) {
        if (!text && text !== 0) return '';
        var div = document.createElement('div');
        div.textContent = text;
        return div.innerHTML;
    }

    function escapeAttr(text) {
        return String(text).replace(/"/g, '&quot;').replace(/'/g, '&#39;');
    }

});

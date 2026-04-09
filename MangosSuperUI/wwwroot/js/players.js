// MangosSuperUI — Player Actions Page JS

$(function () {

    // ===================== STATE =====================
    var selectedPlayer = null; // { guid, name, accountName }
    var searchTimer = null;

    // ===================== SEARCH =====================
    var $search = $('#playerSearch');
    var $dropdown = $('#searchDropdown');

    $search.on('input', function () {
        var q = $(this).val().trim();
        clearTimeout(searchTimer);

        if (q.length < 3) {
            $dropdown.hide().empty();
            return;
        }

        searchTimer = setTimeout(function () {
            $.getJSON('/Players/Search?q=' + encodeURIComponent(q), function (results) {
                $dropdown.empty();

                if (results.length === 0) {
                    $dropdown.append('<div style="padding: 12px 16px; color: var(--text-muted); font-size: 13px;">No characters found</div>');
                } else {
                    for (var i = 0; i < results.length; i++) {
                        var r = results[i];
                        var html = '<div class="search-result" data-guid="' + r.guid + '" data-name="' + escapeAttr(r.name) + '" data-account="' + escapeAttr(r.accountName) + '">';
                        html += '<div><span class="search-result-name">' + escapeHtml(r.name) + '</span>';
                        html += ' <span class="search-result-detail">Lv' + r.level + ' ' + escapeHtml(r.race) + ' ' + escapeHtml(r.className) + '</span></div>';
                        html += '<div>';
                        if (r.online) html += '<span class="search-result-online">ONLINE</span> ';
                        html += '<span class="search-result-detail">' + escapeHtml(r.accountName) + '</span>';
                        html += '</div></div>';
                        $dropdown.append(html);
                    }
                }

                $dropdown.show();
            });
        }, 250);
    });

    // Select from dropdown
    $dropdown.on('click', '.search-result', function () {
        var guid = $(this).data('guid');
        var name = $(this).data('name');
        var account = $(this).data('account');

        selectedPlayer = { guid: guid, name: name, accountName: account };
        $search.val(name);
        $dropdown.hide();
        loadPlayerDetail(guid);
    });

    // Close dropdown on click outside
    $(document).on('click', function (e) {
        if (!$(e.target).closest('.player-search-wrap').length) {
            $dropdown.hide();
        }
    });

    // Enter key selects first result
    $search.on('keydown', function (e) {
        if (e.key === 'Enter') {
            e.preventDefault();
            var $first = $dropdown.find('.search-result').first();
            if ($first.length) $first.click();
        }
    });

    // ===================== LOAD DETAIL =====================
    function loadPlayerDetail(guid) {
        $.getJSON('/Players/Detail?guid=' + guid, function (data) {
            if (!data.found) {
                paOutput('Player not found.', 'err');
                return;
            }

            var p = data.player;
            var g = data.guild;
            var a = data.account;

            // Header
            $('#pName').text(p.name);
            $('#pStatus').text(p.online ? 'Online' : 'Offline')
                .removeClass('online offline').addClass(p.online ? 'online' : 'offline');
            $('#pLevel').text(p.level);
            $('#pRace').text(p.race);
            $('#pClass').text(p.className);
            $('#pGender').text(p.gender);

            // Guild
            if (g) {
                $('#pGuild').text(g.guildName + ' (Rank ' + g.guildRank + ')');
            } else {
                $('#pGuild').text('None');
            }

            // Account
            if (a) {
                $('#pAccount').text(a.username + ' (#' + a.id + ')');
                if (a.gmLevel > 0) {
                    $('#pGmBadge').text(a.gmLevelName).show();
                } else {
                    $('#pGmBadge').hide();
                }
            }

            // Stats
            $('#pGold').text(p.gold + 'g ' + p.silver + 's ' + p.copper + 'c');
            $('#pXp').text(p.xp.toLocaleString());
            $('#pPlayed').text(p.playedTotal);
            $('#pCreated').text(p.createTime);
            $('#pLastSeen').text(p.online ? 'Now' : p.logoutTime);
            $('#pZone').text(p.zone + ' / Map ' + p.map);
            $('#pRankInfo').text(p.highestRank > 0 ? 'Rank ' + p.highestRank + ' (' + Math.round(p.rankPoints) + ' pts)' : 'None');

            if (a) {
                $('#pLastIp').text(a.lastIp);
            }

            // Show panel, hide empty state
            $('#emptyState').hide();
            $('#playerPanel').show();
        });
    }

    // Refresh button
    $('#btnRefreshPlayer').on('click', function () {
        if (selectedPlayer) loadPlayerDetail(selectedPlayer.guid);
    });

    // ===================== ACTIONS =====================
    function sendPlayerCommand(cmd) {
        if (!cmd) return;

        paOutput('> ' + cmd, 'cmd');

        $.ajax({
            url: '/Players/RaCommand',
            type: 'POST',
            contentType: 'application/json',
            data: JSON.stringify({ command: cmd }),
            success: function (data) {
                if (data.success) {
                    paOutput(data.response || 'OK', 'ok');
                } else {
                    paOutput('Error: ' + data.error, 'err');
                }
            },
            error: function (xhr) {
                paOutput('Request failed: ' + xhr.statusText, 'err');
            },
            complete: function () {
                // Auto-refresh player data after actions
                if (selectedPlayer) {
                    setTimeout(function () { loadPlayerDetail(selectedPlayer.guid); }, 1500);
                }
            }
        });
    }

    // Quick action buttons
    $(document).on('click', '.pa-btn', function () {
        if (!selectedPlayer) { paOutput('No player selected.', 'err'); return; }

        var action = $(this).data('action');
        var name = selectedPlayer.name;
        var acct = selectedPlayer.accountName;
        var cmd = null;

        switch (action) {
            // Character
            case 'revive':      cmd = '.revive ' + name; break;
            case 'repair':      cmd = '.repairitems ' + name; break;
            case 'rename':      cmd = '.character rename ' + name; break;
            case 'resetTalents': cmd = '.reset talents ' + name; break;
            case 'resetSpells': cmd = '.reset spells ' + name; break;
            case 'resetAll':    cmd = '.reset all ' + name; break;
            case 'setLevel':
                var lv = $('#paLevel').val();
                cmd = '.character level ' + name + ' ' + lv;
                break;

            // Communication
            case 'kick':    cmd = '.kick ' + name; break;
            case 'mute':    cmd = '.mute ' + name + ' 30'; break;
            case 'unmute':  cmd = '.unmute ' + name; break;
            case 'sendGold':
                var gold = $('#paGold').val();
                var subj = $('#paMailSubject').val() || 'GM';
                var body = $('#paMailBody').val() || '-';
                cmd = gold ? '.send money ' + name + ' "' + subj + '" "' + body + '" ' + gold : null;
                break;
            case 'sendItem':
                var item = $('#paItem').val().trim();
                var is2 = $('#paMailSubject').val() || 'GM';
                var ib2 = $('#paMailBody').val() || '-';
                cmd = item ? '.send items ' + name + ' "' + is2 + '" "' + ib2 + '" ' + item : null;
                break;
            case 'sendMail':
                var ms = $('#paMailSubject').val() || 'GM';
                var mb = $('#paMailBody').val() || '-';
                cmd = '.send mail ' + name + ' "' + ms + '" "' + mb + '"';
                break;

            // Teleport
            case 'customTele':
                var loc = $('#paCustomTele').val().trim();
                cmd = loc ? '.tele name ' + name + ' ' + loc : null;
                break;

            // Account
            case 'banAccount':
                cmd = '.ban account ' + acct + ' -1 Banned by admin';
                break;
            case 'unbanAccount':
                cmd = '.unban account ' + acct;
                break;
            case 'banChar':
                cmd = '.ban character ' + name + ' -1 Banned by admin';
                break;
            case 'setGmLevel':
                var gl = $('#paGmLevel').val();
                cmd = '.account set gmlevel ' + acct + ' ' + gl;
                break;
            case 'setPassword':
                var np = $('#paNewPass').val().trim();
                cmd = np ? '.account set password ' + acct + ' ' + np + ' ' + np : null;
                break;
        }

        if (cmd) {
            sendPlayerCommand(cmd);
        } else {
            paOutput('Missing required field.', 'err');
        }
    });

    // Teleport preset buttons
    $(document).on('click', '.pa-tele', function () {
        if (!selectedPlayer) { paOutput('No player selected.', 'err'); return; }
        var loc = $(this).data('loc');
        sendPlayerCommand('.tele name ' + selectedPlayer.name + ' ' + loc);
    });

    // ===================== OUTPUT =====================
    function paOutput(text, type) {
        var colors = { cmd: '#7aa2f7', ok: '#c8d0da', err: '#f7768e', sys: '#9ece6a' };
        var color = colors[type] || colors.ok;
        var $el = $('#paOutput');
        $el.append('<div style="color: ' + color + ';">' + escapeHtml(text) + '</div>');
        while ($el[0].children.length > 200) $el[0].removeChild($el[0].children[0]);
        $el.scrollTop($el[0].scrollHeight);
    }

    $('#btnClearPaOutput').on('click', function () {
        $('#paOutput').empty();
    });

    // ===================== UTILITY =====================
    function escapeHtml(text) {
        var div = document.createElement('div');
        div.textContent = text;
        return div.innerHTML;
    }

    function escapeAttr(text) {
        return String(text).replace(/"/g, '&quot;').replace(/'/g, '&#39;');
    }

    // ===================== INIT =====================
    $search.focus();

});

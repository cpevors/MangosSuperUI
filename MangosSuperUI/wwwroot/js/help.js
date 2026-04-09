// MangosSuperUI — Help System
// Hover tooltips + section reference panels. No layout disruption.

var HelpSystem = (function () {

    var _data = null;
    var _visible = false;
    var _loaded = false;
    var $tip = null;

    function load(callback) {
        if (_loaded) { if (callback) callback(); return; }
        $.getJSON('/data/commands.json', function (data) {
            _data = data;
            _loaded = true;
            if (callback) callback();
        });
    }

    function init() {
        // Create the floating tooltip element once
        $tip = $('<div class="help-tip"><span class="help-tip-cmd"></span><span class="help-tip-desc"></span></div>');
        $('body').append($tip);

        // Hover handlers — delegated so they work on dynamic content
        $(document).on('mouseenter', '.cmd-quick[data-cmd], .pa-btn[data-action], .pa-tele[data-loc]', function (e) {
            if (!_visible || !_data) return;
            var desc = getDescriptionFor($(this));
            if (!desc) return;
            showTooltip(e, desc.cmd, desc.text);
        });

        $(document).on('mouseleave', '.cmd-quick[data-cmd], .pa-btn[data-action], .pa-tele[data-loc]', function () {
            hideTooltip();
        });

        // Preload data
        load();
    }

    function toggle() {
        _visible = !_visible;
        if (_visible) {
            show();
        } else {
            hide();
        }
    }

    function show() {
        if (!_loaded) { load(function () { show(); }); return; }
        _visible = true;
        $('body').addClass('help-mode');
        updateToggleButton();
        injectCurrentTabPanel();
    }

    function hide() {
        _visible = false;
        $('body').removeClass('help-mode');
        $('.help-section-panel').removeClass('help-visible');
        hideTooltip();
        updateToggleButton();
    }

    function showTooltip(e, cmd, text) {
        $tip.find('.help-tip-cmd').text(cmd);
        $tip.find('.help-tip-desc').text(text);

        // Position below the hovered element
        var $el = $(e.currentTarget);
        var rect = $el[0].getBoundingClientRect();
        var tipLeft = rect.left;
        var tipTop = rect.bottom + 8;

        // Keep within viewport
        $tip.css({ left: 0, top: 0 }).addClass('visible');
        var tipWidth = $tip.outerWidth();
        if (tipLeft + tipWidth > window.innerWidth - 16) {
            tipLeft = window.innerWidth - tipWidth - 16;
        }
        if (tipLeft < 8) tipLeft = 8;

        // If tooltip would go below viewport, show above
        var tipHeight = $tip.outerHeight();
        if (tipTop + tipHeight > window.innerHeight - 8) {
            tipTop = rect.top - tipHeight - 8;
            // Flip the arrow
            $tip.css('--arrow-flip', 'rotate(225deg)');
        }

        $tip.css({ left: tipLeft + 'px', top: tipTop + 'px' });
    }

    function hideTooltip() {
        if ($tip) $tip.removeClass('visible');
    }

    function getDescriptionFor($el) {
        // Quick-fire command buttons
        var cmd = $el.data('cmd');
        if (cmd) {
            var desc = findDescription(cmd);
            return desc ? { cmd: cmd, text: desc } : null;
        }

        // Player action buttons
        var action = $el.data('action');
        if (action) {
            var mapped = mapActionToCommand(action);
            if (mapped) {
                var desc = findDescription(mapped);
                return desc ? { cmd: mapped, text: desc } : null;
            }
        }

        // Teleport preset buttons
        var loc = $el.data('loc');
        if (loc) {
            return { cmd: '.tele name $player ' + loc, text: 'Teleport the selected player to ' + loc + '. Works on offline players.' };
        }

        return null;
    }

    function findDescription(cmd) {
        if (!_data) return null;
        for (var section in _data) {
            var cmds = _data[section].commands;
            if (cmds[cmd]) return cmds[cmd];
        }
        // Partial match
        for (var section in _data) {
            var cmds = _data[section].commands;
            for (var key in cmds) {
                if (cmd.indexOf(key) === 0 || key.indexOf(cmd) === 0) {
                    return cmds[key];
                }
            }
        }
        return null;
    }

    function mapActionToCommand(action) {
        var map = {
            'revive': '.revive', 'repair': '.repairitems', 'rename': '.character rename',
            'resetTalents': '.reset talents', 'resetSpells': '.reset spells', 'resetAll': '.reset all',
            'setLevel': '.character level', 'kick': '.kick', 'mute': '.mute', 'unmute': '.unmute',
            'sendGold': '.send money', 'sendItem': '.send items', 'sendMail': '.send mail',
            'customTele': '.tele name', 'banAccount': '.ban account', 'unbanAccount': '.unban account',
            'banChar': '.ban character', 'setGmLevel': '.account set gmlevel', 'setPassword': '.account set password',
            'accCreate': '.account create', 'accGmLevel': '.account set gmlevel', 'accSetPass': '.account set password',
            'accChars': '.account characters', 'charLevel': '.character level', 'charRename': '.character rename',
            'pinfo': '.pinfo', 'revivePlayer': '.revive', 'repairPlayer': '.repairitems',
            'sendMoney': '.send money', 'sendItems': '.send items',
            'resetLevel': '.reset level', 'resetItems': '.reset items',
            'shutdown': '.server shutdown', 'restart': '.server restart',
            'announce': '.announce', 'notify': '.notify', 'setmotd': '.server set motd',
            'ban': '.ban account', 'unban': '.unban account',
            'eventStart': '.event start', 'eventStop': '.event stop',
            'botAdd': '.bot add', 'botDelete': '.bot delete',
            'lookup': '.lookup item', 'lookupPlayer': '.lookup player ip',
            'spellInfo': '.spell info', 'spellEffects': '.spell effects',
            'guildCreate': '.guild create', 'guildDelete': '.guild delete',
            'guildInvite': '.guild invite', 'guildUninvite': '.guild uninvite',
            'guildRankSet': '.guild rank', 'guildRename': '.guild rename',
            'teleName': '.tele name', 'teleDel': '.tele del', 'lookupTele': '.lookup tele',
            'antispamAdd': '.antispam add', 'antispamRemove': '.antispam remove',
            'antispamReplace': '.antispam replace', 'antispamRemoveReplace': '.antispam removereplace',
            'spamerMute': '.spamer mute', 'spamerUnmute': '.spamer unmute',
            'pdumpWrite': '.pdump write', 'pdumpLoad': '.pdump load',
            'petList': '.pet list', 'petRename': '.pet rename', 'petDelete': '.pet delete',
            'massMail': '.send mass mail', 'massMoney': '.send mass money', 'massItems': '.send mass items'
        };
        return map[action] || null;
    }

    function injectCurrentTabPanel() {
        var activeTab = $('.console-tab.active').data('tab');
        if (activeTab && _data && _data[activeTab]) {
            injectSectionHelp(activeTab);
        }
    }

    function injectSectionHelp(tabId) {
        var section = _data[tabId];
        if (!section) return;

        var $panel = $('#panel-' + tabId);
        if ($panel.find('.help-section-panel').length) {
            $panel.find('.help-section-panel').addClass('help-visible');
            return;
        }

        var html = '<div class="help-section-panel help-visible">';
        html += '<div class="help-section-header"><i class="fa-solid fa-circle-question"></i> ' + escapeHtml(section.label) + '</div>';
        html += '<div class="help-section-body">';

        var cmds = section.commands;
        for (var cmd in cmds) {
            html += '<div class="help-entry">';
            html += '<code class="help-cmd">' + escapeHtml(cmd) + '</code>';
            html += '<span class="help-desc">' + escapeHtml(cmds[cmd]) + '</span>';
            html += '</div>';
        }

        html += '</div></div>';
        $panel.prepend(html);
    }

    function updateToggleButton() {
        var $btn = $('#btnToggleHelp');
        if ($btn.length) {
            $btn.toggleClass('help-active', _visible);
            $btn.attr('title', _visible ? 'Hide command help' : 'Show command help');
        }
    }

    function escapeHtml(text) {
        var div = document.createElement('div');
        div.textContent = text;
        return div.innerHTML;
    }

    // Re-inject panel on tab switch
    $(document).on('click', '.console-tab', function () {
        if (_visible) {
            // Hide all section panels first
            $('.help-section-panel').removeClass('help-visible');
            setTimeout(injectCurrentTabPanel, 50);
        }
    });

    // Init on DOM ready
    $(init);

    return {
        toggle: toggle,
        show: show,
        hide: hide,
        isVisible: function () { return _visible; }
    };

})();
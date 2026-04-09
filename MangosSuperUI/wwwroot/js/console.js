// MangosSuperUI — Console Page JS (Command Panel + Terminal)

$(function () {

    // ===================== STATE =====================
    var connection = null;
    var connected = false;
    var termHistory = [];
    var termHistoryIdx = -1;
    var maxHistory = 100;

    // ===================== SIGNALR =====================
    function initConnection() {
        connection = new signalR.HubConnectionBuilder()
            .withUrl('/hubs/console')
            .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
            .build();

        connection.on('ReceiveResponse', function (response, success) {
            if (!response) return;
            var lines = response.split('\n');
            for (var i = 0; i < lines.length; i++) {
                if (lines[i].trim() === '') continue;
                // Route to the right output
                var activeTab = $('.console-tab.active').data('tab');
                if (activeTab === 'terminal') {
                    termAppend(lines[i], success ? 'line-ok' : 'line-err');
                } else {
                    cmdAppend(lines[i], success ? 'line-ok' : 'line-err');
                }
            }
        });

        connection.on('ConnectionStatus', function (isConnected, message) {
            if (isConnected) {
                setStatus('online');
            } else {
                setStatus('error');
                cmdAppend('RA connection failed: ' + message, 'line-err');
            }
        });

        connection.onreconnecting(function () {
            setStatus('offline');
        });

        connection.onreconnected(function () {
            setStatus('online');
        });

        connection.onclose(function () {
            setStatus('offline');
        });

        connection.start()
            .then(function () {
                setStatus('online');
                connection.invoke('TestConnection').catch(function () { });
            })
            .catch(function (err) {
                setStatus('error');
                cmdAppend('Failed to connect: ' + err.toString(), 'line-err');
            });
    }

    function setStatus(state) {
        connected = (state === 'online');
        $('#consoleStatus').removeClass('online offline error').addClass(state);
        var labels = { online: 'RA: Connected', offline: 'RA: Disconnected', error: 'RA: Error' };
        $('#consoleStatusText').text(labels[state] || state);
    }

    function sendRaCommand(cmd) {
        if (!cmd) return;
        if (!connected) {
            cmdAppend('Not connected to RA.', 'line-err');
            return;
        }
        cmdAppend('> ' + cmd, 'line-cmd');
        connection.invoke('SendCommand', cmd).catch(function (err) {
            cmdAppend('Send failed: ' + err.toString(), 'line-err');
        });
    }

    // ===================== CMD OUTPUT (panels) =====================
    function cmdAppend(text, cls) {
        var div = document.createElement('div');
        div.className = cls || 'line-ok';
        div.textContent = text;
        var el = document.getElementById('cmdOutput');
        el.appendChild(div);
        // Trim to 500 lines
        while (el.children.length > 500) el.removeChild(el.children[0]);
        el.scrollTop = el.scrollHeight;
    }

    $('#btnClearResponse').on('click', function () {
        $('#cmdOutput').empty();
    });

    // ===================== TERMINAL OUTPUT =====================
    function termAppend(text, cls) {
        var div = document.createElement('div');
        div.className = cls || 'line-ok';
        div.textContent = text;
        var el = document.getElementById('terminalOutput');
        el.appendChild(div);
        while (el.children.length > 2000) el.removeChild(el.children[0]);
        var term = document.getElementById('consoleTerminal');
        term.scrollTop = term.scrollHeight;
    }

    // ===================== TAB SWITCHING =====================
    $('#consoleTabs').on('click', '.console-tab', function () {
        var tab = $(this).data('tab');
        $('.console-tab').removeClass('active');
        $(this).addClass('active');
        $('.console-panel').removeClass('active');
        $('#panel-' + tab).addClass('active');

        // Show/hide response bar (hide for terminal tab)
        if (tab === 'terminal') {
            $('#responseBar').hide();
        } else {
            $('#responseBar').show();
        }
    });

    // ===================== QUICK COMMANDS =====================
    $(document).on('click', '.cmd-quick', function () {
        var cmd = $(this).data('cmd');
        if (cmd) sendRaCommand(cmd);
    });

    // ===================== COMMAND BUILDERS =====================
    $(document).on('click', '.cmd-build', function () {
        var build = $(this).data('build');
        var cmd = buildCommand(build);
        if (cmd) sendRaCommand(cmd);
    });

    function buildCommand(id) {
        switch (id) {
            // --- Server ---
            case 'shutdown':
                return '.server shutdown ' + ($('#shutdownDelay').val() || '30');
            case 'restart':
                return '.server restart ' + ($('#restartDelay').val() || '30');
            case 'announce':
                var msg = $('#announceMsg').val().trim();
                return msg ? '.announce ' + msg : null;
            case 'notify':
                var nmsg = $('#announceMsg').val().trim();
                return nmsg ? '.notify ' + nmsg : null;
            case 'setmotd':
                var motd = $('#motdMsg').val().trim();
                return motd ? '.server set motd ' + motd : null;

            // --- Accounts ---
            case 'accCreate':
                var name = $('#accCreateName').val().trim();
                var pass = $('#accCreatePass').val().trim();
                return (name && pass) ? '.account create ' + name + ' ' + pass : null;
            case 'accGmLevel':
                var gname = $('#accGmName').val().trim();
                var glvl = $('#accGmLevel').val();
                return gname ? '.account set gmlevel ' + gname + ' ' + glvl : null;
            case 'accSetPass':
                var pname = $('#accPassName').val().trim();
                var ppass = $('#accPassNew').val().trim();
                return (pname && ppass) ? '.account set password ' + pname + ' ' + ppass + ' ' + ppass : null;
            case 'accChars':
                var cname = $('#accCharName').val().trim();
                return cname ? '.account characters ' + cname : null;

            // --- Players ---
            case 'pinfo':
                var pn = $('#playerName').val().trim();
                return pn ? '.pinfo ' + pn : '.pinfo';
            case 'kick':
                var kn = $('#playerName').val().trim();
                return kn ? '.kick ' + kn : null;
            case 'revivePlayer':
                var rn = $('#playerName').val().trim();
                return rn ? '.revive ' + rn : '.revive';
            case 'repairPlayer':
                var rpn = $('#playerName').val().trim();
                return rpn ? '.repairitems ' + rpn : '.repairitems';
            case 'charRename':
                var crn = $('#playerName').val().trim();
                return crn ? '.character rename ' + crn : null;
            case 'charLevel':
                var ln = $('#levelPlayerName').val().trim();
                var lv = $('#levelValue').val();
                return ln ? '.character level ' + ln + ' ' + lv : null;

            // --- Send ---
            case 'sendMoney':
                var st = $('#sendTarget').val().trim();
                var subj = $('#sendSubject').val().trim() || 'GM';
                var body = $('#sendBody').val().trim() || '-';
                var gold = $('#sendGold').val();
                return (st && gold) ? '.send money ' + st + ' "' + subj + '" "' + body + '" ' + gold : null;
            case 'sendItems':
                var sit = $('#sendTarget').val().trim();
                var sis = $('#sendSubject').val().trim() || 'GM';
                var sib = $('#sendBody').val().trim() || '-';
                var item = $('#sendItem').val().trim();
                return (sit && item) ? '.send items ' + sit + ' "' + sis + '" "' + sib + '" ' + item : null;
            case 'sendMail':
                var smt = $('#sendTarget').val().trim();
                var sms = $('#sendSubject').val().trim() || 'GM';
                var smb = $('#sendBody').val().trim() || '-';
                return smt ? '.send mail ' + smt + ' "' + sms + '" "' + smb + '"' : null;

            // --- Mute ---
            case 'mute':
                var mn = $('#muteName').val().trim();
                var mm = $('#muteMinutes').val();
                return mn ? '.mute ' + mn + ' ' + mm : null;
            case 'unmute':
                var un = $('#muteName').val().trim();
                return un ? '.unmute ' + un : null;

            // --- Reset ---
            case 'resetTalents':
                var rt = $('#resetPlayerName').val().trim();
                return rt ? '.reset talents ' + rt : null;
            case 'resetLevel':
                var rl = $('#resetPlayerName').val().trim();
                return rl ? '.reset level ' + rl : null;
            case 'resetSpells':
                var rs = $('#resetPlayerName').val().trim();
                return rs ? '.reset spells ' + rs : null;
            case 'resetItems':
                var ri = $('#resetPlayerName').val().trim();
                return ri ? '.reset items ' + ri : null;
            case 'resetAll':
                var ra = $('#resetPlayerName').val().trim();
                return ra ? '.reset all ' + ra : null;

            // --- Bans ---
            case 'ban':
                var bt = $('#banType').val();
                var bn = $('#banTarget').val().trim();
                var bd = $('#banDuration').val().trim() || '-1';
                var br = $('#banReason').val().trim() || 'No reason';
                return bn ? '.ban ' + bt + ' ' + bn + ' ' + bd + ' ' + br : null;
            case 'unban':
                var ut = $('#unbanType').val();
                var ubn = $('#unbanTarget').val().trim();
                return ubn ? '.unban ' + ut + ' ' + ubn : null;

            // --- Events ---
            case 'eventStart':
                var eid = $('#eventId').val();
                return eid ? '.event start ' + eid : null;
            case 'eventStop':
                var esid = $('#eventId').val();
                return esid ? '.event stop ' + esid : null;

            // --- Bots ---
            case 'botAdd':
                var bp = $('#botAddParam').val().trim();
                return bp ? '.bot add ' + bp : null;
            case 'botDelete':
                var bdp = $('#botAddParam').val().trim();
                return bdp ? '.bot delete ' + bdp : null;

            // --- Lookup ---
            case 'lookup':
                var lt = $('#lookupType').val();
                var lterm = $('#lookupTerm').val().trim();
                return lterm ? '.lookup ' + lt + ' ' + lterm : null;
            case 'lookupPlayer':
                var plt = $('#playerLookupType').val();
                var plterm = $('#playerLookupTerm').val().trim();
                return plterm ? '.lookup player ' + plt + ' ' + plterm : null;
            case 'spellInfo':
                var sid = $('#spellInfoId').val();
                return sid ? '.spell info ' + sid : null;
            case 'spellEffects':
                var seid = $('#spellInfoId').val();
                return seid ? '.spell effects ' + seid : null;

            // --- Guild ---
            case 'guildCreate':
                var gcl = $('#guildLeader').val().trim();
                var gcn = $('#guildName').val().trim();
                return (gcl && gcn) ? '.guild create ' + gcl + ' "' + gcn + '"' : null;
            case 'guildDelete':
                var gdn = $('#guildName').val().trim();
                return gdn ? '.guild delete "' + gdn + '"' : null;
            case 'guildInvite':
                var gim = $('#guildMember').val().trim();
                var gin = $('#guildName').val().trim();
                return (gim && gin) ? '.guild invite ' + gim + ' "' + gin + '"' : null;
            case 'guildUninvite':
                var gum = $('#guildMember').val().trim();
                return gum ? '.guild uninvite ' + gum : null;
            case 'guildRankSet':
                var grm = $('#guildMember').val().trim();
                var grr = $('#guildRank').val();
                return grm ? '.guild rank ' + grm + ' ' + grr : null;
            case 'guildRename':
                var gon = $('#guildOldName').val().trim();
                var gnn = $('#guildNewName').val().trim();
                return (gon && gnn) ? '.guild rename "' + gon + '" "' + gnn + '"' : null;

            // --- Teleport ---
            case 'teleName':
                var tp = $('#telePlayer').val().trim();
                var tl = $('#teleLocation').val().trim();
                return (tp && tl) ? '.tele name ' + tp + ' ' + tl : null;
            case 'teleDel':
                var tdn = $('#teleDelName').val().trim();
                return tdn ? '.tele del ' + tdn : null;
            case 'lookupTele':
                var tlk = $('#teleLookup').val().trim();
                return tlk ? '.lookup tele ' + tlk : null;

            // --- Chat / Antispam ---
            case 'antispamAdd':
                var aaw = $('#antispamWord').val().trim();
                return aaw ? '.antispam add ' + aaw : null;
            case 'antispamRemove':
                var arw = $('#antispamWord').val().trim();
                return arw ? '.antispam remove ' + arw : null;
            case 'antispamReplace':
                var arf = $('#antispamFrom').val().trim();
                var art = $('#antispamTo').val().trim();
                return (arf && art) ? '.antispam replace ' + arf + ' ' + art : null;
            case 'antispamRemoveReplace':
                var arrf = $('#antispamFrom').val().trim();
                return arrf ? '.antispam removereplace ' + arrf : null;
            case 'spamerMute':
                var smn = $('#spamerName').val().trim();
                return smn ? '.spamer mute ' + smn : null;
            case 'spamerUnmute':
                var sun = $('#spamerName').val().trim();
                return sun ? '.spamer unmute ' + sun : null;

            // --- More: pdump ---
            case 'pdumpWrite':
                var pwf = $('#pdumpFile').val().trim();
                var pwc = $('#pdumpChar').val().trim();
                return (pwf && pwc) ? '.pdump write ' + pwf + ' ' + pwc : null;
            case 'pdumpLoad':
                var plf = $('#pdumpLoadFile').val().trim();
                var pla = $('#pdumpLoadAcct').val().trim();
                return (plf && pla) ? '.pdump load ' + plf + ' ' + pla : null;

            // --- More: pet ---
            case 'petList':
                var plp = $('#petParam').val().trim();
                return plp ? '.pet list ' + plp : '.pet list';
            case 'petRename':
                var prp = $('#petParam').val().trim();
                return prp ? '.pet rename ' + prp : null;
            case 'petDelete':
                var pdp = $('#petParam').val().trim();
                return pdp ? '.pet delete ' + pdp : null;

            // --- More: mass send ---
            case 'massMail':
                var mms = $('#massSubject').val().trim() || 'Server Notice';
                var mmb = $('#massBody').val().trim() || '-';
                return '.send mass mail "' + mms + '" "' + mmb + '"';
            case 'massMoney':
                var mmons = $('#massSubject').val().trim() || 'Server Notice';
                var mmonb = $('#massBody').val().trim() || '-';
                var mmonv = $('#massValue').val().trim();
                return mmonv ? '.send mass money "' + mmons + '" "' + mmonb + '" ' + mmonv : null;
            case 'massItems':
                var mis = $('#massSubject').val().trim() || 'Server Notice';
                var mib = $('#massBody').val().trim() || '-';
                var miv = $('#massValue').val().trim();
                return miv ? '.send mass items "' + mis + '" "' + mib + '" ' + miv : null;

            default:
                cmdAppend('Unknown command builder: ' + id, 'line-err');
                return null;
        }
    }

    // ===================== TERMINAL INPUT =====================
    function sendTerminalCommand() {
        var cmd = $('#terminalInput').val().trim();
        if (!cmd) return;

        // History
        if (termHistory.length === 0 || termHistory[termHistory.length - 1] !== cmd) {
            termHistory.push(cmd);
            if (termHistory.length > maxHistory) termHistory.shift();
        }
        termHistoryIdx = termHistory.length;

        termAppend('> ' + cmd, 'line-cmd');
        $('#terminalInput').val('');

        if (cmd.toLowerCase() === 'clear') {
            $('#terminalOutput').empty();
            termAppend('Console cleared.', 'line-sys');
            return;
        }

        if (!connected) {
            termAppend('Not connected to RA.', 'line-err');
            return;
        }

        // For terminal, we need responses routed to terminal output
        // The ReceiveResponse handler checks the active tab
        connection.invoke('SendCommand', cmd).catch(function (err) {
            termAppend('Send failed: ' + err.toString(), 'line-err');
        });
    }

    $('#btnSendTerminal').on('click', sendTerminalCommand);

    $('#terminalInput').on('keydown', function (e) {
        if (e.key === 'Enter') {
            e.preventDefault();
            sendTerminalCommand();
        } else if (e.key === 'ArrowUp') {
            e.preventDefault();
            if (termHistory.length > 0 && termHistoryIdx > 0) {
                termHistoryIdx--;
                $(this).val(termHistory[termHistoryIdx]);
            }
        } else if (e.key === 'ArrowDown') {
            e.preventDefault();
            if (termHistoryIdx < termHistory.length - 1) {
                termHistoryIdx++;
                $(this).val(termHistory[termHistoryIdx]);
            } else {
                termHistoryIdx = termHistory.length;
                $(this).val('');
            }
        } else if (e.key === 'l' && e.ctrlKey) {
            e.preventDefault();
            $('#terminalOutput').empty();
            termAppend('Console cleared.', 'line-sys');
        }
    });

    $('#btnClearTerminal').on('click', function () {
        $('#terminalOutput').empty();
        termAppend('Console cleared.', 'line-sys');
    });

    // Click terminal to focus input
    $('#consoleTerminal').on('click', function () {
        if (window.getSelection().toString() === '') {
            $('#terminalInput').focus();
        }
    });

    // ===================== ENTER KEY ON FORM INPUTS =====================
    // Allow pressing Enter in any cmd-param input to trigger the nearest cmd-build button
    $(document).on('keydown', '.cmd-param', function (e) {
        if (e.key === 'Enter') {
            e.preventDefault();
            var $card = $(this).closest('.cmd-card, .cmd-section');
            var $btn = $card.find('.cmd-build').first();
            if ($btn.length) $btn.click();
        }
    });

    // ===================== INIT =====================
    initConnection();

});
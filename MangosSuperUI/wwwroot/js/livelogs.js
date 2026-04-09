// MangosSuperUI — Live Logs Page JS

$(function () {

    // ===================== STATE =====================
    var connection = null;
    var activeLog = null;
    var isPaused = false;
    var pauseBuffer = [];
    var lineCount = 0;
    var maxLines = 2000;
    var searchTimer = null;

    // ===================== SIGNALR INIT =====================
    connection = new signalR.HubConnectionBuilder()
        .withUrl('/hubs/logs')
        .withAutomaticReconnect([0, 1000, 2000, 5000, 10000])
        .build();

    connection.on('AvailableLogs', function (logs) {
        renderFileButtons(logs);
    });

    connection.on('LogInitial', function (logName, lines) {
        if (logName !== activeLog) return;
        var $out = $('#llOutput');
        $out.empty();
        lineCount = 0;
        for (var i = 0; i < lines.length; i++) {
            appendLine(lines[i]);
        }
        scrollToBottom();
    });

    connection.on('LogLine', function (logName, line) {
        if (logName !== activeLog) return;
        if (isPaused) {
            pauseBuffer.push(line);
            return;
        }
        appendLine(line);
        if ($('#autoScroll').is(':checked')) scrollToBottom();
    });

    connection.on('LogError', function (msg) {
        appendLine('[ERROR] ' + msg);
    });

    connection.onreconnecting(function () {
        setStatus('offline', 'Reconnecting...');
    });

    connection.onreconnected(function () {
        setStatus('online', 'Connected');
        if (activeLog) {
            connection.invoke('Subscribe', activeLog, 100);
        }
    });

    connection.onclose(function () {
        setStatus('offline', 'Disconnected');
    });

    // Start connection
    connection.start().then(function () {
        setStatus('online', 'Connected');
        connection.invoke('GetAvailableLogs');
    }).catch(function (err) {
        setStatus('offline', 'Connection failed');
        console.error('SignalR error:', err);
        $('#logFiles').html('<div class="text-muted" style="padding: 12px;">Failed to connect to log stream. Check that the hub is mapped.</div>');
    });

    // ===================== FILE BUTTONS =====================
    function renderFileButtons(logs) {
        var $files = $('#logFiles');
        $files.empty();

        // Sort: files with content first, then by name
        logs.sort(function (a, b) {
            if (a.size > 0 && b.size === 0) return -1;
            if (a.size === 0 && b.size > 0) return 1;
            return a.name.localeCompare(b.name);
        });

        for (var i = 0; i < logs.length; i++) {
            var f = logs[i];
            if (!f.exists) continue;

            var emptyClass = f.size === 0 ? ' empty' : '';
            var html = '<button class="ll-file' + emptyClass + '" data-log="' + escapeAttr(f.name) + '">';
            html += '<i class="fa-solid ' + getLogIcon(f.name) + '"></i>';
            html += escapeHtml(f.name);
            if (f.size > 0) html += '<span class="ll-file-size">' + f.sizeFormatted + '</span>';
            html += '</button>';
            $files.append(html);
        }

        // Auto-select Server log
        var $server = $files.find('[data-log="Server"]');
        if ($server.length) $server.click();
    }

    function getLogIcon(name) {
        var icons = {
            'Server': 'fa-server', 'Chat': 'fa-comments', 'RA': 'fa-terminal',
            'Network': 'fa-network-wired', 'Performance': 'fa-gauge-high',
            'DBErrors': 'fa-database', 'Anticheat': 'fa-shield-virus',
            'Character': 'fa-user', 'Loot': 'fa-box-open', 'Trades': 'fa-right-left',
            'LevelUp': 'fa-arrow-up', 'Battleground': 'fa-flag',
            'Scripts': 'fa-code', 'Movement': 'fa-person-running',
            'GMCritical': 'fa-crown', 'Realmd': 'fa-globe'
        };
        return icons[name] || 'fa-file-lines';
    }

    // ===================== TAB SWITCHING =====================
    $(document).on('click', '.ll-file', function () {
        var logName = $(this).data('log');

        // Unsubscribe from previous
        if (activeLog) {
            connection.invoke('Unsubscribe', activeLog);
        }

        // Update UI
        $('.ll-file').removeClass('active');
        $(this).addClass('active');
        activeLog = logName;

        // Clear and subscribe
        $('#llOutput').empty();
        lineCount = 0;
        pauseBuffer = [];
        updateLineCount();

        connection.invoke('Subscribe', logName, 100);
    });

    // ===================== LINE RENDERING =====================
    function appendLine(text) {
        var $out = $('#llOutput');
        var colorClass = classifyLine(text);
        var query = ($('#llSearch').val() || '').trim().toLowerCase();

        var displayText = escapeHtml(text);

        // Highlight timestamp
        displayText = displayText.replace(/^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})/, '<span class="ll-line-time">$1</span>');

        // Highlight search term
        var hidden = '';
        if (query) {
            if (text.toLowerCase().indexOf(query) >= 0) {
                var re = new RegExp('(' + query.replace(/[.*+?^${}()|[\]\\]/g, '\\$&') + ')', 'gi');
                displayText = displayText.replace(re, '<span class="ll-hl">$1</span>');
            } else {
                hidden = ' ll-line-hidden';
            }
        }

        $out.append('<div class="ll-line ' + colorClass + hidden + '">' + displayText + '</div>');

        lineCount++;
        // Trim old lines
        while ($out[0].children.length > maxLines) {
            $out[0].removeChild($out[0].children[0]);
            lineCount--;
        }

        updateLineCount();
    }

    function classifyLine(text) {
        var lower = text.toLowerCase();
        if (lower.indexOf('error') >= 0 || lower.indexOf('fatal') >= 0) return 'll-line-error';
        if (lower.indexOf('warning') >= 0 || lower.indexOf('warn') >= 0) return 'll-line-warning';
        if (lower.indexOf('[chat]') >= 0) return 'll-line-chat';
        return 'll-line-info';
    }

    function updateLineCount() {
        var visible = $('#llOutput').children(':visible').length;
        var total = $('#llOutput').children().length;
        if (visible < total) {
            $('#llLineCount').text(visible + ' / ' + total + ' lines');
        } else {
            $('#llLineCount').text(total + ' lines');
        }
    }

    function scrollToBottom() {
        var out = document.getElementById('llOutput');
        out.scrollTop = out.scrollHeight;
    }

    // ===================== SEARCH / FILTER =====================
    $('#llSearch').on('input', function () {
        clearTimeout(searchTimer);
        searchTimer = setTimeout(function () {
            var query = ($('#llSearch').val() || '').trim().toLowerCase();
            var $lines = $('#llOutput .ll-line');

            if (!query) {
                $lines.removeClass('ll-line-hidden');
                // Remove old highlights
                $lines.each(function () {
                    var $this = $(this);
                    $this.find('.ll-hl').each(function () {
                        $(this).replaceWith($(this).text());
                    });
                });
            } else {
                var re = new RegExp('(' + query.replace(/[.*+?^${}()|[\]\\]/g, '\\$&') + ')', 'gi');
                $lines.each(function () {
                    var $this = $(this);
                    var rawText = $this.text();
                    if (rawText.toLowerCase().indexOf(query) >= 0) {
                        $this.removeClass('ll-line-hidden');
                    } else {
                        $this.addClass('ll-line-hidden');
                    }
                });
            }

            updateLineCount();
        }, 200);
    });

    // ===================== PAUSE / RESUME =====================
    $('#btnPause').on('click', function () {
        isPaused = !isPaused;

        if (isPaused) {
            $(this).html('<i class="fa-solid fa-play"></i> Resume');
            setStatus('paused', 'Paused');
            // Add paused badge
            $('.main-content').css('position', 'relative');
            $('body').append('<div class="ll-paused-badge" id="pausedBadge">PAUSED</div>');
            var $mc = $('.content-body');
            $('#pausedBadge').css({ position: 'fixed', top: 80, right: 40 });
        } else {
            $(this).html('<i class="fa-solid fa-pause"></i> Pause');
            setStatus('online', 'Connected');
            $('#pausedBadge').remove();
            // Flush buffer
            for (var i = 0; i < pauseBuffer.length; i++) {
                appendLine(pauseBuffer[i]);
            }
            pauseBuffer = [];
            if ($('#autoScroll').is(':checked')) scrollToBottom();
        }
    });

    // ===================== CLEAR =====================
    $('#btnClear').on('click', function () {
        $('#llOutput').empty();
        lineCount = 0;
        updateLineCount();
    });

    // ===================== STATUS =====================
    function setStatus(state, text) {
        var $dot = $('#llStatus .ll-status-dot');
        $dot.removeClass('online offline paused').addClass(state);
        $('#llStatusText').text(text);
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

-- MangosSuperUI_Placer :: Core.lua
-- Slash commands, GM command execution, chat parsing, GPS capture,
-- directional move/turn for placed objects.

MSUI_Placer = MSUI_Placer or {}
MSUI_PlacerSettings = MSUI_PlacerSettings or {}

local P = MSUI_Placer

-- Constants
local PI = 3.14159265358979
local TWO_PI = 6.28318530717959
local MOVE_STEP = 1        -- yards per directional move click
local TURN_STEP = 15       -- degrees per rotate click

-- State
P.lastPlacedEntry = nil
P.lastPlacedName = nil
P.lastPlacedGuid = nil
P.selectedGuid = nil
P.nearbyList = {}
P.nearbyCount = 0
P.nearbyPending = false
P.debugLog = {}
P.debugCount = 0

-- GPS state — captured from .gps command output
P.gps = nil  -- { x=, y=, z=, orientation= } or nil

-- Object tracking — last known orientation for selected object
-- Updated whenever we turn an object
P.selectedOrientation = nil

-- Pending operation state
P._placeChain = nil    -- { entry=, name= }
P._gpsPending = false
P._gpsCallback = nil   -- function to call after GPS captured

-- Safe table length (table.getn is unreliable in Lua 5.0)
function P.tlen(t)
    local n = 0
    for _ in ipairs(t) do n = n + 1 end
    return n
end

-- ============================================================
-- Helpers
-- ============================================================

function P.Print(msg)
    DEFAULT_CHAT_FRAME:AddMessage("|cff00ccff[MSUI]|r " .. tostring(msg))
end

function P.PrintError(msg)
    DEFAULT_CHAT_FRAME:AddMessage("|cffff4444[MSUI]|r " .. tostring(msg))
end

function P.PrintSuccess(msg)
    DEFAULT_CHAT_FRAME:AddMessage("|cff44ff44[MSUI]|r " .. tostring(msg))
end

function P.GM(cmd)
    SendChatMessage("." .. cmd, "SAY")
end

function P.DebugLog(msg)
    table.insert(P.debugLog, msg)
    P.debugCount = P.debugCount + 1
    if P.OnDebugUpdated then P.OnDebugUpdated() end
end

-- Normalize angle to [0, 2*PI)
function P.NormalizeAngle(rad)
    while rad < 0 do rad = rad + TWO_PI end
    while rad >= TWO_PI do rad = rad - TWO_PI end
    return rad
end

-- ============================================================
-- Catalog Access
-- ============================================================

function P.GetCatalog()
    return MSUI_CATALOG or {}
end

function P.GetCatalogCount()
    local n = 0
    for _ in pairs(P.GetCatalog()) do n = n + 1 end
    return n
end

function P.GetTypeName(typeId)
    if MSUI_TYPE_NAMES and MSUI_TYPE_NAMES[typeId] then
        return MSUI_TYPE_NAMES[typeId]
    end
    return "Type " .. tostring(typeId)
end

function P.FilterCatalog(searchText, typeFilter)
    local results = {}
    local catalog = P.GetCatalog()
    local searchLower = searchText and string.lower(searchText) or nil

    for entry, data in pairs(catalog) do
        local skip = false
        if typeFilter and typeFilter > -1 and data.type ~= typeFilter then
            skip = true
        end
        if not skip and searchLower and searchLower ~= "" then
            local nameLower = string.lower(data.name or "")
            local entryStr = tostring(entry)
            if not string.find(nameLower, searchLower, 1, true) and not string.find(entryStr, searchLower, 1, true) then
                skip = true
            end
        end
        if not skip then
            table.insert(results, {
                entry = entry,
                name = data.name or "Unknown",
                type = data.type or 0,
                displayId = data.displayId or 0,
                desc = data.desc or "",
                spawns = data.spawns or 0,
            })
        end
    end
    table.sort(results, function(a, b) return a.entry < b.entry end)
    return results
end

-- ============================================================
-- Chat Parsers
-- ============================================================

-- Parse .gobject near output line
function P.ParseNearLine(msg)
    if not msg then return nil end
    msg = string.gsub(msg, "%s+$", "")

    -- Strip WoW hyperlink markup
    msg = string.gsub(msg, "|H[^|]*|h", "")
    msg = string.gsub(msg, "|h", "")

    -- Extract GUID and Entry from the start
    local _, _, guidStr, entryStr, rest = string.find(msg, "^(%d+), Entry (%d+) %- (.*)")
    if not guidStr then return nil end

    local guid = tonumber(guidStr)
    local entry = tonumber(entryStr)

    -- Extract coordinates from anywhere in the rest of the string
    -- They may be inside or outside brackets depending on scan context
    local x, y, z, mapId = 0, 0, 0, 0
    local _, _, xStr = string.find(rest, "X:([%-%.%d]+)")
    if xStr then x = tonumber(xStr) or 0 end
    local _, _, yStr = string.find(rest, "Y:([%-%.%d]+)")
    if yStr then y = tonumber(yStr) or 0 end
    local _, _, zStr = string.find(rest, "Z:([%-%.%d]+)")
    if zStr then z = tonumber(zStr) or 0 end
    local _, _, mStr = string.find(rest, "MapId:(%d+)")
    if mStr then mapId = tonumber(mStr) or 0 end

    -- Extract name: strip brackets, then strip coordinate junk
    local name = rest
    -- Remove surrounding brackets if present
    local _, _, inner = string.find(name, "^%[(.*)%]$")
    if inner then name = inner end
    -- Remove coordinate parts to get clean name
    name = string.gsub(name, "%s*X:[%-%.%d]+", "")
    name = string.gsub(name, "%s*Y:[%-%.%d]+", "")
    name = string.gsub(name, "%s*Z:[%-%.%d]+", "")
    name = string.gsub(name, "%s*MapId:%d+", "")
    -- Trim whitespace
    name = string.gsub(name, "^%s+", "")
    name = string.gsub(name, "%s+$", "")
    -- Remove leftover brackets
    name = string.gsub(name, "^%[", "")
    name = string.gsub(name, "%]$", "")

    if name == "" then name = "Unknown" end

    return { guid = guid, entry = entry, name = name, x = x, y = y, z = z, map = mapId }
end

-- Parse .gps output line (the X/Y/Z/Orientation line)
-- Format: "X: -8932.985352 Y: -128.612976 Z: 82.667183 Orientation: 1.905618"
function P.ParseGpsLine(msg)
    if not msg then return nil end
    local _, _, xStr = string.find(msg, "X: ([%-%.%d]+)")
    if not xStr then return nil end
    local _, _, yStr = string.find(msg, "Y: ([%-%.%d]+)")
    if not yStr then return nil end
    local _, _, zStr = string.find(msg, "Z: ([%-%.%d]+)")
    if not zStr then return nil end
    local _, _, oStr = string.find(msg, "Orientation: ([%-%.%d]+)")
    if not oStr then return nil end

    return {
        x = tonumber(xStr) or 0,
        y = tonumber(yStr) or 0,
        z = tonumber(zStr) or 0,
        orientation = tonumber(oStr) or 0,
    }
end

-- ============================================================
-- Chat Event Hook — routes system messages to parsers
-- ============================================================

local chatFrame = CreateFrame("Frame")
chatFrame:RegisterEvent("CHAT_MSG_SYSTEM")
chatFrame:SetScript("OnEvent", function()
    local msg = arg1
    if not msg then return end

    -- GPS capture (always check — it's cheap)
    if P._gpsPending then
        local gpsData = P.ParseGpsLine(msg)
        if gpsData then
            P.gps = gpsData
            P._gpsPending = false
            P.DebugLog("GPS: X=" .. string.format("%.2f", gpsData.x) ..
                " Y=" .. string.format("%.2f", gpsData.y) ..
                " Z=" .. string.format("%.2f", gpsData.z) ..
                " O=" .. string.format("%.4f", gpsData.orientation))
            if P._gpsCallback then
                local cb = P._gpsCallback
                P._gpsCallback = nil
                cb()
            end
            return  -- don't also process as nearby
        end
    end

    -- Nearby scan
    if P.nearbyPending then
        local msgLen = string.len(msg)
        P.DebugLog("LEN=" .. msgLen)
        P.DebugLog("START=" .. string.sub(msg, 1, 50))
        if msgLen > 30 then
            P.DebugLog("END=" .. string.sub(msg, msgLen - 29))
        end
        local firstByte = string.byte(msg, 1)
        P.DebugLog("BYTE1=" .. tostring(firstByte))
    end

    if not P.nearbyPending then return end

    local parsed = P.ParseNearLine(msg)
    if parsed then
        table.insert(P.nearbyList, parsed)
        P.nearbyCount = P.nearbyCount + 1
        P.DebugLog("PARSED: guid=" .. parsed.guid .. " entry=" .. parsed.entry .. " name=" .. parsed.name)
        if P.OnNearbyUpdated then
            P.OnNearbyUpdated()
        end
    else
        P.DebugLog("NO MATCH")
    end
end)

-- ============================================================
-- GPS Capture
-- ============================================================

-- Fire .gps and call back when we have the result
function P.CaptureGPS(callback)
    P._gpsPending = true
    P._gpsCallback = callback
    P.GM("gps")

    -- Timeout: if no GPS response in 2 seconds, cancel
    local timeout = CreateFrame("Frame")
    timeout._elapsed = 0
    timeout:SetScript("OnUpdate", function()
        timeout._elapsed = timeout._elapsed + (arg1 or 0.016)
        if timeout._elapsed >= 2 then
            timeout:SetScript("OnUpdate", nil)
            if P._gpsPending then
                P._gpsPending = false
                P._gpsCallback = nil
                P.PrintError("GPS capture timed out.")
            end
        end
    end)
end

-- ============================================================
-- Place Chain: GPS -> add -> scan -> auto-select GUID
-- ============================================================

function P.PlaceObject(entry)
    local catalog = P.GetCatalog()
    local data = catalog[entry]
    if not data then
        P.PrintError("Entry #" .. entry .. " not found in catalog.")
        return
    end

    P.lastPlacedEntry = entry
    P.lastPlacedName = data.name
    P.lastPlacedGuid = nil

    -- Step 1: Capture GPS first so we know position + orientation
    P.Print("Capturing position...")
    P.CaptureGPS(function()
        -- Step 2: Spawn at player position
        P.GM("gobject add " .. entry)
        P.PrintSuccess("Placing: " .. data.name .. " (#" .. entry .. ")")

        -- Store player orientation as the object's initial orientation
        if P.gps then
            P.selectedOrientation = P.gps.orientation
        end

        -- Step 3: After short delay, scan to find GUID
        local delay = CreateFrame("Frame")
        delay._elapsed = 0
        delay:SetScript("OnUpdate", function()
            delay._elapsed = delay._elapsed + (arg1 or 0.016)
            if delay._elapsed >= 0.5 then
                delay:SetScript("OnUpdate", nil)
                P._PlaceChainScan(entry, data.name)
            end
        end)
    end)
end

function P._PlaceChainScan(entry, name)
    P.nearbyList = {}
    P.nearbyCount = 0
    P.nearbyPending = true
    P.GM("gobject near 10")
    P.DebugLog("PLACE_CHAIN: scanning for entry " .. entry)

    local timer = CreateFrame("Frame")
    timer._elapsed = 0
    timer:SetScript("OnUpdate", function()
        timer._elapsed = timer._elapsed + (arg1 or 0.016)
        if timer._elapsed >= 2 then
            P.nearbyPending = false
            timer:SetScript("OnUpdate", nil)
            P._PlaceChainFinish(entry, name)
        end
    end)
end

function P._PlaceChainFinish(entry, name)
    local bestGuid = nil
    for _, obj in ipairs(P.nearbyList) do
        if obj.entry == entry then
            if not bestGuid or obj.guid > bestGuid then
                bestGuid = obj.guid
            end
        end
    end

    if not bestGuid then
        P.Print("Could not auto-detect GUID. Use |cffffff00/msui near|r to find it.")
        if P.OnNearbyScanComplete then P.OnNearbyScanComplete() end
        return
    end

    P.lastPlacedGuid = bestGuid
    P.selectedGuid = bestGuid
    P.PrintSuccess("Auto-selected GUID: |cffffff00" .. bestGuid .. "|r")
    P.Print("Use Face/Move/Turn controls to adjust.")

    if P.OnSelectionChanged then P.OnSelectionChanged() end
    if P.OnNearbyScanComplete then P.OnNearbyScanComplete() end
end

-- ============================================================
-- Turn Commands
-- ============================================================

-- Turn to absolute radian value
function P.TurnObjectTo(guid, radians)
    local g = tonumber(guid) or P.selectedGuid
    if not g then
        P.PrintError("No GUID selected.")
        return
    end
    local gStr = string.format("%d", g)
    local rad = P.NormalizeAngle(radians)
    local radStr = string.format("%.4f", rad)
    P.GM("gobject turn " .. gStr .. " " .. radStr)
    P.selectedOrientation = rad
    P.DebugLog("TURN: guid=" .. gStr .. " rad=" .. radStr)
end

-- Face Me: object faces toward the player's current position
function P.FaceMe()
    if not P.selectedGuid then
        P.PrintError("No GUID selected.")
        return
    end
    P.CaptureGPS(function()
        if not P.gps then
            P.PrintError("Could not get position.")
            return
        end
        -- Player facing + PI = opposite direction = object looks at player
        local rad = P.NormalizeAngle(P.gps.orientation + PI)
        P.TurnObjectTo(P.selectedGuid, rad)
        P.PrintSuccess("Facing toward you.")
    end)
end

-- Face Away: object faces same direction as player
function P.FaceAway()
    if not P.selectedGuid then
        P.PrintError("No GUID selected.")
        return
    end
    P.CaptureGPS(function()
        if not P.gps then
            P.PrintError("Could not get position.")
            return
        end
        local rad = P.NormalizeAngle(P.gps.orientation)
        P.TurnObjectTo(P.selectedGuid, rad)
        P.PrintSuccess("Facing away from you.")
    end)
end

-- Rotate Left/Right: nudge from current known orientation
function P.RotateLeft()
    if not P.selectedGuid then
        P.PrintError("No GUID selected.")
        return
    end
    if not P.selectedOrientation then
        P.PrintError("Unknown orientation. Use Face Me/Away first.")
        return
    end
    local stepRad = TURN_STEP * PI / 180
    local rad = P.NormalizeAngle(P.selectedOrientation + stepRad)
    P.TurnObjectTo(P.selectedGuid, rad)
    P.Print("Rotated left " .. TURN_STEP .. " deg.")
end

function P.RotateRight()
    if not P.selectedGuid then
        P.PrintError("No GUID selected.")
        return
    end
    if not P.selectedOrientation then
        P.PrintError("Unknown orientation. Use Face Me/Away first.")
        return
    end
    local stepRad = TURN_STEP * PI / 180
    local rad = P.NormalizeAngle(P.selectedOrientation - stepRad)
    P.TurnObjectTo(P.selectedGuid, rad)
    P.Print("Rotated right " .. TURN_STEP .. " deg.")
end

-- Turn by arbitrary degrees (slash command)
function P.TurnObject(args)
    local _, _, first, second = string.find(args or "", "^(%S+)%s*(.*)$")
    local g, deg
    if second and second ~= "" then
        g = tonumber(first)
        deg = tonumber(second) or 0
    else
        deg = tonumber(first) or 0
        g = P.selectedGuid
    end
    if not g then
        P.PrintError("No GUID. Use: /msui turn [guid] <degrees>")
        return
    end
    local radians = deg * PI / 180
    P.TurnObjectTo(g, radians)
    P.Print("Set GUID #" .. string.format("%d", g) .. " to " .. deg .. " deg (" .. string.format("%.4f", radians) .. " rad).")
end

-- ============================================================
-- Move Commands
-- ============================================================

-- Move to absolute coordinates
function P.MoveObjectTo(guid, x, y, z)
    local g = tonumber(guid) or P.selectedGuid
    if not g then
        P.PrintError("No GUID selected.")
        return
    end
    local gStr = string.format("%d", g)
    local xStr = string.format("%.2f", x)
    local yStr = string.format("%.2f", y)
    local zStr = string.format("%.2f", z)
    P.GM("gobject move " .. gStr .. " " .. xStr .. " " .. yStr .. " " .. zStr)
    P.DebugLog("MOVE: guid=" .. gStr .. " x=" .. xStr .. " y=" .. yStr .. " z=" .. zStr)
end

-- Move to player position (captures GPS, then moves with coords)
function P.MoveToMe()
    if not P.selectedGuid then
        P.PrintError("No GUID selected.")
        return
    end
    P.CaptureGPS(function()
        if not P.gps then
            P.PrintError("Could not get position.")
            return
        end
        P.MoveObjectTo(P.selectedGuid, P.gps.x, P.gps.y, P.gps.z)
        P.PrintSuccess("Moved to your position.")
    end)
end

-- Directional move relative to stored orientation (set by Face Me / Face Away)
-- Forward = away from where player was standing (along orientation)
-- Back = toward where player was standing
-- Left/Right = perpendicular
-- Requires Face Me or Face Away to be used first (sets P.selectedOrientation)
function P.MoveDirection(direction)
    if not P.selectedGuid then
        P.PrintError("No GUID selected.")
        return
    end
    if not P.selectedOrientation then
        P.PrintError("Use Face Me or Face Away first to set direction.")
        return
    end

    -- Find the selected object's current position from nearbyList
    local objX, objY, objZ = nil, nil, nil
    for _, obj in ipairs(P.nearbyList) do
        if obj.guid == P.selectedGuid then
            objX = obj.x
            objY = obj.y
            objZ = obj.z
            break
        end
    end

    if not objX then
        P.Print("Object position unknown. Run |cffffff00Scan|r first.")
        return
    end

    -- Use the orientation captured when Face Me/Away was clicked
    -- This is the PLAYER's facing at that moment
    -- WoW forward vector: dx = -cos(O), dy = -sin(O)
    -- (verified: walking at O~1.88 gave X+6.45, Y-20.17 over ~21yd)
    local O = P.selectedOrientation
    local dist = MOVE_STEP
    local fwdX = -math.cos(O)
    local fwdY = -math.sin(O)

    local dx, dy = 0, 0
    if direction == "forward" then
        dx = fwdX * dist
        dy = fwdY * dist
    elseif direction == "back" then
        dx = -fwdX * dist
        dy = -fwdY * dist
    elseif direction == "left" then
        -- Rotate forward vector 90 degrees left: (-fwdY, fwdX)
        dx = -fwdY * dist
        dy = fwdX * dist
    elseif direction == "right" then
        -- Rotate forward vector 90 degrees right: (fwdY, -fwdX)
        dx = fwdY * dist
        dy = -fwdX * dist
    end

    local newX = objX + dx
    local newY = objY + dy
    local newZ = objZ

    -- Debug: show exactly what we're computing
    P.DebugLog("NUDGE " .. direction .. ": O=" .. string.format("%.4f", O) ..
        " obj=(" .. string.format("%.1f", objX) .. "," .. string.format("%.1f", objY) .. ")" ..
        " dx=" .. string.format("%.2f", dx) .. " dy=" .. string.format("%.2f", dy) ..
        " new=(" .. string.format("%.1f", newX) .. "," .. string.format("%.1f", newY) .. ")")

    P.MoveObjectTo(P.selectedGuid, newX, newY, newZ)
    P.Print("Nudged " .. direction .. " " .. dist .. "yd.")

    -- Update nearbyList so repeated moves chain correctly
    for _, obj in ipairs(P.nearbyList) do
        if obj.guid == P.selectedGuid then
            obj.x = newX
            obj.y = newY
            obj.z = newZ
            break
        end
    end
end

-- Slash command: move selected/specified object to player pos
function P.MoveObject(guid)
    local g = tonumber(guid) or P.selectedGuid
    if not g then
        P.PrintError("No GUID. Use /msui near, click a row, then move.")
        return
    end
    P.selectedGuid = g
    P.MoveToMe()
end

-- ============================================================
-- Other Commands
-- ============================================================

function P.SelectGuid(guid)
    local g = tonumber(guid)
    if not g then
        P.PrintError("Usage: /msui select <guid>")
        return
    end
    P.selectedGuid = g
    P.selectedOrientation = nil
    P.PrintSuccess("Selected GUID: " .. g)
    if P.OnSelectionChanged then P.OnSelectionChanged() end
end

function P.DeleteObject(guid)
    local g = tonumber(guid) or P.selectedGuid
    if not g then
        P.PrintError("No GUID. Use /msui near, click a row, then delete.")
        return
    end
    local gStr = string.format("%d", g)
    P.GM("gobject delete " .. gStr)
    P.PrintSuccess("Deleting GUID #" .. gStr)
    if g == P.selectedGuid then
        P.selectedGuid = nil
        P.selectedOrientation = nil
        if P.OnSelectionChanged then P.OnSelectionChanged() end
    end
end

function P.ScanNearby(dist)
    local d = tonumber(dist) or 30
    P.nearbyList = {}
    P.nearbyCount = 0
    P.debugLog = {}
    P.debugCount = 0
    P.nearbyPending = true
    P.GM("gobject near " .. d)
    P.Print("Scanning...")
    local timer = CreateFrame("Frame")
    timer._elapsed = 0
    timer:SetScript("OnUpdate", function()
        timer._elapsed = timer._elapsed + (arg1 or 0.016)
        if timer._elapsed >= 2 then
            P.nearbyPending = false
            timer:SetScript("OnUpdate", nil)
            if P.OnNearbyScanComplete then P.OnNearbyScanComplete() end
        end
    end)
end

-- ============================================================
-- Slash Command Handler
-- ============================================================

SLASH_MSUI1 = "/msui"
SlashCmdList["MSUI"] = function(msg)
    local _, _, cmd, rest = string.find(msg, "^(%S+)%s*(.*)$")
    if not cmd then cmd = msg; rest = "" end
    cmd = string.lower(cmd or "")

    if cmd == "" or cmd == "show" or cmd == "open" then
        P.ToggleUI()
    elseif cmd == "place" then
        local entry = tonumber(rest)
        if entry then P.PlaceObject(entry)
        else P.PrintError("Usage: /msui place <entry>") end
    elseif cmd == "select" or cmd == "sel" then
        P.SelectGuid(rest)
    elseif cmd == "delete" or cmd == "del" then
        P.DeleteObject(rest)
    elseif cmd == "move" then
        P.MoveObject(rest)
    elseif cmd == "turn" then
        P.TurnObject(rest)
    elseif cmd == "faceme" then
        P.FaceMe()
    elseif cmd == "faceaway" then
        P.FaceAway()
    elseif cmd == "near" then
        P.ScanNearby(rest)
    elseif cmd == "gps" then
        P.CaptureGPS(function()
            if P.gps then
                P.Print(string.format("X: %.2f  Y: %.2f  Z: %.2f  O: %.4f",
                    P.gps.x, P.gps.y, P.gps.z, P.gps.orientation))
            end
        end)
    elseif cmd == "help" then
        P.Print("|cffffffCommands:|r")
        P.Print("  /msui  --  Toggle placer window")
        P.Print("  /msui place <entry>  --  Spawn + auto-detect GUID")
        P.Print("  /msui near [dist]  --  Scan nearby objects")
        P.Print("  /msui select <guid>  --  Set target GUID")
        P.Print("  /msui delete [guid]  --  Remove object")
        P.Print("  /msui move [guid]  --  Move to your position")
        P.Print("  /msui turn [guid] <degrees>  --  Set rotation")
        P.Print("  /msui faceme  --  Object faces you")
        P.Print("  /msui faceaway  --  Object faces away")
        P.Print("  /msui gps  --  Show your coordinates")
    else
        P.PrintError("Unknown command. Type /msui help")
    end
end

-- ============================================================
-- Init
-- ============================================================

local initFrame = CreateFrame("Frame")
initFrame:RegisterEvent("PLAYER_LOGIN")
initFrame:SetScript("OnEvent", function()
    local count = P.GetCatalogCount()
    P.Print("Placer loaded  --  " .. count .. " custom object" .. (count ~= 1 and "s" or "") .. ". Type /msui to open.")
end)
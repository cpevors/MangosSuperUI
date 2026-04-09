-- MangosSuperUI_Placer :: UI.lua
-- Browse frame with catalog/nearby/debug tabs, search, scrolling list,
-- action buttons, rotation controls, directional move controls.

local P = MSUI_Placer

-- Constants
local FRAME_WIDTH = 360
local FRAME_HEIGHT = 560
local ROW_HEIGHT = 36
local VISIBLE_ROWS = 9
local LIST_HEIGHT = ROW_HEIGHT * VISIBLE_ROWS

-- State
local filteredList = {}
local scrollOffset = 0
local selectedIndex = nil
local rows = {}
local currentTab = "catalog"

-- Colors
local COL_BG        = { 0.08, 0.08, 0.12, 0.95 }
local COL_BORDER    = { 0.25, 0.55, 0.85, 0.8 }
local COL_HEADER    = { 0.10, 0.12, 0.18, 1 }
local COL_ROW       = { 0.12, 0.14, 0.20, 1 }
local COL_ROW_ALT   = { 0.10, 0.12, 0.17, 1 }
local COL_ROW_SEL   = { 0.18, 0.30, 0.50, 1 }
local COL_ROW_HOVER = { 0.15, 0.20, 0.32, 1 }
local COL_ACCENT    = { 0.30, 0.65, 1.0 }
local COL_GREEN     = { 0.30, 0.85, 0.40 }
local COL_RED       = { 0.90, 0.30, 0.30 }
local COL_YELLOW    = { 1.0, 0.85, 0.30 }
local COL_ORANGE    = { 1.0, 0.60, 0.20 }
local COL_MUTED     = { 0.55, 0.55, 0.65 }
local COL_WHITE     = { 0.92, 0.92, 0.95 }
local COL_TAB_ON    = { 0.18, 0.28, 0.45, 1 }
local COL_TAB_OFF   = { 0.10, 0.12, 0.17, 1 }
local COL_BTN       = { 0.18, 0.22, 0.32, 1 }
local COL_BTN_HOVER = { 0.25, 0.35, 0.50, 1 }

-- ============================================================
-- Helpers
-- ============================================================

local function SetBG(frame, r, g, b, a)
    if frame._bgTex then
        frame._bgTex:SetTexture(r, g, b, a)
        return frame._bgTex
    end
    local t = frame:CreateTexture(nil, "BACKGROUND")
    t:SetAllPoints()
    t:SetTexture(r, g, b, a)
    frame._bgTex = t
    return t
end

local function MakeButton(parent, text, width, height)
    local btn = CreateFrame("Button", nil, parent)
    btn:SetWidth(width)
    btn:SetHeight(height)
    SetBG(btn, unpack(COL_BTN))
    btn._text = btn:CreateFontString(nil, "OVERLAY", "GameFontNormalSmall")
    btn._text:SetPoint("CENTER", btn, "CENTER", 0, 0)
    btn._text:SetText(text)
    btn._text:SetTextColor(unpack(COL_WHITE))
    btn:SetScript("OnEnter", function() SetBG(btn, unpack(COL_BTN_HOVER)) end)
    btn:SetScript("OnLeave", function() SetBG(btn, unpack(COL_BTN)) end)
    return btn
end

local function MakeSmallButton(parent, text, width)
    return MakeButton(parent, text, width, 22)
end

-- Tooltip helper for buttons
local function AddTooltip(btn, title, body)
    local oldEnter = btn:GetScript("OnEnter")
    btn:SetScript("OnEnter", function()
        if oldEnter then oldEnter() end
        GameTooltip:SetOwner(btn, "ANCHOR_TOP")
        GameTooltip:SetText(title, unpack(COL_ACCENT))
        if body then
            GameTooltip:AddLine(body, 1, 1, 1, true)
        end
        GameTooltip:Show()
    end)
    local oldLeave = btn:GetScript("OnLeave")
    btn:SetScript("OnLeave", function()
        if oldLeave then oldLeave() end
        GameTooltip:Hide()
    end)
end

-- ============================================================
-- Main Frame
-- ============================================================

local f = CreateFrame("Frame", "MSUIPlacerFrame", UIParent)
f:SetWidth(FRAME_WIDTH)
f:SetHeight(FRAME_HEIGHT)
f:SetPoint("CENTER", UIParent, "CENTER", 0, 0)
f:SetMovable(true)
f:EnableMouse(true)
f:SetClampedToScreen(true)
f:SetFrameStrata("HIGH")
f:Hide()

SetBG(f, unpack(COL_BG))
f:SetBackdrop({
    edgeFile = "Interface\\Tooltips\\UI-Tooltip-Border",
    edgeSize = 12,
    insets = { left = 2, right = 2, top = 2, bottom = 2 },
})
f:SetBackdropBorderColor(unpack(COL_BORDER))

-- ============================================================
-- Title Bar
-- ============================================================

local titleBar = CreateFrame("Frame", nil, f)
titleBar:SetHeight(28)
titleBar:SetPoint("TOPLEFT", f, "TOPLEFT", 4, -4)
titleBar:SetPoint("TOPRIGHT", f, "TOPRIGHT", -4, -4)
SetBG(titleBar, unpack(COL_HEADER))
titleBar:EnableMouse(true)
titleBar:RegisterForDrag("LeftButton")
titleBar:SetScript("OnDragStart", function() f:StartMoving() end)
titleBar:SetScript("OnDragStop", function() f:StopMovingOrSizing() end)

local titleText = titleBar:CreateFontString(nil, "OVERLAY", "GameFontNormal")
titleText:SetPoint("LEFT", titleBar, "LEFT", 10, 0)
titleText:SetText("|cff4da6ffMSUI|r Placer")

local countText = titleBar:CreateFontString(nil, "OVERLAY", "GameFontNormalSmall")
countText:SetPoint("RIGHT", titleBar, "RIGHT", -30, 0)
countText:SetTextColor(unpack(COL_MUTED))

local closeBtn = CreateFrame("Button", nil, titleBar, "UIPanelCloseButton")
closeBtn:SetPoint("TOPRIGHT", titleBar, "TOPRIGHT", 6, 6)
closeBtn:SetScript("OnClick", function() f:Hide() end)

-- ============================================================
-- Tabs
-- ============================================================

local tabWidth = math.floor((FRAME_WIDTH - 20) / 3)

local tabCatalog = CreateFrame("Button", nil, f)
tabCatalog:SetHeight(22)
tabCatalog:SetWidth(tabWidth)
tabCatalog:SetPoint("TOPLEFT", titleBar, "BOTTOMLEFT", 0, -4)
SetBG(tabCatalog, unpack(COL_TAB_ON))
tabCatalog._label = tabCatalog:CreateFontString(nil, "OVERLAY", "GameFontNormalSmall")
tabCatalog._label:SetPoint("CENTER", tabCatalog, "CENTER", 0, 0)
tabCatalog._label:SetText("Catalog")
tabCatalog._label:SetTextColor(unpack(COL_WHITE))

local tabNearby = CreateFrame("Button", nil, f)
tabNearby:SetHeight(22)
tabNearby:SetWidth(tabWidth)
tabNearby:SetPoint("LEFT", tabCatalog, "RIGHT", 2, 0)
SetBG(tabNearby, unpack(COL_TAB_OFF))
tabNearby._label = tabNearby:CreateFontString(nil, "OVERLAY", "GameFontNormalSmall")
tabNearby._label:SetPoint("CENTER", tabNearby, "CENTER", 0, 0)
tabNearby._label:SetText("Nearby (0)")
tabNearby._label:SetTextColor(unpack(COL_MUTED))

local tabDebug = CreateFrame("Button", nil, f)
tabDebug:SetHeight(22)
tabDebug:SetWidth(tabWidth)
tabDebug:SetPoint("LEFT", tabNearby, "RIGHT", 2, 0)
SetBG(tabDebug, unpack(COL_TAB_OFF))
tabDebug._label = tabDebug:CreateFontString(nil, "OVERLAY", "GameFontNormalSmall")
tabDebug._label:SetPoint("CENTER", tabDebug, "CENTER", 0, 0)
tabDebug._label:SetText("Debug")
tabDebug._label:SetTextColor(unpack(COL_MUTED))

local function SetTab(tab)
    currentTab = tab
    selectedIndex = nil
    scrollOffset = 0

    SetBG(tabCatalog, unpack(COL_TAB_OFF))
    tabCatalog._label:SetTextColor(unpack(COL_MUTED))
    SetBG(tabNearby, unpack(COL_TAB_OFF))
    tabNearby._label:SetTextColor(unpack(COL_MUTED))
    SetBG(tabDebug, unpack(COL_TAB_OFF))
    tabDebug._label:SetTextColor(unpack(COL_MUTED))

    if tab == "catalog" then
        SetBG(tabCatalog, unpack(COL_TAB_ON))
        tabCatalog._label:SetTextColor(unpack(COL_WHITE))
    elseif tab == "nearby" then
        SetBG(tabNearby, unpack(COL_TAB_ON))
        tabNearby._label:SetTextColor(unpack(COL_WHITE))
    elseif tab == "debug" then
        SetBG(tabDebug, unpack(COL_TAB_ON))
        tabDebug._label:SetTextColor(unpack(COL_WHITE))
    end
    P.DoFilter()
end

tabCatalog:SetScript("OnClick", function() SetTab("catalog") end)
tabNearby:SetScript("OnClick", function() SetTab("nearby") end)
tabDebug:SetScript("OnClick", function() SetTab("debug") end)

-- ============================================================
-- Search Box
-- ============================================================

local searchBox = CreateFrame("EditBox", "MSUIPlacerSearch", f, "InputBoxTemplate")
searchBox:SetHeight(22)
searchBox:SetPoint("TOPLEFT", tabCatalog, "BOTTOMLEFT", 6, -6)
searchBox:SetPoint("TOPRIGHT", tabDebug, "BOTTOMRIGHT", -6, -6)
searchBox:SetAutoFocus(false)
searchBox:SetFontObject("GameFontHighlightSmall")

local searchLabel = searchBox:CreateFontString(nil, "OVERLAY", "GameFontNormalSmall")
searchLabel:SetPoint("LEFT", searchBox, "LEFT", 4, 0)
searchLabel:SetText("Search...")
searchLabel:SetTextColor(0.45, 0.45, 0.55, 0.7)
searchBox:SetScript("OnEditFocusGained", function() searchLabel:Hide() end)
searchBox:SetScript("OnEditFocusLost", function()
    if searchBox:GetText() == "" then searchLabel:Show() end
end)

-- ============================================================
-- Scroll List
-- ============================================================

local listFrame = CreateFrame("Frame", nil, f)
listFrame:SetPoint("TOPLEFT", searchBox, "BOTTOMLEFT", -4, -6)
listFrame:SetPoint("TOPRIGHT", searchBox, "BOTTOMRIGHT", 4, -6)
listFrame:SetHeight(LIST_HEIGHT)

for i = 1, VISIBLE_ROWS do
    local row = CreateFrame("Button", nil, listFrame)
    row:SetHeight(ROW_HEIGHT)
    row:SetPoint("TOPLEFT", listFrame, "TOPLEFT", 0, -((i - 1) * ROW_HEIGHT))
    row:SetPoint("TOPRIGHT", listFrame, "TOPRIGHT", 0, -((i - 1) * ROW_HEIGHT))

    row._bg = row:CreateTexture(nil, "BACKGROUND")
    row._bg:SetAllPoints()

    row._name = row:CreateFontString(nil, "OVERLAY", "GameFontNormalSmall")
    row._name:SetPoint("TOPLEFT", row, "TOPLEFT", 8, -4)
    row._name:SetPoint("TOPRIGHT", row, "TOPRIGHT", -60, -4)
    row._name:SetJustifyH("LEFT")
    row._name:SetTextColor(unpack(COL_WHITE))

    row._meta = row:CreateFontString(nil, "OVERLAY", "GameFontNormalSmall")
    row._meta:SetPoint("BOTTOMLEFT", row, "BOTTOMLEFT", 8, 4)
    row._meta:SetPoint("BOTTOMRIGHT", row, "BOTTOMRIGHT", -60, 4)
    row._meta:SetJustifyH("LEFT")
    row._meta:SetTextColor(unpack(COL_MUTED))

    row._right = row:CreateFontString(nil, "OVERLAY", "GameFontNormalSmall")
    row._right:SetPoint("RIGHT", row, "RIGHT", -8, 0)
    row._right:SetJustifyH("RIGHT")
    row._right:SetTextColor(unpack(COL_MUTED))

    row._index = i
    row._data = nil
    row._dataIndex = nil

    rows[i] = row
end

-- Row click and double-click
for i = 1, VISIBLE_ROWS do
    local row = rows[i]
    local lastClick = 0
    row:SetScript("OnClick", function()
        if not row._data then return end
        local now = GetTime()
        if row._dataIndex == selectedIndex and (now - lastClick) < 0.4 then
            if currentTab == "catalog" then
                P.PlaceObject(row._data.entry)
            end
        else
            selectedIndex = row._dataIndex
            if currentTab == "nearby" and row._data.guid then
                P.selectedGuid = row._data.guid
                P.selectedOrientation = nil  -- unknown for manually selected objects
                if P.OnSelectionChanged then P.OnSelectionChanged() end
            end
            P.RefreshList()
        end
        lastClick = now
    end)

    row:SetScript("OnEnter", function()
        if row._data and row._dataIndex ~= selectedIndex then
            row._bg:SetTexture(unpack(COL_ROW_HOVER))
        end
        if row._data then
            GameTooltip:SetOwner(row, "ANCHOR_RIGHT")
            if currentTab == "nearby" then
                GameTooltip:SetText(row._data.name, unpack(COL_ACCENT))
                GameTooltip:AddDoubleLine("GUID", "#" .. (row._data.guid or "?"), unpack(COL_MUTED), 1, 1, 0.3)
                GameTooltip:AddDoubleLine("Entry", "#" .. (row._data.entry or "?"), unpack(COL_MUTED), unpack(COL_MUTED))
                local coords = string.format("%.1f, %.1f, %.1f", row._data.x or 0, row._data.y or 0, row._data.z or 0)
                GameTooltip:AddDoubleLine("Position", coords, unpack(COL_MUTED), unpack(COL_MUTED))
            elseif currentTab == "catalog" then
                GameTooltip:SetText(row._data.name, unpack(COL_ACCENT))
                if row._data.desc and row._data.desc ~= "" then
                    GameTooltip:AddLine(row._data.desc, 1, 1, 1, true)
                end
                GameTooltip:AddLine(" ")
                GameTooltip:AddDoubleLine("Entry", "#" .. row._data.entry, unpack(COL_MUTED), unpack(COL_MUTED))
                GameTooltip:AddDoubleLine("Type", P.GetTypeName(row._data.type), unpack(COL_MUTED), unpack(COL_MUTED))
            end
            GameTooltip:Show()
        end
    end)

    row:SetScript("OnLeave", function()
        if row._data and row._dataIndex ~= selectedIndex then
            local col = (math.mod(row._index, 2) == 0) and COL_ROW_ALT or COL_ROW
            row._bg:SetTexture(unpack(col))
        end
        GameTooltip:Hide()
    end)
end

-- Mousewheel scroll
listFrame:EnableMouseWheel(true)
listFrame:SetScript("OnMouseWheel", function()
    local delta = arg1 or 0
    scrollOffset = scrollOffset - delta
    local maxScroll = math.max(0, P.tlen(filteredList) - VISIBLE_ROWS)
    if scrollOffset < 0 then scrollOffset = 0 end
    if scrollOffset > maxScroll then scrollOffset = maxScroll end
    P.RefreshList()
end)

-- ============================================================
-- Button Bar — Row 1: Actions
-- ============================================================

local btnRow1 = CreateFrame("Frame", nil, f)
btnRow1:SetHeight(26)
btnRow1:SetPoint("TOPLEFT", listFrame, "BOTTOMLEFT", 0, -6)
btnRow1:SetPoint("TOPRIGHT", listFrame, "BOTTOMRIGHT", 0, -6)

local btnPlace = MakeButton(btnRow1, "Place", 62, 26)
btnPlace:SetPoint("LEFT", btnRow1, "LEFT", 0, 0)
btnPlace._text:SetTextColor(unpack(COL_GREEN))
btnPlace:SetScript("OnClick", function()
    if currentTab == "catalog" and selectedIndex then
        local data = filteredList[selectedIndex]
        if data then P.PlaceObject(data.entry) end
    else
        P.PrintError("Select an object from the Catalog tab.")
    end
end)
AddTooltip(btnPlace, "Place Object", "Spawn at your feet, auto-detect GUID. Face the direction you want the object to face.")

local btnScan = MakeButton(btnRow1, "Scan", 50, 26)
btnScan:SetPoint("LEFT", btnPlace, "RIGHT", 4, 0)
btnScan._text:SetTextColor(unpack(COL_ACCENT))
btnScan:SetScript("OnClick", function()
    P.ScanNearby(30)
    SetTab("nearby")
end)
AddTooltip(btnScan, "Scan Nearby", "Find all game objects within 30 yards.")

local btnDelete = MakeButton(btnRow1, "Delete", 58, 26)
btnDelete:SetPoint("LEFT", btnScan, "RIGHT", 4, 0)
btnDelete._text:SetTextColor(unpack(COL_RED))
btnDelete:SetScript("OnClick", function() P.DeleteObject(nil) end)
AddTooltip(btnDelete, "Delete", "Permanently remove the selected object.")

local btnMoveTo = MakeButton(btnRow1, "Move To Me", 84, 26)
btnMoveTo:SetPoint("LEFT", btnDelete, "RIGHT", 4, 0)
btnMoveTo._text:SetTextColor(unpack(COL_YELLOW))
btnMoveTo:SetScript("OnClick", function() P.MoveToMe() end)
AddTooltip(btnMoveTo, "Move To Me", "Teleport the selected object to your current position.")

-- ============================================================
-- Button Bar — Row 2: Rotation
-- ============================================================

local btnRow2 = CreateFrame("Frame", nil, f)
btnRow2:SetHeight(22)
btnRow2:SetPoint("TOPLEFT", btnRow1, "BOTTOMLEFT", 0, -4)
btnRow2:SetPoint("TOPRIGHT", btnRow1, "BOTTOMRIGHT", 0, -4)

-- Section label
local turnLabel = btnRow2:CreateFontString(nil, "OVERLAY", "GameFontNormalSmall")
turnLabel:SetPoint("LEFT", btnRow2, "LEFT", 0, 0)
turnLabel:SetText("Rotate:")
turnLabel:SetTextColor(unpack(COL_MUTED))

local btnFaceMe = MakeSmallButton(btnRow2, "Face Me", 62)
btnFaceMe:SetPoint("LEFT", turnLabel, "RIGHT", 6, 0)
btnFaceMe._text:SetTextColor(unpack(COL_ORANGE))
btnFaceMe:SetScript("OnClick", function() P.FaceMe() end)
AddTooltip(btnFaceMe, "Face Me", "Turn object to face toward you. Stand where you want the object to look.")

local btnFaceAway = MakeSmallButton(btnRow2, "Face Away", 72)
btnFaceAway:SetPoint("LEFT", btnFaceMe, "RIGHT", 4, 0)
btnFaceAway._text:SetTextColor(unpack(COL_ORANGE))
btnFaceAway:SetScript("OnClick", function() P.FaceAway() end)
AddTooltip(btnFaceAway, "Face Away", "Turn object to face the same direction you're looking.")

local btnRotL = MakeSmallButton(btnRow2, "<  15", 46)
btnRotL:SetPoint("LEFT", btnFaceAway, "RIGHT", 8, 0)
btnRotL._text:SetTextColor(unpack(COL_WHITE))
btnRotL:SetScript("OnClick", function() P.RotateLeft() end)
AddTooltip(btnRotL, "Rotate Left", "Nudge orientation left by 15 degrees.")

local btnRotR = MakeSmallButton(btnRow2, "15  >", 46)
btnRotR:SetPoint("LEFT", btnRotL, "RIGHT", 4, 0)
btnRotR._text:SetTextColor(unpack(COL_WHITE))
btnRotR:SetScript("OnClick", function() P.RotateRight() end)
AddTooltip(btnRotR, "Rotate Right", "Nudge orientation right by 15 degrees.")

-- ============================================================
-- Button Bar — Row 3: Directional Move
-- ============================================================

local btnRow3 = CreateFrame("Frame", nil, f)
btnRow3:SetHeight(22)
btnRow3:SetPoint("TOPLEFT", btnRow2, "BOTTOMLEFT", 0, -4)
btnRow3:SetPoint("TOPRIGHT", btnRow2, "BOTTOMRIGHT", 0, -4)

local moveLabel = btnRow3:CreateFontString(nil, "OVERLAY", "GameFontNormalSmall")
moveLabel:SetPoint("LEFT", btnRow3, "LEFT", 0, 0)
moveLabel:SetText("Nudge:")
moveLabel:SetTextColor(unpack(COL_MUTED))

local btnFwd = MakeSmallButton(btnRow3, "Fwd", 42)
btnFwd:SetPoint("LEFT", moveLabel, "RIGHT", 6, 0)
btnFwd._text:SetTextColor(unpack(COL_YELLOW))
btnFwd:SetScript("OnClick", function() P.MoveDirection("forward") end)
AddTooltip(btnFwd, "Nudge Forward", "Move object 1yd in the direction you're facing.")

local btnBack = MakeSmallButton(btnRow3, "Back", 42)
btnBack:SetPoint("LEFT", btnFwd, "RIGHT", 4, 0)
btnBack._text:SetTextColor(unpack(COL_YELLOW))
btnBack:SetScript("OnClick", function() P.MoveDirection("back") end)
AddTooltip(btnBack, "Nudge Back", "Move object 1yd opposite your facing.")

local btnLeft = MakeSmallButton(btnRow3, "Left", 42)
btnLeft:SetPoint("LEFT", btnBack, "RIGHT", 4, 0)
btnLeft._text:SetTextColor(unpack(COL_YELLOW))
btnLeft:SetScript("OnClick", function() P.MoveDirection("left") end)
AddTooltip(btnLeft, "Nudge Left", "Move object 1yd to your left.")

local btnRight = MakeSmallButton(btnRow3, "Right", 46)
btnRight:SetPoint("LEFT", btnLeft, "RIGHT", 4, 0)
btnRight._text:SetTextColor(unpack(COL_YELLOW))
btnRight:SetScript("OnClick", function() P.MoveDirection("right") end)
AddTooltip(btnRight, "Nudge Right", "Move object 1yd to your right.")

-- ============================================================
-- Status Bar
-- ============================================================

local statusBar = CreateFrame("Frame", nil, f)
statusBar:SetHeight(22)
statusBar:SetPoint("BOTTOMLEFT", f, "BOTTOMLEFT", 4, 4)
statusBar:SetPoint("BOTTOMRIGHT", f, "BOTTOMRIGHT", -4, 4)

local statusText = statusBar:CreateFontString(nil, "OVERLAY", "GameFontNormalSmall")
statusText:SetPoint("LEFT", statusBar, "LEFT", 6, 0)
statusText:SetTextColor(unpack(COL_MUTED))

local guidText = statusBar:CreateFontString(nil, "OVERLAY", "GameFontNormalSmall")
guidText:SetPoint("RIGHT", statusBar, "RIGHT", -6, 0)

-- ============================================================
-- Refresh Logic
-- ============================================================

function P.RefreshList()
    for i = 1, VISIBLE_ROWS do
        local row = rows[i]
        local dataIdx = scrollOffset + i
        local data = filteredList[dataIdx]

        if data then
            row._data = data
            row._dataIndex = dataIdx

            if currentTab == "debug" then
                row._name:SetText(data.name or "")
                row._meta:SetText("")
                row._right:SetText("")
            elseif currentTab == "nearby" then
                row._name:SetText(data.name)
                row._meta:SetText("GUID: " .. (data.guid or "?"))
                row._right:SetText("#" .. data.entry)
            else
                row._name:SetText(data.name)
                row._meta:SetText(P.GetTypeName(data.type or 0))
                row._right:SetText("#" .. data.entry)
            end

            if dataIdx == selectedIndex then
                row._bg:SetTexture(unpack(COL_ROW_SEL))
                row._name:SetTextColor(1, 1, 1)
                if currentTab == "nearby" then
                    row._meta:SetTextColor(unpack(COL_YELLOW))
                end
            else
                local col = (math.mod(i, 2) == 0) and COL_ROW_ALT or COL_ROW
                row._bg:SetTexture(unpack(col))
                row._name:SetTextColor(unpack(COL_WHITE))
                row._meta:SetTextColor(unpack(COL_MUTED))
            end
            row:Show()
        else
            row._data = nil
            row._dataIndex = nil
            row:Hide()
        end
    end

    local total = P.tlen(filteredList)
    countText:SetText(total .. (currentTab == "nearby" and " spawn" or " object") .. (total ~= 1 and "s" or ""))

    if total > 0 then
        statusText:SetText((scrollOffset + 1) .. "-" .. math.min(scrollOffset + VISIBLE_ROWS, total) .. " of " .. total)
    else
        statusText:SetText(currentTab == "nearby" and "Click Scan to find nearby objects" or "No matches")
    end

    if P.selectedGuid then
        local oStr = ""
        if P.selectedOrientation then
            oStr = "  |cffccccccO:" .. string.format("%.1f", P.selectedOrientation) .. "|r"
        end
        guidText:SetText("|cffffffGUID:|r |cffffff00" .. P.selectedGuid .. "|r" .. oStr)
    elseif P.lastPlacedName then
        guidText:SetText("|cff44ff44Last: " .. P.lastPlacedName .. "|r")
    else
        guidText:SetText("")
    end

    tabNearby._label:SetText("Nearby (" .. P.nearbyCount .. ")")
end

function P.DoFilter()
    if currentTab == "debug" then
        filteredList = {}
        for _, msg in ipairs(P.debugLog) do
            table.insert(filteredList, { name = msg, entry = 0, type = 0 })
        end
    elseif currentTab == "nearby" then
        local searchText = string.lower(searchBox:GetText() or "")
        if searchText == "" then
            filteredList = P.nearbyList
        else
            filteredList = {}
            for _, item in ipairs(P.nearbyList) do
                local nameLower = string.lower(item.name or "")
                local guidStr = tostring(item.guid or "")
                local entryStr = tostring(item.entry or "")
                if string.find(nameLower, searchText, 1, true) or string.find(guidStr, searchText, 1, true) or string.find(entryStr, searchText, 1, true) then
                    table.insert(filteredList, item)
                end
            end
        end
    else
        local searchText = searchBox:GetText() or ""
        filteredList = P.FilterCatalog(searchText, nil)
    end
    scrollOffset = 0
    P.RefreshList()
end

-- ============================================================
-- Callbacks from Core
-- ============================================================

P.OnNearbyUpdated = function()
    if currentTab == "nearby" then P.DoFilter() end
    tabNearby._label:SetText("Nearby (" .. P.nearbyCount .. ")")
end

P.OnNearbyScanComplete = function()
    if P.nearbyCount > 0 then
        SetTab("nearby")
    end
end

P.OnSelectionChanged = function()
    P.RefreshList()
end

P.OnDebugUpdated = function()
    tabDebug._label:SetText("Debug (" .. P.debugCount .. ")")
    if currentTab == "debug" then P.DoFilter() end
end

-- ============================================================
-- Toggle
-- ============================================================

function P.ToggleUI()
    if f:IsShown() then
        f:Hide()
    else
        P.DoFilter()
        f:Show()
    end
end

-- ============================================================
-- Live Search
-- ============================================================

local searchTimer = 0
local searchDirty = false
searchBox:SetScript("OnTextChanged", function()
    searchDirty = true
    searchTimer = 0
end)
searchBox:SetScript("OnEnterPressed", function()
    P.DoFilter()
    searchBox:ClearFocus()
end)
searchBox:SetScript("OnEscapePressed", function()
    searchBox:SetText("")
    P.DoFilter()
    searchBox:ClearFocus()
end)

local updateFrame = CreateFrame("Frame")
updateFrame:SetScript("OnUpdate", function()
    if not searchDirty then return end
    searchTimer = searchTimer + (arg1 or 0.016)
    if searchTimer >= 0.25 then
        searchDirty = false
        P.DoFilter()
    end
end)

-- ============================================================
-- Escape to close
-- ============================================================

table.insert(UISpecialFrames, "MSUIPlacerFrame")

P.Print("UI module loaded.")
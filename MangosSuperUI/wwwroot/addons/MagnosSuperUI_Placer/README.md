## What is this?

MangosSuperUI Placer lets you **browse and place custom game objects** directly in the WoW game world. It connects to your MangosSuperUI catalog — any object you create or clone on the web admin shows up here, ready to spawn.

## How it works

- Open the placer window with `/msui`
- The **Catalog** tab shows all your custom objects from MangosSuperUI
- **Double-click** any object to spawn it at your character's feet
- Click **Scan** to find nearby placed objects and their GUIDs
- The **Nearby** tab lists everything around you — click a row to select its GUID
- Use **Delete**, **Move**, and **Turn** to manipulate selected objects

## Requirements

- A GM-level account on the server (the addon uses `.gobject` commands)
- Custom objects created in MangosSuperUI (entry 900000+)

## Updating the catalog

When you create new objects in MangosSuperUI, just visit the Downloads page and click **Download** again. The catalog is regenerated from the database every time the page loads, so the ZIP always contains the latest objects.

## Slash commands

- `/msui` — Toggle the placer window
- `/msui place <entry>` — Spawn an object at your feet
- `/msui near [distance]` — Scan for nearby objects (default 30yd)
- `/msui select <guid>` — Target a specific object by GUID
- `/msui delete [guid]` — Remove an object
- `/msui move [guid]` — Move an object to your position
- `/msui turn [guid] <degrees>` — Rotate an object
- `/msui help` — Show all commands
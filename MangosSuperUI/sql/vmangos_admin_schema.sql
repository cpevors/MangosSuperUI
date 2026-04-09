-- ============================================================================
-- MangosSuperUI — vmangos_admin schema
-- ============================================================================
-- These tables are created automatically by DbInitializationService on first
-- boot. This file is provided for reference and manual setup only.
--
-- Tables prefixed og_baseline_* are NOT included here — they are 1:1 structural
-- copies of VMaNGOS tables (items, creatures, game objects, loot, etc.) and are
-- auto-created at runtime when baseline snapshots are taken.
--
-- Usage (if creating manually):
--   mysql -u mangos -pmangos vmangos_admin < vmangos_admin_schema.sql
-- ============================================================================

CREATE DATABASE IF NOT EXISTS `vmangos_admin`
  DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;

USE `vmangos_admin`;

-- --------------------------------------------------------------------------
-- audit_log
-- Tracks every write operation performed through MangosSuperUI: RA commands,
-- item/creature edits, loot changes, config saves, etc.
-- --------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS `audit_log` (
  `id` bigint(20) unsigned NOT NULL AUTO_INCREMENT,
  `timestamp` datetime(3) NOT NULL DEFAULT current_timestamp(3),
  `operator` varchar(64) NOT NULL DEFAULT 'system',
  `operator_ip` varchar(45) DEFAULT NULL,
  `category` varchar(32) NOT NULL,
  `action` varchar(64) NOT NULL,
  `target_type` varchar(32) DEFAULT NULL,
  `target_name` varchar(128) DEFAULT NULL,
  `target_id` int(10) unsigned DEFAULT NULL,
  `ra_command` text DEFAULT NULL,
  `ra_response` text DEFAULT NULL,
  `state_before` longtext CHARACTER SET utf8mb4 COLLATE utf8mb4_bin DEFAULT NULL CHECK (json_valid(`state_before`)),
  `state_after` longtext CHARACTER SET utf8mb4 COLLATE utf8mb4_bin DEFAULT NULL CHECK (json_valid(`state_after`)),
  `is_reversible` tinyint(1) NOT NULL DEFAULT 0,
  `reverses_id` bigint(20) unsigned DEFAULT NULL,
  `success` tinyint(1) NOT NULL DEFAULT 1,
  `notes` text DEFAULT NULL,
  PRIMARY KEY (`id`),
  KEY `idx_timestamp` (`timestamp`),
  KEY `idx_category` (`category`),
  KEY `idx_action` (`action`),
  KEY `idx_target` (`target_type`,`target_name`),
  KEY `idx_operator` (`operator`),
  KEY `idx_reversible` (`is_reversible`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- --------------------------------------------------------------------------
-- config_history
-- Snapshot of server-config.json after every save from the Settings page.
-- --------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS `config_history` (
  `id` bigint(20) unsigned NOT NULL AUTO_INCREMENT,
  `timestamp` datetime(3) NOT NULL DEFAULT current_timestamp(3),
  `operator` varchar(64) NOT NULL DEFAULT 'system',
  `config_json` mediumtext NOT NULL,
  `changes` longtext CHARACTER SET utf8mb4 COLLATE utf8mb4_bin DEFAULT NULL CHECK (json_valid(`changes`)),
  `notes` text DEFAULT NULL,
  PRIMARY KEY (`id`),
  KEY `idx_timestamp` (`timestamp`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- --------------------------------------------------------------------------
-- scheduled_actions
-- Deferred operations (e.g. scheduled restarts, timed RA commands).
-- --------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS `scheduled_actions` (
  `id` bigint(20) unsigned NOT NULL AUTO_INCREMENT,
  `created_at` datetime(3) NOT NULL DEFAULT current_timestamp(3),
  `execute_at` datetime(3) NOT NULL,
  `executed_at` datetime(3) DEFAULT NULL,
  `operator` varchar(64) NOT NULL DEFAULT 'system',
  `action_type` varchar(64) NOT NULL,
  `action_data` longtext CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL CHECK (json_valid(`action_data`)),
  `status` varchar(16) NOT NULL DEFAULT 'pending',
  `result` text DEFAULT NULL,
  `audit_log_id` bigint(20) unsigned DEFAULT NULL,
  PRIMARY KEY (`id`),
  KEY `idx_execute_at` (`execute_at`),
  KEY `idx_status` (`status`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- --------------------------------------------------------------------------
-- lootifier_generated_items
-- Registry of custom items created by the Lootifier. Maps each generated
-- item_template entry back to its base item and the creature it was made for.
-- --------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS `lootifier_generated_items` (
  `id` int(11) NOT NULL AUTO_INCREMENT,
  `generated_entry` int(11) NOT NULL,
  `base_entry` int(11) NOT NULL,
  `creature_entry` int(11) NOT NULL,
  `budget_pct` float DEFAULT 0,
  `tier_name` varchar(64) DEFAULT '',
  `created_at` datetime NOT NULL,
  PRIMARY KEY (`id`),
  KEY `idx_creature` (`creature_entry`),
  KEY `idx_generated` (`generated_entry`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_general_ci;

-- --------------------------------------------------------------------------
-- lootifier_loot_entries
-- Registry of loot table rows created/modified by the Lootifier. Tracks the
-- original vs new drop chance and which action was taken (add/replace/remove).
-- --------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS `lootifier_loot_entries` (
  `id` int(11) NOT NULL AUTO_INCREMENT,
  `creature_entry` int(11) NOT NULL,
  `loot_table` varchar(64) NOT NULL,
  `loot_entry` int(11) NOT NULL,
  `item_entry` int(11) NOT NULL,
  `action_type` varchar(16) NOT NULL,
  `original_chance` float DEFAULT 0,
  `new_chance` float DEFAULT 0,
  `created_at` datetime NOT NULL,
  PRIMARY KEY (`id`),
  KEY `idx_creature` (`creature_entry`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_general_ci;

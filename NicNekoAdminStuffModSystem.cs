// FILE 1: FlightToggleMod.cs (MODIFIED - with logging system)
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ProtoBuf;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace NicNekoAdminStuff
{
    // Packet classes for network communication
    [ProtoContract]
    public class FlightPermissionRequest
    {
        [ProtoMember(1)]
        public string PlayerUid { get; set; } = null!;

        [ProtoMember(2)]
        public string PlayerName { get; set; } = null!;

        [ProtoMember(3)]
        public string Action { get; set; } = null!; // "flight" or "flightnoclip"

        [ProtoMember(4)]
        public bool RequestingEnable { get; set; } // true = enabling, false = disabling
    }

    [ProtoContract]
    public class FlightPermissionResponse
    {
        [ProtoMember(1)]
        public string PlayerUid { get; set; } = null!;

        [ProtoMember(2)]
        public bool HasPermission { get; set; }

        [ProtoMember(3)]
        public string Message { get; set; } = null!;

        [ProtoMember(4)]
        public string Action { get; set; } = null!; // "flight" or "flightnoclip"

        [ProtoMember(5)]
        public bool RequestingEnable { get; set; }
    }

    // Config class for flight permissions
    public class FlightConfigData
    {
        public bool AllowAllPlayers { get; set; } = false;
        public List<string> AllowedPlayers { get; set; } = new List<string>();
        public Dictionary<string, string> AllowedPlayerUIDs { get; set; } = new Dictionary<string, string>();
        public bool ShowNotifications { get; set; } = true;
        public string FlightDeniedMessage { get; set; } = "You don't have permission to use flight mode!";
        public string FlightNoclipDeniedMessage { get; set; } = "You don't have permission to use flight+noclip mode!";
        public string FlightKey { get; set; } = "R";
        public string FlightNoclipKey { get; set; } = "Ctrl+R";

        public FlightConfigData()
        {
            AllowedPlayers = new List<string>();
            AllowedPlayerUIDs = new Dictionary<string, string>();
        }
    }

    // Logger class for file logging
    public class ModLogger
    {
        private string logDirectory;
        private string generalLogPath;
        private string securityLogPath;
        private object logLock = new object();

        public ModLogger(ICoreAPI api)
        {
            // Create log directory in ModData
            logDirectory = api.GetOrCreateDataPath(Path.Combine("ModData", "NicNekoAdminStuff"));

            // Set up log file paths with date
            string dateStamp = DateTime.UtcNow.ToString("yyyy-MM-dd");
            generalLogPath = Path.Combine(logDirectory, $"general_{dateStamp}.log");
            securityLogPath = Path.Combine(logDirectory, $"security_{dateStamp}.log");

            // Write initial log entry
            LogGeneral("=== NicNekoAdminStuff Mod Initialized ===");
            LogGeneral($"Mod loaded at {GetTimestamp()}");
        }

        private string GetTimestamp()
        {
            return DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss") + " UTC";
        }

        public void LogGeneral(string message)
        {
            lock (logLock)
            {
                try
                {
                    string logEntry = $"[{GetTimestamp()}] {message}";
                    File.AppendAllText(generalLogPath, logEntry + Environment.NewLine);
                }
                catch (Exception ex)
                {
                    // Fallback to console if file logging fails
                    Console.WriteLine($"[NicNekoAdminStuff] Failed to write to log: {ex.Message}");
                }
            }
        }

        public void LogSecurity(string message)
        {
            lock (logLock)
            {
                try
                {
                    string logEntry = $"[{GetTimestamp()}] [SECURITY] {message}";
                    File.AppendAllText(securityLogPath, logEntry + Environment.NewLine);

                    // Also log to general log for visibility
                    File.AppendAllText(generalLogPath, logEntry + Environment.NewLine);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[NicNekoAdminStuff] Failed to write to security log: {ex.Message}");
                }
            }
        }

        public void LogPermissionCheck(string playerName, string playerUid, string action, bool granted)
        {
            string status = granted ? "GRANTED" : "DENIED";
            LogGeneral($"Permission {status} - Player: {playerName} (UID: {playerUid}) - Action: {action}");
        }

        public void LogConfigAction(string action)
        {
            LogGeneral($"Config Action: {action}");
        }

        public void LogPlayerAction(string playerName, string playerUid, string action)
        {
            LogGeneral($"Player Action - {playerName} (UID: {playerUid}): {action}");
        }
    }

    public class FlightToggleMod : ModSystem
    {
        // Shared properties
        private static FlightConfigData? config;
        private static ModLogger? modLogger;

        // Client-side properties
        private ICoreClientAPI clientApi = null!;
        private IClientNetworkChannel clientChannel = null!;
        private bool isFlying = false;
        private bool isFlightNoclip = false;
        private IClientPlayer? player;
        private float originalFallDamageMultiplier;
        private Dictionary<string, TaskCompletionSource<FlightPermissionResponse>> pendingRequests = new();
        private bool waitingForPermissionResponse = false;

        // Server-side properties
        private ICoreServerAPI serverApi = null!;
        private IServerNetworkChannel serverChannel = null!;

        public override void Start(ICoreAPI api)
        {
            base.Start(api);
        }

        public override void StartClientSide(ICoreClientAPI api)
        {
            clientApi = api;
            pendingRequests = new Dictionary<string, TaskCompletionSource<FlightPermissionResponse>>();

            // Load config to get keybinds (client will load it read-only for keybind info)
            LoadClientConfig();

            // Register network channel for client
            clientChannel = api.Network.RegisterChannel("flightpermissions")
                .RegisterMessageType<FlightPermissionRequest>()
                .RegisterMessageType<FlightPermissionResponse>()
                .SetMessageHandler<FlightPermissionResponse>(OnFlightPermissionResponse);

            // Register keybinds based on config
            RegisterKeybinds();

            // Get player reference when player joins
            clientApi.Event.PlayerJoin += OnPlayerJoin;

            clientApi.Logger.Notification("Flight Toggle Client loaded!");
        }

        public override void StartServerSide(ICoreServerAPI api)
        {
            serverApi = api;

            // Initialize logger
            modLogger = new ModLogger(api);
            modLogger.LogGeneral("Server-side initialization started");

            // Load server config
            LoadConfig();

            // Resolve UIDs for players
            ResolvePlayerUIDs();

            // Register network channel for server
            serverChannel = api.Network.RegisterChannel("flightpermissions")
                .RegisterMessageType<FlightPermissionRequest>()
                .RegisterMessageType<FlightPermissionResponse>()
                .SetMessageHandler<FlightPermissionRequest>(OnFlightPermissionRequest);

            serverApi.Logger.Notification("Flight Toggle Server loaded!");
            modLogger.LogGeneral("Server-side initialization completed successfully");
        }

        #region Server-Side Methods

        private void LoadConfig()
        {
            try
            {
                config = serverApi.LoadModConfig<FlightConfigData>("NicNekoAdminStuff.json");
                if (config == null)
                {
                    config = new FlightConfigData();
                    config.AllowedPlayers.Add("PlayerName1");
                    config.AllowedPlayers.Add("PlayerName2");
                    config.AllowedPlayerUIDs = new Dictionary<string, string>();
                    config.ShowNotifications = true;
                    serverApi.Logger.Notification("Creating new NicNekoAdminStuff config file with default settings.");
                    serverApi.StoreModConfig<FlightConfigData>(config, "NicNekoAdminStuff.json");
                    modLogger?.LogConfigAction("Created new config file with default settings");
                }
                else
                {
                    // Ensure AllowedPlayerUIDs dictionary exists
                    if (config.AllowedPlayerUIDs == null)
                    {
                        config.AllowedPlayerUIDs = new Dictionary<string, string>();
                    }
                    serverApi.StoreModConfig<FlightConfigData>(config, "NicNekoAdminStuff.json");
                    modLogger?.LogConfigAction($"Config loaded successfully - AllowAllPlayers: {config.AllowAllPlayers}, Allowed players count: {config.AllowedPlayers.Count}");
                }
                serverApi.Logger.Notification("NicNekoAdminStuff server config loaded successfully.");
            }
            catch (Exception e)
            {
                serverApi.Logger.Error("Could not load NicNekoAdminStuff server config! Loading default settings instead.");
                serverApi.Logger.Error("Error details: " + e.Message);
                modLogger?.LogGeneral($"ERROR loading config: {e.Message}");
                config = new FlightConfigData();

                try
                {
                    config.AllowedPlayers.Add("PlayerName1");
                    config.AllowedPlayers.Add("PlayerName2");
                    config.AllowedPlayerUIDs = new Dictionary<string, string>();
                    config.ShowNotifications = true;
                    serverApi.StoreModConfig<FlightConfigData>(config, "NicNekoAdminStuff_backup.json");
                    serverApi.Logger.Notification("Created NicNekoAdminStuff_backup.json with correct formatting as reference.");
                    modLogger?.LogConfigAction("Created backup config file due to load error");
                }
                catch (Exception backupEx)
                {
                    serverApi.Logger.Error("Could not create backup config: " + backupEx.Message);
                    modLogger?.LogGeneral($"ERROR creating backup config: {backupEx.Message}");
                }
            }
        }

        private void ResolvePlayerUIDs()
        {
            if (config == null || config.AllowedPlayers == null || config.AllowedPlayers.Count == 0)
            {
                serverApi.Logger.Notification("No players to resolve UIDs for.");
                modLogger?.LogGeneral("UID resolution skipped - no players in AllowedPlayers list");
                return;
            }

            modLogger?.LogGeneral($"Starting UID resolution for {config.AllowedPlayers.Count} players");
            bool configChanged = false;

            foreach (var playerName in config.AllowedPlayers)
            {
                // Skip if we already have the UID for this player
                if (config.AllowedPlayerUIDs.ContainsKey(playerName) && !string.IsNullOrEmpty(config.AllowedPlayerUIDs[playerName]))
                {
                    modLogger?.LogGeneral($"UID already exists for player '{playerName}': {config.AllowedPlayerUIDs[playerName]}");
                    continue;
                }

                // Try to find the player's UID from the server's player data
                var playerData = serverApi.World.AllPlayers.FirstOrDefault(p => p.PlayerName == playerName);

                if (playerData != null)
                {
                    config.AllowedPlayerUIDs[playerName] = playerData.PlayerUID;
                    configChanged = true;
                    serverApi.Logger.Notification($"Resolved UID for player '{playerName}': {playerData.PlayerUID}");
                    modLogger?.LogGeneral($"Resolved UID for player '{playerName}': {playerData.PlayerUID}");
                }
                else
                {
                    // Player hasn't logged in yet, we'll resolve it later
                    if (!config.AllowedPlayerUIDs.ContainsKey(playerName))
                    {
                        config.AllowedPlayerUIDs[playerName] = "";
                    }
                    serverApi.Logger.Warning($"Could not resolve UID for player '{playerName}'. Will be resolved when they join.");
                    modLogger?.LogGeneral($"UID resolution pending for player '{playerName}' - waiting for first join");
                }
            }

            // Save config if we added any UIDs
            if (configChanged)
            {
                serverApi.StoreModConfig<FlightConfigData>(config, "NicNekoAdminStuff.json");
                serverApi.Logger.Notification("Config updated with player UIDs.");
                modLogger?.LogConfigAction("Config saved with newly resolved UIDs");
            }

            // Listen for player joins to resolve UIDs that weren't found
            serverApi.Event.PlayerJoin += OnServerPlayerJoin;
            modLogger?.LogGeneral("UID resolution completed - listening for player joins");
        }

        private void OnServerPlayerJoin(IServerPlayer player)
        {
            modLogger?.LogGeneral($"Player joined - Name: {player.PlayerName}, UID: {player.PlayerUID}");

            if (config == null || config.AllowedPlayers == null)
                return;

            // Check if this player is in the allowed list but doesn't have a UID yet
            if (config.AllowedPlayers.Contains(player.PlayerName))
            {
                if (!config.AllowedPlayerUIDs.ContainsKey(player.PlayerName) ||
                    string.IsNullOrEmpty(config.AllowedPlayerUIDs[player.PlayerName]))
                {
                    config.AllowedPlayerUIDs[player.PlayerName] = player.PlayerUID;
                    serverApi.StoreModConfig<FlightConfigData>(config, "NicNekoAdminStuff.json");
                    serverApi.Logger.Notification($"Resolved and saved UID for player '{player.PlayerName}': {player.PlayerUID}");
                    modLogger?.LogConfigAction($"Auto-registered UID for '{player.PlayerName}': {player.PlayerUID}");
                }
            }
        }

        private void OnFlightPermissionRequest(IServerPlayer player, FlightPermissionRequest packet)
        {
            modLogger?.LogGeneral($"Permission request received - Player: {packet.PlayerName} (UID: {packet.PlayerUid}), Action: {packet.Action}, Requesting: {(packet.RequestingEnable ? "ENABLE" : "DISABLE")}");

            bool hasPermission = CheckFlightPermission(packet.PlayerName, packet.PlayerUid);

            string message;
            if (hasPermission)
            {
                if (packet.Action == "flight")
                {
                    message = packet.RequestingEnable ? "Flight mode enabled!" : "Flight mode disabled!";
                }
                else // flightnoclip
                {
                    message = packet.RequestingEnable ? "Flight+Noclip mode enabled!" : "Flight+Noclip mode disabled!";
                }
                modLogger?.LogPlayerAction(packet.PlayerName, packet.PlayerUid, $"{packet.Action} {(packet.RequestingEnable ? "ENABLED" : "DISABLED")}");
            }
            else
            {
                message = packet.Action == "flight" ?
                    (config?.FlightDeniedMessage ?? "You don't have permission to use flight mode!") :
                    (config?.FlightNoclipDeniedMessage ?? "You don't have permission to use flight+noclip mode!");
                modLogger?.LogPermissionCheck(packet.PlayerName, packet.PlayerUid, packet.Action, false);
            }

            var response = new FlightPermissionResponse
            {
                PlayerUid = packet.PlayerUid,
                HasPermission = hasPermission,
                Message = message,
                Action = packet.Action,
                RequestingEnable = packet.RequestingEnable
            };

            serverChannel.SendPacket(response, player);

            serverApi.Logger.Notification($"{packet.Action} permission check for {packet.PlayerName} ({packet.PlayerUid}): {(hasPermission ? "ALLOWED" : "DENIED")}");
        }

        private bool CheckFlightPermission(string playerName, string playerUid)
        {
            if (config == null)
            {
                modLogger?.LogGeneral("Permission check failed - config is null");
                return false;
            }

            if (config.AllowAllPlayers)
            {
                modLogger?.LogGeneral($"Permission granted to {playerName} - AllowAllPlayers is enabled");
                return true;
            }

            // Check if player UID exists in the registered UIDs
            if (config.AllowedPlayerUIDs != null && config.AllowedPlayerUIDs.ContainsValue(playerUid))
            {
                modLogger?.LogPermissionCheck(playerName, playerUid, "UID match", true);
                return true;
            }

            // If player is in AllowedPlayers but UID not yet registered, allow but warn
            if (config.AllowedPlayers != null && config.AllowedPlayers.Contains(playerName))
            {
                if (config.AllowedPlayerUIDs != null &&
                    (!config.AllowedPlayerUIDs.ContainsKey(playerName) ||
                     string.IsNullOrEmpty(config.AllowedPlayerUIDs[playerName])))
                {
                    // Register UID now
                    config.AllowedPlayerUIDs[playerName] = playerUid;
                    serverApi.StoreModConfig<FlightConfigData>(config, "NicNekoAdminStuff.json");
                    serverApi.Logger.Notification($"Auto-registered UID for player '{playerName}': {playerUid}");
                    modLogger?.LogConfigAction($"Auto-registered UID during permission check - Player: {playerName}, UID: {playerUid}");
                    modLogger?.LogPermissionCheck(playerName, playerUid, "First-time auto-registration", true);
                    return true;
                }

                // If UID is registered but doesn't match, deny access (possible impersonation attempt)
                if (config.AllowedPlayerUIDs != null && config.AllowedPlayerUIDs[playerName] != playerUid)
                {
                    serverApi.Logger.Warning($"UID mismatch for player '{playerName}'! Expected: {config.AllowedPlayerUIDs[playerName]}, Got: {playerUid}. Access DENIED.");
                    modLogger?.LogSecurity($"UID MISMATCH DETECTED - Player: {playerName}, Expected UID: {config.AllowedPlayerUIDs[playerName]}, Received UID: {playerUid} - ACCESS DENIED");
                    return false;
                }
            }

            modLogger?.LogPermissionCheck(playerName, playerUid, "No matching credentials", false);
            return false;
        }

        #endregion

        #region Client-Side Methods

        private void LoadClientConfig()
        {
            try
            {
                // Client loads config read-only to get keybind settings
                var clientConfig = clientApi.LoadModConfig<FlightConfigData>("NicNekoAdminStuff.json");
                if (clientConfig != null)
                {
                    config = clientConfig;
                }
                else
                {
                    // Use default config if none exists
                    config = new FlightConfigData();
                }
            }
            catch (Exception e)
            {
                clientApi.Logger.Error("Could not load client config for keybinds: " + e.Message);
                config = new FlightConfigData();
            }
        }

        private void RegisterKeybinds()
        {
            try
            {
                // Parse flight key (just flight, no noclip)
                var flightKey = ParseKeyCombo(config?.FlightKey ?? "R");
                clientApi.Input.RegisterHotKey("toggleflight", "Toggle Flight Mode", flightKey.Key, HotkeyType.CharacterControls);
                clientApi.Input.SetHotKeyHandler("toggleflight", (comb) => OnToggleFlightWithModifiers(comb, flightKey));

                // Parse flight+noclip key - now just "N"
                var flightNoclipKey = ParseKeyCombo(config?.FlightNoclipKey ?? "Ctrl+R");
                clientApi.Input.RegisterHotKey("toggleflightnoclip", "Toggle Flight+Noclip Mode", flightNoclipKey.Key, HotkeyType.CharacterControls);
                clientApi.Input.SetHotKeyHandler("toggleflightnoclip", (comb) => OnToggleFlightNoclipWithModifiers(comb, flightNoclipKey));

                clientApi.Logger.Notification($"Keybinds registered: Flight={config?.FlightKey ?? "R"}, Flight+Noclip={config?.FlightNoclipKey ?? "Ctrl+R"}");
            }
            catch (Exception e)
            {
                clientApi.Logger.Error("Error registering keybinds: " + e.Message);
                // Fallback to default keys
                clientApi.Input.RegisterHotKey("toggleflight", "Toggle Flight Mode", GlKeys.R, HotkeyType.CharacterControls);
                clientApi.Input.SetHotKeyHandler("toggleflight", OnToggleFlight);
                clientApi.Input.RegisterHotKey("toggleflightnoclip", "Toggle Flight+Noclip Mode", GlKeys.R, HotkeyType.CharacterControls, false, true, false);
                clientApi.Input.SetHotKeyHandler("toggleflightnoclip", OnToggleFlightNoclip);
            }
        }

        private (GlKeys Key, bool Alt, bool Ctrl, bool Shift) ParseKeyCombo(string keyString)
        {
            if (string.IsNullOrEmpty(keyString))
                return (GlKeys.R, false, false, false);

            bool alt = false, ctrl = false, shift = false;
            string mainKey = keyString;

            // Handle modifier combinations
            var parts = keyString.Split('+');
            if (parts.Length > 1)
            {
                for (int i = 0; i < parts.Length - 1; i++)
                {
                    var modifier = parts[i].Trim().ToLower();
                    if (modifier == "alt") alt = true;
                    else if (modifier == "ctrl") ctrl = true;
                    else if (modifier == "shift") shift = true;
                }
                mainKey = parts[parts.Length - 1].Trim();
            }

            GlKeys key = ParseSingleKey(mainKey);
            return (key, alt, ctrl, shift);
        }

        private GlKeys ParseSingleKey(string keyString)
        {
            if (string.IsNullOrEmpty(keyString))
                return GlKeys.R;

            // Handle single character keys
            if (keyString.Length == 1)
            {
                char c = keyString.ToUpper()[0];
                if (c >= 'A' && c <= 'Z')
                {
                    return (GlKeys)Enum.Parse(typeof(GlKeys), c.ToString());
                }
            }

            // Try to parse as GlKeys enum
            if (Enum.TryParse<GlKeys>(keyString, true, out GlKeys result))
            {
                return result;
            }

            // Default fallback
            return GlKeys.R;
        }

        private void OnPlayerJoin(IClientPlayer byPlayer)
        {
            player = byPlayer;
            if (player?.Entity?.Properties != null)
            {
                originalFallDamageMultiplier = player.Entity.Properties.FallDamageMultiplier;
            }
        }

        private void OnFlightPermissionResponse(FlightPermissionResponse packet)
        {
            var currentPlayer = clientApi.World.Player;
            if (currentPlayer == null || packet.PlayerUid != currentPlayer.PlayerUID)
                return;

            string key = $"{packet.PlayerUid}:{packet.Action}:{packet.RequestingEnable}";

            if (pendingRequests.ContainsKey(key))
            {
                pendingRequests[key].SetResult(packet);
                pendingRequests.Remove(key);
            }

            waitingForPermissionResponse = false;
        }

        private bool OnToggleFlight(KeyCombination comb)
        {
            if (player == null || clientApi.World.Player == null)
                return false;

            if (waitingForPermissionResponse)
            {
                if (config?.ShowNotifications ?? true)
                {
                    clientApi.ShowChatMessage("Please wait, processing previous request...");
                }
                return true;
            }

            _ = ToggleFlightModeAsync();
            return true;
        }

        private bool OnToggleFlightWithModifiers(KeyCombination comb, (GlKeys Key, bool Alt, bool Ctrl, bool Shift) expectedKey)
        {
            if (comb.Alt != expectedKey.Alt || comb.Ctrl != expectedKey.Ctrl || comb.Shift != expectedKey.Shift)
                return false;

            return OnToggleFlight(comb);
        }

        private bool OnToggleFlightNoclip(KeyCombination comb)
        {
            if (player == null || clientApi.World.Player == null)
                return false;

            if (waitingForPermissionResponse)
            {
                if (config?.ShowNotifications ?? true)
                {
                    clientApi.ShowChatMessage("Please wait, processing previous request...");
                }
                return true;
            }

            _ = ToggleFlightNoclipModeAsync();
            return true;
        }

        private bool OnToggleFlightNoclipWithModifiers(KeyCombination comb, (GlKeys Key, bool Alt, bool Ctrl, bool Shift) expectedKey)
        {
            if (comb.Alt != expectedKey.Alt || comb.Ctrl != expectedKey.Ctrl || comb.Shift != expectedKey.Shift)
                return false;

            return OnToggleFlightNoclip(comb);
        }

        private async Task<FlightPermissionResponse> RequestPermissionAsync(string action, bool requestingEnable)
        {
            var currentPlayer = clientApi.World.Player;
            if (currentPlayer == null)
                return new FlightPermissionResponse { HasPermission = false, Message = "Player not found" };

            string key = $"{currentPlayer.PlayerUID}:{action}:{requestingEnable}";

            var tcs = new TaskCompletionSource<FlightPermissionResponse>();
            pendingRequests[key] = tcs;

            var request = new FlightPermissionRequest
            {
                PlayerUid = currentPlayer.PlayerUID,
                PlayerName = currentPlayer.PlayerName,
                Action = action,
                RequestingEnable = requestingEnable
            };

            waitingForPermissionResponse = true;
            clientChannel.SendPacket(request);

            try
            {
                var timeoutTask = Task.Delay(5000);
                var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);

                if (completedTask == tcs.Task)
                {
                    return await tcs.Task;
                }
                else
                {
                    return new FlightPermissionResponse
                    {
                        HasPermission = false,
                        Message = "Permission request timed out. Please try again."
                    };
                }
            }
            catch (Exception ex)
            {
                clientApi.Logger.Error($"Error requesting {action} permission: " + ex.Message);
                return new FlightPermissionResponse
                {
                    HasPermission = false,
                    Message = "Error checking permissions."
                };
            }
            finally
            {
                pendingRequests.Remove(key);
                waitingForPermissionResponse = false;
            }
        }

        private async Task ToggleFlightModeAsync()
        {
            try
            {
                // If flight+noclip is currently on, disable it first
                if (isFlightNoclip)
                {
                    DisableAllFlight();
                }

                bool requestingEnable = !isFlying;

                var permissionResponse = await RequestPermissionAsync("flight", requestingEnable);

                // Show message only if notifications are enabled
                if (config?.ShowNotifications ?? true)
                {
                    clientApi.ShowChatMessage(permissionResponse.Message);
                }

                if (!permissionResponse.HasPermission)
                {
                    return;
                }

                var playerEntity = clientApi.World.Player.Entity;
                var worldData = clientApi.World.Player.WorldData;

                if (!isFlying)
                {
                    // Enable flight mode only (no noclip)
                    worldData.FreeMove = true;
                    worldData.NoClip = false; // Ensure noclip is off
                    playerEntity.Properties.FallDamageMultiplier = 0f;
                    worldData.MoveSpeedMultiplier = 1f;
                    worldData.EntityControls.MovespeedMultiplier = 1f;
                    worldData.FreeMovePlaneLock = EnumFreeMovAxisLock.None;

                    isFlying = true;
                }
                else
                {
                    // Disable flight mode
                    DisableAllFlight();
                }
            }
            catch (Exception ex)
            {
                clientApi.Logger.Error("Error toggling flight mode: " + ex.Message);
                if (config?.ShowNotifications ?? true)
                {
                    clientApi.ShowChatMessage("Error toggling flight mode. Check logs for details.");
                }
            }
        }

        private async Task ToggleFlightNoclipModeAsync()
        {
            try
            {
                // If regular flight is currently on, disable it first
                if (isFlying)
                {
                    DisableAllFlight();
                }

                bool requestingEnable = !isFlightNoclip;

                var permissionResponse = await RequestPermissionAsync("flightnoclip", requestingEnable);

                // Show message only if notifications are enabled
                if (config?.ShowNotifications ?? true)
                {
                    clientApi.ShowChatMessage(permissionResponse.Message);
                }

                if (!permissionResponse.HasPermission)
                {
                    return;
                }

                var playerEntity = clientApi.World.Player.Entity;
                var worldData = clientApi.World.Player.WorldData;

                if (!isFlightNoclip)
                {
                    // Enable flight + noclip mode
                    worldData.FreeMove = true;
                    worldData.NoClip = true;
                    playerEntity.Properties.FallDamageMultiplier = 0f;
                    worldData.MoveSpeedMultiplier = 1f;
                    worldData.EntityControls.MovespeedMultiplier = 1f;
                    worldData.FreeMovePlaneLock = EnumFreeMovAxisLock.None;

                    isFlightNoclip = true;
                }
                else
                {
                    // Disable flight + noclip mode
                    DisableAllFlight();
                }
            }
            catch (Exception ex)
            {
                clientApi.Logger.Error("Error toggling flight+noclip mode: " + ex.Message);
                if (config?.ShowNotifications ?? true)
                {
                    clientApi.ShowChatMessage("Error toggling flight+noclip mode. Check logs for details.");
                }
            }
        }

        private void DisableAllFlight()
        {
            var playerEntity = clientApi.World.Player.Entity;
            var worldData = clientApi.World.Player.WorldData;

            worldData.FreeMove = false;
            worldData.NoClip = false;
            playerEntity.Properties.FallDamageMultiplier = originalFallDamageMultiplier;
            worldData.MoveSpeedMultiplier = 1f;
            worldData.EntityControls.MovespeedMultiplier = 1f;
            worldData.FreeMovePlaneLock = EnumFreeMovAxisLock.None;

            playerEntity.PositionBeforeFalling = playerEntity.Pos.XYZ;

            if (playerEntity.Pos.Motion.Y < -0.5)
            {
                playerEntity.Pos.Motion.Y = -0.5;
            }

            isFlying = false;
            isFlightNoclip = false;
        }

        #endregion

        public override void Dispose()
        {
            if ((isFlying || isFlightNoclip) && player != null && clientApi != null)
            {
                try
                {
                    var playerEntity = clientApi.World.Player.Entity;
                    var worldData = clientApi.World.Player.WorldData;

                    worldData.FreeMove = false;
                    worldData.NoClip = false;
                }
                catch (Exception ex)
                {
                    clientApi?.Logger?.Error("Error during cleanup: " + ex.Message);
                }
            }

            modLogger?.LogGeneral("Mod disposing/shutting down");
            base.Dispose();
        }
    }
}
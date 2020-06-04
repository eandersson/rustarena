using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Oxide.Core;
using Oxide.Core.Configuration;
using Oxide.Core.Libraries;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Plugins;
using Oxide.Game.Rust;
using Oxide.Game.Rust.Cui;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using Rust;
using UnityEngine;


namespace Oxide.Plugins
{
    [Info("RustArena", "eandersson", "0.3.2", ResourceId = 2681)]
    class RustArena : RustPlugin
    {
        #region Variables & Constants

        private readonly static int LayerGround = Rust.Layers.Solid | Rust.Layers.Mask.Water;

        private const string PrefabHighExternalStoneWall = "assets/prefabs/building/wall.external.high.stone/wall.external.high.stone.prefab";
        private const string PrefabRowboat = "assets/content/vehicles/boats/rowboat/rowboat.prefab";
        private const string PrefabMapmarker = "assets/prefabs/tools/map/genericradiusmarker.prefab";
        private const string TeamUi = "TeamUi";
        private const string StageUiPanel = "StageUiPanel";
        private const string TimeUiPanel = "TimeUiPanel";
        private const string TcUiPanel = "TcUiPanel";
        private const string TeamUiPanel = "TeamUiPanel";
        private const string TicketUiPanel = "TicketUiPanel";

        private const int GatherRate = 6;
        private const int PickupRate = 4;
        private const int LootRate = 2;
        private const int BoostedLootRate = 5;
        private const int TeamStartingTickets = 200;
        private int MaxPlayersPerTeam = RelationshipManager.maxTeamSize;

        private MatchData matchData;
        private bool initialized = false;

        private List<BasePlayer> cooldown = new List<BasePlayer>();
        private List<BasePlayer> deadPlayers = new List<BasePlayer>();
        private List<BasePlayer> votes = new List<BasePlayer>();
        private Dictionary<ulong, BasePlayer> players = new Dictionary<ulong, BasePlayer>();

        private List<Vector3> initialStartingPositions = new List<Vector3>();
        private Dictionary<int, List<Vector3>> teamStartingPositions = new Dictionary<int, List<Vector3>>()
        {
            {0, new List<Vector3>()},
            {1, new List<Vector3>()},
            {2, new List<Vector3>()},
            {3, new List<Vector3>()},
        };
        private Dictionary<int, RelationshipManager.PlayerTeam> parties = new Dictionary<int, RelationshipManager.PlayerTeam>();
        private Dictionary<int, Color> teamColors = new Dictionary<int, Color>()
        {
            {0, Color.red},
            {1, Color.blue},
            {2, Color.green},
            {3, Color.yellow},
        };

        private List<string> notBoostedItems = new List<string>()
        {
            "guitar.item",
            "hat.cap.base.item",
            "mask.bandana.item",
            "hoodie.red.item",
            "binoculars.item",
            "antiradpills.item",
            "crude_oil.item",
            "gloves.leather.item",
            "pants.cargo.item",
            "hat.boonie.item",
            "smallwaterbottle.item",
            "can_beans.item",
            "tshirt.long.blue.item",
            "sign.wooden.large.item",
            "picklejar.item",
            "jacket.snow.item",
            "tshirt.green.item",
            "shirt.tanktop.item",
            "burlap_shirt.item",
            "shirt.collared.item",
            "pants.burlap.item",
            "pants.shorts.item",
            "corn_seed.item",
            "hemp_seed.item",
            "pumpkin_seed.item",
            "clone.corn.item",
            "clone.hemp.item",
            "clone.pumpkin.item",
        };

        private Dictionary<string, float> craftableBuildTime = new Dictionary<string, float>
        {
            {"gunpowder.item", 0.5f},
            {"explosive.timed.item", 10.0f},
            {"explosives.item", 1.0f},
        };

        private List<string> boostedItems = new List<string>()
        {
            "scrap.item",
            "fuel.lowgrade.item",
        };

        private Dictionary<string, int> hackableLootItems = new Dictionary<string, int>
        {
            {"riflebody", 10},
            {"smgbody", 10},
            {"rocket.launcher", 1},
            {"ammo.rocket.basic", 8},
            {"supply.signal", 5},
            {"scrap", 1250},
        };

        private List<string> startItems = new List<string>
        {
            "pickaxe", 
            "hatchet"
        };

        public class Team
        {
            public int Id;
            public string Name;
            public List<ulong> Players = new List<ulong>();
            public string TeamCupboard;
            public string TeamCupboardMarker;
            public string Color;
            public bool IsOut = false;
            public int TicketsRemaning = TeamStartingTickets;

            public bool HasPlayers()
            {
                return this.Players.Count > 0;
            }

            public Team(int id, string name, string color)
            {
                this.Id = id;
                this.Name = name;
                this.Color = color;
            }
        }

        public class MatchData
        {
            public string id = Guid.NewGuid().ToString();
            public int CurrentStage = 0;
            public string StageEndTime;
            public string StageStartTime;
            public string ServerStartTime = DateTime.UtcNow.ToString();
            public string CurrentStageName;
            public bool IsPaused = false;
            public bool MapInitialized = false;
            public bool GameOver = false;
            public Team WinningTeam = null;

            public void ResetTeamTickets()
            {
                foreach (Team team in this.Teams.Values)
                {
                    team.TicketsRemaning = TeamStartingTickets;
                    team.IsOut = false;
                }
            }

            public Dictionary<int, Team> Teams = new Dictionary<int, Team>()
            {
                {0, new Team(0, "Red", "0.8 0 0 1")},
                {1, new Team(1, "Blue", "0 0 1 1")},
                {2, new Team(2, "Green", "0 0.8 0 1")},
                {3, new Team(3, "Yellow", "0.8 0.8 0 1")},
            };

            public MatchData()
            {
            }
        }

        #endregion

        #region ConsoleCommands

        [ConsoleCommand("rustarena.join")]
        private void ChangeTeamConsole(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;

            if (matchData.CurrentStage != 0 && (GetPlayerTeam(player) != null))
            {
                SendReply(player, "Not allowed to change team after the game has started.");
                return;
            }

            if (arg.Args.Length != 1)
            {
                ShowTeamUi(player);
                return;
            }

            try
            {
                Team team = GetTeam(arg.Args[0].ToLower());
                if (team == null) return;

                if (team.Players.Count() + 1 > MaxPlayersPerTeam)
                {
                    SendReply(player, string.Format("Too many players already on team {0}", team.Name));
                    return;
                }

                JoinTeam(player, team);
                CuiHelper.DestroyUi(arg.Player(), TeamUi);

                if (matchData.CurrentStage != 0) player.Hurt(1000);
            }
            catch (Exception e)
            {
                Puts(e.ToString());
                return;
            }
        }

        [ConsoleCommand("rustarena.game.id")]
        private void GetGameIdConsole(ConsoleSystem.Arg arg)
        {
            if (arg.Player() != null && !arg.Player().IsAdmin) return;
            SendReply(arg, matchData.id);
            return;
        }

        [ConsoleCommand("rustarena.game.status")]
        private void GetGameStatusConsole(ConsoleSystem.Arg arg)
        {
            if (arg.Player() != null && !arg.Player().IsAdmin) return;
            SendReply(arg, JsonConvert.SerializeObject(matchData));
            return;
        }

        [ConsoleCommand("rustarena.teams")]
        private void GetTeamsConsole(ConsoleSystem.Arg arg)
        {
            if (arg.Player() != null && !arg.Player().IsAdmin) return;
            SendReply(arg, JsonConvert.SerializeObject(matchData.Teams));
            return;
        }

        #endregion

        #region ChatCommands

        [ChatCommand("about")]
        private void AboutRustArenaCmd(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;
        }

        [ChatCommand("addwarmup")]
        private void AddWarmupRustArenaCmd(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;
            if (!IsAdmin(player)) return;

            if (matchData.CurrentStage == 0)
            {
                DateTime now = DateTime.UtcNow;
                DateTime rs = now.AddHours(1f);
                matchData.StageEndTime = rs.ToString();
                covalence.Server.Broadcast("Added 1 hour to Warmup");
            }
        }

        [ChatCommand("join")]
        private void ChangeTeamCmd(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;

            if (matchData.CurrentStage != 0 && (GetPlayerTeam(player) != null))
            {
                SendReply(player, "Not allowed to change team after the game has started.");
                return;
            }

            if (args.Length != 1)
            {
                ShowTeamUi(player);
                return;
            }

            try
            {
                Team team = GetTeam(args[0].ToLower());
                if (team == null) return;

                if (team.Players.Count() + 1 > MaxPlayersPerTeam)
                {
                    SendReply(player, string.Format("Too many players already on team {0}", team.Name));
                    return;
                }

                JoinTeam(player, team);
                CuiHelper.DestroyUi(player, TeamUi);

                if (matchData.CurrentStage != 0) player.Hurt(1000);
            }
            catch (Exception e)
            {
                Puts(e.ToString());
                SendReply(player, "Need to pick red, blue, green or yellow (e.g. /join green)");
                return;
            }
        }

        [ChatCommand("settickets")]
        private void SetTicketsCmd(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;
            if (!IsAdmin(player)) return;
            if (args.Length != 2) return;

            try
            {
                Team team = GetTeam(args[0].ToLower());
                int tickets = Convert.ToInt32(args[1].ToLower());
                
                if (tickets >= 0 && tickets < 9999)
                    team.TicketsRemaning = tickets;
            }
            catch
            {

            }
        }

        [ChatCommand("leave")]
        private void LeaveTeamCmd(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;

            if (matchData.CurrentStage != 0)
            {
                SendReply(player, "Not allowed to leave a team after the game has started.");
                return;
            }

            LeaveTeam(player);
        }

        [ChatCommand("vote")]
        private void VoteCmd(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;
            if (matchData.CurrentStage > 0) return;

            if (args.Length != 1 || args[0].ToLower() != "start")
            {
                SendReply(player, "Need to provide start (e.g. /vote start)");
                return;
            }

            if (votes.Contains(player))
            {
                SendReply(player, "Not allowed to vote twice.");
                return;
            }

            votes.Add(player);
            if (votes.Count() > 8)
            {
                if (matchData.CurrentStage == 0) StartPreparationStage();
            }
            else;
            {
                covalence.Server.Broadcast(string.Format("{0} out of 8 votes required to start the game", votes.Count()));
            }
        }

        [ChatCommand("next")]
        private void NextStageCmd(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;
            if (!IsAdmin(player)) return;

            if (matchData.CurrentStage >= 2) return;

            matchData.CurrentStage += 1;
            if (matchData.CurrentStage == 1) StartPreparationStage();
            if (matchData.CurrentStage == 2) StartRaidingStage();
        }

        [ChatCommand("reset")]
        private void ResetStageCmd(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;
            if (!IsAdmin(player)) return;

            StartWarmupStage();
        }

        [ChatCommand("start")]
        private void StartCmd(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;
            if (!IsAdmin(player)) return;

            if (matchData.CurrentStage == 0) StartPreparationStage();
        }

        [ChatCommand("respawn")]
        private void RespawnCmd(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;
            if (!IsAdmin(player)) return;

            RespawnPlayers();
        }

        [ChatCommand("team")]
        private void TeamCmd(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;

            var matches = matchData.Teams.Where(t => t.Value.Players.Any(p => p == player.userID));
            foreach (var team in matches)
            {
                SendReply(player, string.Format("You are currently on team {0}.", team.Value.Name));
            }
        }

        [ChatCommand("pause")]
        private void PauseRustArenaCmd(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;
            if (!IsAdmin(player)) return;

            if (matchData.CurrentStage == 0)
            {
                matchData.IsPaused = true;
                covalence.Server.Broadcast("Paused Warmup");
            }
        }

        [ChatCommand("resume")]
        private void ResumeRustArenaCmd(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;
            if (!IsAdmin(player)) return;

            if (matchData.CurrentStage == 0)
            {
                matchData.IsPaused = false;
                covalence.Server.Broadcast("Resumed Warmup");
            }
        }

        [ChatCommand("createwalls")]
        private void CreateWallsCmd(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;
            if (!IsAdmin(player)) return;

            CreateArenaWalls();
        }

        [ChatCommand("removewalls")]
        private void RemoveWallsCmd(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;
            if (!IsAdmin(player)) return;

            RemoveArenaWalls();
        }

        #endregion

        private bool IsAdmin(BasePlayer player)
        {
            return player.IsAdmin;
        }

        private string GetPlayerName(ulong id)
        {
            if (!players.ContainsKey(id)) return "null";
            return players[id].displayName;
        }

        private Team GetPlayerTeam(BasePlayer player)
        {
            var matches = matchData.Teams.Where(t => t.Value.Players.Any(p => p == player.userID));
            return matches.Any() ? matches.First().Value : null;
        }

        private Team GetPlayerTeam(ulong userID)
        {
            var matches = matchData.Teams.Where(t => t.Value.Players.Any(p => p == userID));
            return matches.Any() ? matches.First().Value : null;
        }

        private Team GetTeam(int teamId)
        {
            return matchData.Teams[teamId];
        }

        private Team GetTeam(string name)
        {
            var matches = matchData.Teams.Where(t => t.Value.Name.ToLower() == name.ToLower());
            return matches.Any() ? matches.First().Value : null;
        }

        private Team GetCupboardOwner(string cupboard)
        {
            var matches = matchData.Teams.Values.Where(t => t.TeamCupboard != null && t.TeamCupboard == cupboard);
            return matches.Any() ? matches.First() : null;
        }

        private List<Team> GetTeamsWithCupboards()
        {
            return matchData.Teams.Values.Where(t => t.TeamCupboard != null).ToList();
        }

        private int GetRemaningToolCupboards()
        {
            return GetTeamsWithCupboards().Count();
        }

        private bool IsPlayerOnATeam(BasePlayer player)
        {
            var matches = matchData.Teams.Where(t => t.Value.Players.Any(p => p == player.userID));
            if (matches.Any())
            {
                if (matches.Count() > 1) Puts("Player {0} ({1}) found in multiple teams.", player.displayName, player.userID);
                return true;
            }

            return false;
        }

        private string GetCurrentStage()
        {
            return matchData.CurrentStageName;
        }

        private TimeSpan GetTimeLeft()
        {
            return Convert.ToDateTime(matchData.StageEndTime).Subtract(DateTime.UtcNow);
        }

        private void JoinTeam(BasePlayer player, Team newTeam)
        {
            if (player == null) return;
            if (newTeam == null) return;

            foreach (Team team in matchData.Teams.Values)
            {
                if (team == newTeam) continue;

                if (team.Players.Contains(player.userID))
                {
                    team.Players.Remove(player.userID);
                }

                if (parties.ContainsKey(team.Id))
                {
                    parties[team.Id].RemovePlayer(player.userID);
                }
            }

            player.ClearTeam();
            CreateOrValidateTeam(newTeam);

            if (!parties.ContainsKey(newTeam.Id))
            {
                Puts("No such party!");
            }

            if (!newTeam.Players.Contains(player.userID))
            {
                newTeam.Players.Add(player.userID);
            }

            if (!parties[newTeam.Id].AddPlayer(player))
            {
                Puts("Already on team");
            }

            if (parties[newTeam.Id].GetLeader() == null)
            {
                parties[newTeam.Id].SetTeamLeader(player.userID);
                Puts("No leader!");
            }

            covalence.Server.Broadcast(string.Format("{0} joined team {1}.", player.displayName, newTeam.Name));
        }

        private void CreateOrValidateTeam(Team team)
        {
            if (!parties.ContainsKey(team.Id))
            {
                parties.Add(team.Id, RelationshipManager.Instance.CreateTeam());
                return;
            }

            var currentTeam = RelationshipManager.Instance.FindTeam(parties[team.Id].teamID);
            if (currentTeam == null)
            {
                Puts("Re-creating bad team!");
                if (parties.ContainsKey(team.Id))
                {
                    if (parties[team.Id] != null)
                    {
                        Puts("Trying to Disband previous team!");
                        parties[team.Id].Disband();
                    }

                    parties.Remove(team.Id);
                }

                parties.Add(team.Id, RelationshipManager.Instance.CreateTeam());
            }
        }

        private void ValidateAndFixTeams()
        {
            foreach (BasePlayer player in BasePlayer.activePlayerList)
            {
                Team team = GetPlayerTeam(player);

                if (player.currentTeam == 0UL && team != null)
                {
                    CreateOrValidateTeam(team);

                    if (!parties.ContainsKey(team.Id))
                    {
                        Puts("No such party!");
                    }

                    if (!parties[team.Id].AddPlayer(player))
                    {
                        Puts("Already on team");
                    }

                    if (parties[team.Id].GetLeader() == null)
                    {
                        parties[team.Id].SetTeamLeader(player.userID);
                        Puts("No leader!");
                    }
                }
            }
        }

        private void LeaveTeam(BasePlayer player)
        {
            if (player == null) return;

            covalence.Server.Broadcast(string.Format("{0} left team {1}.", player.displayName, GetPlayerTeam(player).Name));

            foreach (Team team in matchData.Teams.Values)
            {
                if (team.Players.Contains(player.userID))
                {
                    team.Players.Remove(player.userID);
                }

                if (parties.ContainsKey(team.Id))
                {
                    parties[team.Id].RemovePlayer(player.userID);
                }
            }

            player.ClearTeam();
        }

        private Vector3 GetSpawnPoint(BasePlayer player)
        {
            Vector3 position;
            Team team = GetPlayerTeam(player);

            if (team == null)
            {

                var randomPosition = UnityEngine.Random.Range(0, initialStartingPositions.Count);
                position = initialStartingPositions[randomPosition];
            }
            else
            {
                var randomPosition = UnityEngine.Random.Range(0, teamStartingPositions[team.Id].Count);
                position = teamStartingPositions[team.Id][randomPosition];
            }

            return position;
        }

        private void TeamLost(Team team)
        {
            if (team == null) return;

            team.IsOut = true;
            team.TicketsRemaning = 0;

            RemoveTeamCupboard(team);
            DestroyMarker(team);

            foreach (ulong playerId in team.Players)
            {
                BasePlayer player = BasePlayer.FindByID(playerId);
                if (player == null) continue;
                player.Hurt(1000);
            }

            covalence.Server.Broadcast(string.Format("Team {0} is out!", team.Name));

            int remaning = GetRemaningToolCupboards();
            if (remaning <= 1)
            {
                Team winningTeam = GetTeamsWithCupboards().FirstOrDefault();
                if (winningTeam != null)
                {
                    covalence.Server.Broadcast(string.Format("Team {0} Won!", winningTeam.Name));
                    matchData.WinningTeam = winningTeam;
                }

                StartGameOverStage();
            }
        }

        private T GetConfig<T>(string name, T defaultValue)
        {
            if (Config[name] == null) return defaultValue;
            return (T)Convert.ChangeType(Config[name], typeof(T));
        }

        private void Init()
        {
            try
            {
                matchData = Interface.Oxide.DataFileSystem.ReadObject<MatchData>(this.Name);
            }
            catch
            {
                matchData = new MatchData();
            }
        }

        private Vector3 GetGroundPosition(Vector3 position)
        {
            RaycastHit hitinfo;
            if (Physics.Raycast(position + Vector3.up * 200, Vector3.down, out hitinfo, 250f, LayerGround))
                position.y = hitinfo.point.y;
            else position.y = TerrainMeta.HeightMap.GetHeight(position);

            return position;
        }

        private void CreateTeamSpawnPoints()
        {
            Puts("Creating Team Spawn Points");
            int currentTeam = 0;
            foreach (MonumentInfo monument in UnityEngine.Object.FindObjectsOfType<MonumentInfo>())
            {
                if (!monument.name.ToLower().Contains("warehouse")) continue;
                if (!teamStartingPositions.ContainsKey(currentTeam)) continue;

                teamStartingPositions[currentTeam].Clear();
                List<BaseEntity> list = new List<BaseEntity>();
                Vis.Entities(monument.transform.position, 20, list);
                foreach (BaseEntity entity in list)
                {
                    if (entity.name.ToLower().Contains("card")) continue;
                    if (entity.name.ToLower().Contains("door")) continue;
                    Vector3 chairPos = entity.transform.position;
                    chairPos.y += 1.0f;
                    teamStartingPositions[currentTeam].Add(chairPos);
                }

                currentTeam += 1;
            }
        }

        private void CreateInitialSpawnPoints()
        {
            Puts("Creating Initial Spawn Points");

            initialStartingPositions.Clear();
            foreach (var spawn in UnityEngine.Object.FindObjectsOfType<GameObject>())
            {
                if (spawn.name.Contains("spawn_point"))
                {
                    Vector3 chairPos = spawn.transform.position;
                    chairPos.y += 1;
                    initialStartingPositions.Add(chairPos);
                }
            }
        }

        private void SpawnStartingBoats()
        {
            foreach (LiquidContainer container in UnityEngine.Object.FindObjectsOfType<LiquidContainer>())
            {
                if (!container.name.ToLower().Contains("water")) continue;

                for (float i = 0; i < 3; i++)
                {
                    Vector3 spawnPos = GetGroundPosition(container.transform.position);

                    // TODO(eandersson): Do something more reasonable here.
                    spawnPos.x += (i * 5f);
                    spawnPos.z += (i + 5f);
                    spawnPos.y += (i + 1f);
                    var normalized = (spawnPos - container.transform.position).normalized;
                    var angle = normalized != Vector3.zero
                        ? Quaternion.LookRotation(normalized).eulerAngles.y
                        : UnityEngine.Random.Range(0f, 360f);
                    Quaternion rotation = Quaternion.Euler(new Vector3(0f, angle + 90f, 0f));
                    spawnPos += Vector3.up * 1.5f;
                    MotorRowboat entity = GameManager.server.CreateEntity(PrefabRowboat, spawnPos, rotation, true) as MotorRowboat;
                    entity.Spawn();
                }
            }
        }

        private void OnNewSave(string filename)
        {
            matchData.MapInitialized = false;
            foreach (Team team in matchData.Teams.Values)
            {
                team.TeamCupboard = null;
                team.Players.Clear();
            }
            
            Startup();
        }

        private void Startup()
        {
            Puts("Startup called...");
            NextTick(() =>
            {
                Puts("Startup NextTick called...");
                if (matchData.MapInitialized == true)
                    return;

                SpawnStartingBoats();
                SetBuildTimes();
                SetStackSizes();
                StartWarmupStage();
                matchData.MapInitialized = true;
            });
        }

        private void SetBuildTimes()
        {
            foreach (var bp in ItemManager.bpList)
            {
                if (bp.userCraftable)
                {
                    if (craftableBuildTime.ContainsKey(bp.name))
                        bp.time = craftableBuildTime[bp.name];
                    else if (bp.name.StartsWith("ammo"))
                        bp.time = 1.0f;
                    else
                        bp.time = 5.0f;
                }
                    
            }
        }

        void SetStackSizes()
        {
            var gameitemList = ItemManager.itemList;
            foreach (var item in gameitemList)
            {
                if (item.stackable == 1000)
                {
                    item.stackable = 5000;
                }
                else if (item.stackable == 128)
                {
                    item.stackable = 512;
                }
                else if (item.name.Contains("ammo_rocket"))
                {
                    item.stackable = 8;
                }
                else if (item.name.Contains("fuel.lowgrade.item"))
                {
                    item.stackable = 1000;
                }
            }
        }

        private void Teleport(BasePlayer player, Vector3 targetPosition)
        {
            player.EnsureDismounted();
            player.Teleport(GetGroundPosition(targetPosition));
        }

        private void RespawnPlayers()
        {
            // TODO(eandersson): Respawn individually based on death timer.
            foreach (BasePlayer player in deadPlayers.ToList())
            {
                try
                {
                    Team team = GetPlayerTeam(player);
                    if (matchData.CurrentStage >= 2 && team?.TeamCupboard == null)
                    {
                        continue;
                    }

                    if (team != null)
                    {
                        Vector3 position = GetSpawnPoint(player);
                        Teleport(player, position);
                        NextTick(() => { GiveStartingItems(player); });
                        continue;
                    }
                }
                finally
                {
                    deadPlayers.Remove(player);
                }
            }
        }

        private void Loaded()
        {
            Puts("Loaded called...");

            covalence.Server.Command("decay.upkeep true");
            covalence.Server.Command("decay.upkeep_period_minutes 7200");
            covalence.Server.Command("decay.upkeep_grief_protection 0");
            
            covalence.Server.Command("spawn.min_rate 1");
            covalence.Server.Command("spawn.max_rate 1");
            covalence.Server.Command("spawn.min_density 1");
            covalence.Server.Command("spawn.max_density 1");

            NextTick(() =>
            {
                Puts("Loading timers...");

                StartStageUiTimer();
                StartTeamUiTimer();
                StartStageTimer();
                StartRespawnTimer();
                StartTeamValidationTimer();
                PopulatePlayerList();
            });
        }

        private void PopulatePlayerList()
        {
            foreach (BasePlayer player in BasePlayer.activePlayerList)
            {
                players[player.userID] = player;
            }
        }

        private void Unload()
        {
            Puts("Unload called...");

            RespawnPlayers();
            initialStartingPositions.Clear();
            teamStartingPositions.Clear();
            
            SaveFile();
        }

        #region Timers

        private void StartStageUiTimer()
        {
            timer.Every(1.0f, () =>
            {
                if (!initialized) return;

                try
                {
                    foreach (BasePlayer player in BasePlayer.activePlayerList)
                    {
                        UpdateStageUi(player);
                    }
                }
                catch (Exception e)
                {
                    Puts(e.ToString());
                }
            });
        }

        private void StartTeamUiTimer()
        {
            timer.Every(10.0f, () =>
            {
                if (!initialized) return;

                try
                {
                    foreach (BasePlayer player in BasePlayer.activePlayerList)
                    {
                        if (GetPlayerTeam(player) == null)
                        {
                            ShowTeamUi(player);
                        }
                    }
                }
                catch (Exception e)
                {
                    Puts(e.ToString());
                }
            });
        }

        private void StartStageTimer()
        {
            timer.Every(5.0f, () =>
            {
                if (!initialized) return;

                try
                {
                    if (matchData.IsPaused == true)
                    {
                        DateTime now = DateTime.UtcNow;
                        DateTime rs = now.AddMinutes(15f);
                        matchData.StageEndTime = rs.ToString();
                        return;
                    }

                    if (matchData.CurrentStage >= 2) return;

                    if (DateTime.UtcNow >= Convert.ToDateTime(matchData.StageEndTime))
                    {
                        matchData.CurrentStage += 1;
                        if (matchData.CurrentStage == 1) StartPreparationStage();
                        if (matchData.CurrentStage == 2) StartRaidingStage();

                        return;
                    }
                }
                catch (Exception e)
                {
                    Puts(e.ToString());
                }
            });

        }

        private void StartRespawnTimer()
        {
            timer.Every(90.0f, () =>
            {
                if (!initialized) return;

                try
                {
                    if (matchData.CurrentStage == 0) return;
                    RespawnPlayers();
                }
                catch (Exception e)
                {
                    Puts(e.ToString());
                }
            });
        }

        private void StartTeamValidationTimer()
        {
            timer.Every(30.0f, () =>
            {
                if (!initialized) return;

                try
                {
                    ValidateAndFixTeams();
                }
                catch (Exception e)
                {
                    Puts(e.ToString());
                }
            });
        }

        #endregion

        #region Stages

        private void StartWarmupStage()
        {
            DateTime now = DateTime.UtcNow;
            DateTime rs = now.AddMinutes(15f);
            matchData.CurrentStage = 0;
            matchData.StageEndTime = rs.ToString();
            matchData.StageStartTime = DateTime.UtcNow.ToString();
            matchData.CurrentStageName = "Warmup";
            matchData.WinningTeam = null;
            matchData.GameOver = false;
            matchData.ResetTeamTickets();
            RemoveArenaWalls();
            RemoveCupboards();
            votes.Clear();

            foreach (BasePlayer player in BasePlayer.activePlayerList)
            {
                try
                {
                    var randomPosition = UnityEngine.Random.Range(0, initialStartingPositions.Count);
                    Vector3 position = initialStartingPositions[randomPosition];

                    Teleport(player, position);

                    NextTick(() => { ClearInventory(player); });
                }
                catch
                {
                }
            }

            covalence.Server.Broadcast("Warmup phase has started!");
            SaveFile();
        }

        private void StartPreparationStage()
        {
            DateTime now = DateTime.UtcNow;
            DateTime rs = now.AddMinutes(60f);
            matchData.CurrentStage = 1;
            matchData.StageEndTime = rs.ToString();
            matchData.StageStartTime = DateTime.UtcNow.ToString();
            matchData.CurrentStageName = "Preparation";
            matchData.GameOver = false;
            matchData.ResetTeamTickets();
            CreateArenaWalls();
            votes.Clear();

            foreach (BasePlayer player in BasePlayer.activePlayerList)
            {
                if (IsPlayerOnATeam(player) != true)
                {
                    Team smallestTeam = matchData.Teams.OrderBy(k => k.Value.Players.Count).FirstOrDefault().Value;
                    JoinTeam(player, smallestTeam);
                }

                CuiHelper.DestroyUi(player, TeamUi);
            }

            foreach (BasePlayer player in BasePlayer.activePlayerList)
            {
                Team team = GetPlayerTeam(player);
                if (team == null) continue;

                Teleport(player, GetSpawnPoint(player));

                NextTick(() => { GiveStartingItems(player); });
            }

            covalence.Server.Broadcast("Preparation phase has started!");
            SaveFile();
        }

        private void StartRaidingStage()
        {
            int remaning = GetRemaningToolCupboards();
            if (remaning <= 1)
            {
                covalence.Server.Broadcast("No TCs built! Ending game.");
                StartGameOverStage();
                return;
            }

            DateTime now = DateTime.UtcNow;
            matchData.CurrentStage = 2;
            matchData.CurrentStageName = "Raiding";
            matchData.StageEndTime = null;
            matchData.StageStartTime = DateTime.UtcNow.ToString();
            matchData.ResetTeamTickets();
            RemoveArenaWalls();
            votes.Clear();

            covalence.Server.Broadcast("Raiding phase has started!");
            SaveFile();
        }

        private void StartGameOverStage()
        {
            DateTime now = DateTime.UtcNow;
            matchData.CurrentStage = 3;
            matchData.CurrentStageName = "Game Over";
            matchData.StageEndTime = null;
            matchData.StageStartTime = DateTime.UtcNow.ToString();
            matchData.GameOver = true;
            matchData.ResetTeamTickets();
            RemoveArenaWalls();
            votes.Clear();

            SaveFile();
        }

        #endregion

        public void RemoveCupboards()
        {
            foreach (Team team in matchData.Teams.Values)
            {
                team.TeamCupboard = null;
                team.TeamCupboardMarker = null;
            }

            foreach (BuildingPrivlidge entity in BaseNetworkable.serverEntities
                .Where(e => e?.name != null && e.name.Contains("cupboard.tool")).OfType<BuildingPrivlidge>().ToList())
            {
                entity.Kill();
            }

            RemoveMapMarkers();
        }

        public void RemoveTeamCupboard(Team team)
        {
            if (team.TeamCupboard == null) return;

            foreach (BuildingPrivlidge entity in BaseNetworkable.serverEntities
                .Where(e => e?.name != null && e.name.Contains("cupboard.tool")).OfType<BuildingPrivlidge>().ToList())
            {
                if (team.TeamCupboard == entity.ToString())
                {
                    entity.Kill();
                    team.TeamCupboard = null;
                }
            }
        }

        private void UnlockAllBlueprints(BasePlayer player)
        {
            var info = SingletonComponent<ServerMgr>.Instance.persistance.GetPlayerInfo(player.userID);
            info.unlockedItems = ItemManager.bpList.Select(x => x.targetItem.itemid).ToList();
            SingletonComponent<ServerMgr>.Instance.persistance.SetPlayerInfo(player.userID, info);
            player.SendNetworkUpdate();
        }

        bool BuildingProtected()
        {
            if (matchData.CurrentStage == 2) return false;
            return true;
        }

        private void SendDelayedMessage(BasePlayer player, string message)
        {
            if (player == null) return;
            if (cooldown.Contains(player)) return;
            cooldown.Add(player);

            timer.Once(1.0f, () =>
            {
                if (cooldown.Contains(player)) cooldown.Remove(player);
                SendReply(player, message);
            });
        }

        private void MsgRaidingNotAllowed(BasePlayer attacker)
        {
            SendDelayedMessage(attacker, "Raiding damage is disabled until the Raiding stage.");
        }

        private void MsgGreatWall(BasePlayer attacker)
        {
            SendDelayedMessage(attacker, "Not allowed to damage the great wall.");
        }

        private void SaveFile() => Interface.Oxide.DataFileSystem.WriteObject(this.Name, matchData);

        private void CreateMarker(Team team, BaseEntity entity)
        {
            DestroyMarker(team);

            Vector3 pos = entity.transform.position;
            MapMarkerGenericRadius marker =
                GameManager.server.CreateEntity(PrefabMapmarker, pos) as MapMarkerGenericRadius;
            if (marker == null) return;
            marker.alpha = 1.0f;

            marker.color1 = teamColors[team.Id];
            marker.color2 = Color.black;
            marker.radius = 0.1f;

            marker.Spawn();
            marker.SendUpdate();

            team.TeamCupboardMarker = marker.ToString();
        }

        private void DestroyMarker(Team team)
        {
            if (team.TeamCupboardMarker == null) return;

            Puts(string.Format("Destroying marker for team {0}", team.Name));

            foreach (MapMarkerGenericRadius marker in BaseNetworkable.serverEntities.Where(e => e?.name != null && e.name.Contains(PrefabMapmarker)).OfType<MapMarkerGenericRadius>().ToList())
            {
                if (team.TeamCupboardMarker == marker.ToString())
                {
                    marker.Kill();
                    marker.SendUpdate();
                    team.TeamCupboardMarker = null;
                }
            }
        }

        public void RemoveMapMarkers()
        {
            foreach (MapMarkerGenericRadius marker in BaseNetworkable.serverEntities
                .Where(e => e?.name != null && e.name.Contains(PrefabMapmarker)).OfType<MapMarkerGenericRadius>().ToList())
            {
                marker.Kill();
                marker.SendUpdate();
            }
        }

        private object CreateArenaWalls()
        {
            RemoveArenaWalls();

            int spawned = 0;
            float gap = 6.25f;
            int stacks = 35;

            float maxX = TerrainMeta.Size.x * 2;
            float maxZ = TerrainMeta.Size.z * 2;

            Quaternion xRotation = Quaternion.Euler(0, 0, 0);
            Quaternion yRotation = Quaternion.Euler(0, -90, 0);

            for (float i = 1; i < stacks; i++)
            {
                float x = -50.0f;
                float z = 0.0f;

                float newY = -50.0f + (7.0f * i);
                float xPos = -(TerrainMeta.Size.x * 2);
                float zPos = -(TerrainMeta.Size.z * 2);

                while (maxX > xPos)
                {
                    Vector3 position = new Vector3(xPos, newY, z);

                    if (CreateWallEntry(position, xRotation)) spawned++;
                    xPos += gap;
                }

                while (maxZ > zPos)
                {
                    Vector3 position = new Vector3(x, newY, zPos);

                    if (CreateWallEntry(position, yRotation)) spawned++;
                    zPos += gap;
                }
            }

            Puts(string.Format("Spawned {0} Wall entries.", spawned));

            return true;
        }

        private bool CreateWallEntry(Vector3 position, Quaternion rotation)
        {
            ulong ownerId = 1;
            SimpleBuildingBlock entity =
                GameManager.server.CreateEntity(PrefabHighExternalStoneWall, position, rotation, false) as
                    SimpleBuildingBlock;

            if (entity != null)
            {
                entity.OwnerID = ownerId;
                entity.transform.LookAt(position, Vector3.up);
                entity.Spawn();
                entity.gameObject.SetActive(true);
                return true;
            }

            return false;
        }

        private int RemoveArenaWalls()
        {
            int removed = 0;
            foreach (var entity in BaseNetworkable.serverEntities
                .Where(e => e?.name != null && e.name.Contains("wall.external.high")).OfType<BaseEntity>().ToList())
            {
                if (entity.OwnerID == 1)
                {
                    entity.Kill();
                    removed++;
                }
            }

            return removed;
        }

        private void ClearInventory(BasePlayer player)
        {
            Puts(string.Format("Removing Player {0} inventory items.", player.displayName));

            PlayerInventory inventory = player.inventory;
            if (inventory != null)
            {
                inventory.DoDestroy();
                inventory.ServerInit(player);
            }
        }

        private void GiveStartingItems(BasePlayer player)
        {
            foreach (string itemName in startItems)
            {
                Item item = ItemManager.CreateByName(itemName, 1);
                player.GiveItem(item);
            }
        }

        #region Hooks

        private void OnServerInitialized()
        {
            Puts("OnServerInitialized called...");
            initialized = true;
            HackableLockedCrate.requiredHackSeconds = 60f;
            HackableLockedCrate.decaySeconds = 7200f;
            SetBuildTimes();
            SetStackSizes();
            var time = UnityEngine.Object.FindObjectOfType<TOD_Time>();
            time.ProgressTime = false;
            CreateTeamSpawnPoints();
            CreateInitialSpawnPoints();
        }

        private object CanUseLockedEntity(BasePlayer player, BaseLock door)
        {
            if (door.HasLockPermission(player))
            {
                return true;
            }

            Team team = GetPlayerTeam(player);
            foreach (ulong playerId in team.Players)
            {
                if (door.HasLockPermission(BasePlayer.FindByID(playerId)))
                {
                    return true;
                }
            }

            return null;
        }

        private void OnPlayerConnected(BasePlayer player)
        {
            if (player == null) return;
            UnlockAllBlueprints(player);
            players[player.userID] = player;

            if (GetPlayerTeam(player) == null)
            {
                ShowTeamUi(player);
            }
        }

        object OnPlayerRespawn(BasePlayer player)
        {
            if (player == null) return null;

            var randomPosition = UnityEngine.Random.Range(0, initialStartingPositions.Count);
            Vector3 position = initialStartingPositions[randomPosition];

            if (matchData.CurrentStage > 0)
            {
                if (!deadPlayers.Contains(player))
                {
                    deadPlayers.Add(player);
                }
            }

            NextTick(() =>
            {
                ClearInventory(player);
                player.health = 100;
                player.metabolism.calories.Increase(500);
                player.metabolism.hydration.Increase(250);
            });

            return new BasePlayer.SpawnPoint()
                { pos = GetGroundPosition((Vector3)position), rot = new Quaternion(0, 0, 0, 1) };
        }

        void OnEntityDeath(BaseCombatEntity entity, HitInfo info)
        {
            if (matchData.GameOver == true) return;
            if (matchData.CurrentStage != 2) return;
            if (!(entity is BasePlayer)) return;

            BasePlayer player = entity as BasePlayer;
            Team playerTeam = GetPlayerTeam(player);

            if (playerTeam == null) return;
            if (playerTeam.IsOut == true) return;

            playerTeam.TicketsRemaning -= 1;
            if (playerTeam.TicketsRemaning <= 0)
            {
                TeamLost(playerTeam);
            }
        }

        private void OnEntityKill(BaseNetworkable entity)
        {
            if (!entity.ShortPrefabName.Contains("cupboard.tool")) return;

            Team ownerTeam = GetCupboardOwner(entity.ToString());
            if (ownerTeam == null) return;

            ownerTeam.TeamCupboard = null;
            DestroyMarker(ownerTeam);

            if (ownerTeam.IsOut == true) return;

            if (matchData.CurrentStage == 2)
            {
                TeamLost(ownerTeam);
            }
        }

        private void OnEntitySpawned(BaseEntity entity, UnityEngine.GameObject gameObject)
        {
            if (entity.ShortPrefabName.Contains("bed") || entity.ShortPrefabName.Contains("sleep"))
            {
                BasePlayer player = BasePlayer.FindByID(entity.OwnerID);
                if (player == null) return;

                entity.KillMessage();
                SendReply(player, "Not allowed to place a sleeping bag or bed.");
            }
            else if (entity.ShortPrefabName.Contains("cupboard.tool"))
            {
                BasePlayer player = BasePlayer.FindByID(entity.OwnerID);
                if (player == null) return;

                if (matchData.CurrentStage >= 2)
                {
                    entity.KillMessage();
                    SendReply(player, "No longer allowed to place new Cupboards.");
                    return;
                }

                Team team = GetPlayerTeam(player);
                if (team == null)
                {
                    entity.KillMessage();
                    SendReply(player, "Need to be on a team to place a cupboard!");
                }
                else
                {
                    if (team.TeamCupboard == null)
                    {
                        team.TeamCupboard = entity.ToString();
                        SendReply(player, "You have successfully placed your teams cupboard!");
                        CreateMarker(team, entity);
                    }
                    else
                    {
                        entity.KillMessage();
                        SendReply(player, "Your team already has a cupboard!");
                    }
                }
            }
        }

        private void OnUserConnected(IPlayer player)
        {
            if (player == null) return;

            player.Reply("Welcome to Rust Arena.");
        }

        private object OnEntityTakeDamage(BaseCombatEntity entity, HitInfo hitinfo)
        {
            if (hitinfo == null) return null;

            if (matchData.CurrentStage == 0)
            {
                if (hitinfo.damageTypes.GetMajorityDamageType().ToString().Contains("Suicide")) return null;

                hitinfo.damageTypes = new DamageTypeList();
                hitinfo.DoHitEffects = false;
                hitinfo.HitMaterial = 0;
                hitinfo.damageTypes.ScaleAll(0);
                return true;
            }

            BasePlayer attacker = hitinfo.InitiatorPlayer;

            if (entity?.OwnerID == 1)
            {
                hitinfo.damageTypes = new DamageTypeList();
                hitinfo.DoHitEffects = false;
                hitinfo.HitMaterial = 0;
                hitinfo.damageTypes.ScaleAll(0);
                MsgGreatWall(attacker);
                return true;
            }

            if (entity == null || entity.OwnerID == hitinfo?.InitiatorPlayer?.userID) return null;

            if (entity?.OwnerID == 0) return null;
            if (!(entity is BuildingBlock || entity is Door || entity.PrefabName.Contains("deployable"))) return null;

            if (BuildingProtected())
            {
                hitinfo.damageTypes = new DamageTypeList();
                hitinfo.DoHitEffects = false;
                hitinfo.HitMaterial = 0;
                MsgRaidingNotAllowed(attacker);
                return true;
            }

            return null;
        }

        private int BonusGatherRate()
        {
            if (matchData.CurrentStage == 2)
            {
                var progress = DateTime.UtcNow.Subtract(Convert.ToDateTime(matchData.StageStartTime));
                int bonus = Convert.ToInt32(Math.Min(progress.TotalMinutes / 10, GatherRate * 4));
                if (bonus > 0)
                    return GatherRate + bonus;
            }

            return GatherRate;
        }

        private void OnDispenserGather(ResourceDispenser dispenser, BaseEntity entity, Item item)
        {
            if (!entity.ToPlayer())
            {
                return;
            }

            int gatherRate = BonusGatherRate();
            var amount = item.amount;
            item.amount = item.amount * gatherRate;
            try
            {
                dispenser.containedItems.Single(x => x.itemid == item.info.itemid).amount +=
                    amount - item.amount / gatherRate;

                if (dispenser.containedItems.Single(x => x.itemid == item.info.itemid).amount < 0)
                {
                    item.amount += (int)dispenser.containedItems.Single(x => x.itemid == item.info.itemid).amount;
                }
            }
            catch
            {
            }
        }

        private void OnDispenserBonus(ResourceDispenser dispenser, BaseEntity entity, Item item)
        {
            OnDispenserGather(dispenser, entity, item);
        }

        private void OnCollectiblePickup(Item item, BasePlayer player)
        {
            if (notBoostedItems.Contains(item.info.name)) return;
            item.amount = (int)(item.amount * PickupRate);
        }

        private object OnLootSpawn(LootContainer container)
        {
            if (container?.inventory?.itemList == null) return null;
            while (container.inventory.itemList.Count > 0)
            {
                var item = container.inventory.itemList[0];
                item.RemoveFromContainer();
                item.Remove(0f);
            }

            if (container is HackableLockedCrate)
            {
                foreach (KeyValuePair<string, int> i in hackableLootItems)
                {
                    Item item = ItemManager.CreateByName(i.Key, i.Value);
                    item.MoveToContainer(container.inventory, -1, false);
                }
            }
            else
            {
                container.PopulateLoot();
                foreach (Item i in container.inventory.itemList)
                {
                    if (i.IsBlueprint())
                    {
                        i.amount = 0;
                    }
                    else if (!i.hasCondition)
                    {
                        if (notBoostedItems.Contains(i.info.name)) continue;
                        if (boostedItems.Contains(i.info.name))
                        {
                            i.amount *= BoostedLootRate;
                        }
                        else
                        {
                            i.amount *= LootRate;
                        }
                    }
                }
            }

            return container;
        }

        #endregion

        #region UserInterface

        class PanelRect
        {
            public float left, bottom, right, top;
            public PanelRect()
            {
                left = 0f;
                bottom = 0f;
                right = 1f;
                top = 1f;
            }
            public PanelRect(float left, float bottom, float right, float top)
            {
                this.left = left;
                this.bottom = bottom;
                this.right = right;
                this.top = top;
            }

            public string AnchorMin
            {
                get { return left + " " + bottom; }
            }
            public string AnchorMax
            {
                get { return right + " " + top; }
            }

            public PanelRect RelativeTo(PanelRect other)
            {
                left = other.left + (other.right - other.left) * left;
                right = other.left + (other.right - other.left) * right;
                top = other.bottom + (other.top - other.bottom) * top;
                bottom = other.bottom + (other.top - other.bottom) * bottom;
                return this;
            }

            public PanelRect Copy()
            {
                return new PanelRect(left, bottom, right, top);
            }
        }

        class PanelInfo
        {
            public PanelRect rect;
            public string backgroundColor;

            public PanelInfo(PanelRect rect, string color)
            {
                this.rect = rect;
                this.backgroundColor = color;
            }
        }

        private CuiElementContainer CreateElementContainer(string panelName, PanelInfo panelInfo, bool cursor = false)
        {
            var newElement = new CuiElementContainer()
            {
                {
                    new CuiPanel
                    {
                        Image = {Color = panelInfo.backgroundColor},
                        RectTransform = { AnchorMin = panelInfo.rect.AnchorMin, AnchorMax = panelInfo.rect.AnchorMax },
                        CursorEnabled = cursor
                    },
                    new CuiElement().Parent,
                    panelName
                }
            };
            return newElement;
        }

        private void CreatePanel(ref CuiElementContainer container, string panel, PanelInfo panelInfo, bool cursor = false)
        {
            container.Add(new CuiPanel
            {
                Image = { Color = panelInfo.backgroundColor },
                RectTransform = { AnchorMin = panelInfo.rect.AnchorMin, AnchorMax = panelInfo.rect.AnchorMax },
                CursorEnabled = cursor
            }, panel);
        }

        private void CreateLabel(ref CuiElementContainer container, string panel, PanelRect rect, string color, string text, int size = 30, TextAnchor align = TextAnchor.MiddleCenter)
        {
            var label = new CuiLabel
            {
                Text = { Color = color, FontSize = size, Align = align, FadeIn = 0.0f, Text = text },
                RectTransform = { AnchorMin = rect.AnchorMin, AnchorMax = rect.AnchorMax }
            };

            container.Add(label, panel);
        }

        private void CreateMenuButton(ref CuiElementContainer container, string panel, string command, string text, PanelRect rect, int fontSize = 22, string color = "1 1 1 0.2", string textcolor = "1 1 1 1", TextAnchor align = TextAnchor.MiddleCenter)
        {
            container.Add(new CuiButton
            {
                Button = { Command = command, Color = color },
                RectTransform = { AnchorMin = rect.AnchorMin, AnchorMax = rect.AnchorMax },
                Text = { Text = text, FontSize = fontSize, Align = align, Color = textcolor }
            }, panel);
        }

        private void ShowTeamUi(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, TeamUi);

            var elements = CreateElementContainer(TeamUi, new PanelInfo(new PanelRect(0.3f, 0.3f, 0.7f, 0.9f), "0.1 0.1 0.1 0.6"), true);
            CreatePanel(ref elements, TeamUi, new PanelInfo(new PanelRect(0f, 0f, 0.001f, 1f), "1 1 1 0.7"));
            CreatePanel(ref elements, TeamUi, new PanelInfo(new PanelRect(0.999f, 0f, 1f, 1f), "1 1 1 0.7"));
            CreatePanel(ref elements, TeamUi, new PanelInfo(new PanelRect(0f, 0.999f, 1f, 1f), "1 1 1 0.7"));
            CreatePanel(ref elements, TeamUi, new PanelInfo(new PanelRect(0f, 0f, 1f, 0.001f), "1 1 1 0.7"));

            CreatePanel(ref elements, TeamUi, new PanelInfo(new PanelRect(0.05f, 0.85f, 0.95f, 0.95f), "0.1 0.1 0.1 0.9"));
            CreatePanel(ref elements, TeamUi, new PanelInfo(new PanelRect(0.05f, 0.849f, 0.95f, 0.85f), "1 1 1 1"));
            CreateLabel(ref elements, TeamUi, new PanelRect(0.05f, 0.85f, 0.95f, 0.95f), "1 1 1 1", "SELECT TEAM");
            CreatePanel(ref elements, TeamUi, new PanelInfo(new PanelRect(0.05f, 0.05f, 0.95f, 0.835f), "0.1 0.1 0.1 0.9"));
            CreatePanel(ref elements, TeamUi, new PanelInfo(new PanelRect(0.05f, 0.049f, 0.95f, 0.05f), "1 1 1 1"));

            foreach (Team team in matchData.Teams.Values)
            {
                float padding = 0.09f;
                float yi = team.Id * padding;

                var buttonRect = new PanelRect(0.06f, 0.755f - yi, 0.29f, 0.825f - yi);
                var separator = new PanelRect(0.06f, 0.754f - yi, 0.29f, 0.755f - yi);
                var infoRect = new PanelRect(0.30f, 0.89f - yi, 0.95f, 0.95f - yi);
                var playerRect = new PanelRect(0.30f, 0.755f - yi, 0.94f, 0.825f - yi);
                var separator2 = new PanelRect(0.30f, 0.754f - yi, 0.94f, 0.755f - yi);
                var infoRect2 = new PanelRect(0.305f, 0.735f - yi, 0.94f, 0.825f - yi);

                CreateMenuButton(ref elements, TeamUi, string.Format("rustarena.join {0}", team.Name), team.Name, buttonRect, textcolor: team.Color);
                CreatePanel(ref elements, TeamUi, new PanelInfo(separator, team.Color));

                List<string> players = new List<string>();
                foreach (var p in team.Players)
                {
                    players.Add(GetPlayerName(p));
                }

                CreatePanel(ref elements, TeamUi, new PanelInfo(playerRect, "1 1 1 0.2"));
                CreatePanel(ref elements, TeamUi, new PanelInfo(separator2, team.Color));
                CreateLabel(ref elements, TeamUi, infoRect2, team.Color, string.Format("{0}", string.Join(", ", players)), size: 12, align: TextAnchor.UpperLeft);
            }

            CuiHelper.AddUi(player, elements);
        }


        private void UpdateStageUi(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, TcUiPanel);
            CuiHelper.DestroyUi(player, TimeUiPanel);
            CuiHelper.DestroyUi(player, StageUiPanel);
            CuiHelper.DestroyUi(player, TeamUiPanel);
            CuiHelper.DestroyUi(player, TicketUiPanel);

            DrawStageUi(player, 0);

            if (matchData.CurrentStage < 2)
                DrawTimeUi(player, 1);
            else if (matchData.CurrentStage >= 2)
                DrawTcUi(player, 1);

            if (GetPlayerTeam(player) != null) 
            {
                DrawTeamUi(player, 3);
                DrawTicketsUi(player, 2);
            }
        }

        private void DrawStageUi(BasePlayer player, int index)
        {
            string text = GetCurrentStage();
            string color = "1 1 1 1";

            if (matchData.CurrentStage == 1) color = "0 0.8 0 1";
            else if (matchData.CurrentStage == 1) color = "1 0.6 0"; 

            DrawInfo(player, index, text, color, StageUiPanel);
        }

        private void DrawTcUi(BasePlayer player, int index)
        {
            int remaning = GetRemaningToolCupboards();
            Team winningTeam = matchData.WinningTeam;

            string text;
            string color = "0 1 0 1";
            
            if (winningTeam != null || remaning == 0)
            {
                text = string.Format("Winner: {0}", winningTeam?.Name ?? "Unknown");
                color = "0 1 0 1";
            }
            else
            {
                text = string.Format("TCs Remaning: {0}", remaning.ToString());
                if (remaning < 4) color = "1 0.6 0";
            }

            DrawInfo(player, index, text, color, TcUiPanel);
        }

        private void DrawTimeUi(BasePlayer player, int index)
        {
            string text = GetTimeLeft().ToString(@"mm\M\ ss\S");
            string color = "1 1 1 1";

            DrawInfo(player, index, text, color, TimeUiPanel);
        }

        private void DrawTeamUi(BasePlayer player, int index)
        {
            string text = string.Format("{0} Team", GetPlayerTeam(player)?.Name ?? "");
            string color = "1 1 1 1";

            DrawInfo(player, index, text, color, TeamUiPanel);
        }

        private void DrawTicketsUi(BasePlayer player, int index)
        {
            string text = string.Format("{0} Tickets", GetPlayerTeam(player)?.TicketsRemaning);
            string color = "1 1 1 1";

            DrawInfo(player, index, text, color, TicketUiPanel);
        }

        private void DrawInfo(BasePlayer player, int index, string text, string color, string panelName)
        {
            float padding = 0.04f;
            float bottom = 0.959f - (index * padding);
            float top = 0.999f - (index * padding);
            PanelRect infoRect = new PanelRect(0f, 0.1f, 0.95f, 0.855f);
            PanelRect separator = new PanelRect(0f, 0.05f, 0.95f, 0.05f);
            PanelInfo panel = new PanelInfo(new PanelRect(0.905f, bottom, 0.999f, top), "0 0 0 0");

            var elements = CreateElementContainer(panelName, panel, false);
            CreatePanel(ref elements, panelName, new PanelInfo(infoRect, "0.1 0.1 0.1 0.9"));
            CreatePanel(ref elements, panelName, new PanelInfo(separator, "1 1 1 1"));
            CreateLabel(ref elements, panelName, new PanelRect(0f, 0f, 0.94f, 0.95f), color, text, size: 16);

            CuiHelper.AddUi(player, elements);
        }

        #endregion
        }
}

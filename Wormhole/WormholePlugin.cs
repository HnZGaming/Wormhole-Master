﻿using NLog;
using Sandbox.Game;
using Sandbox.Game.World;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.ModAPI;
using Sandbox.Common.ObjectBuilders;
using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Controls;
using System.Linq;
using System.Threading.Tasks;
using Torch;
using Torch.API;
using Torch.API.Plugins;
using Torch.Mod;
using Torch.Mod.Messages;
using VRageMath;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.ObjectBuilders;

namespace Wormhole
{
    public class WormholePlugin : TorchPluginBase, IWpfPlugin
    {
        public static WormholePlugin Instance { get; private set; }
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();

        private GUI _control;
        public UserControl GetControl() => _control ?? (_control = new GUI(this));

        private Persistent<Config> _config;
        public Config Config => _config?.Data;
        public void Save() => _config.Save();

        private int tick = 0;

        // The actual task of saving the game on exit or enter
        private Task saveOnExitTask;
        private Task saveOnEnterTask;

        public string admingatesfolder = "admingates";
        public string admingatesconfirmsentfolder = "admingatesconfirmsent";
        public string admingatesconfirmreceivedfolder = "admingatesconfirmreceived";
        public string admingatesconfig = "admingatesconfig";
        public override void Init(ITorchBase torch)
        {
            base.Init(torch);
            Instance = this;
            SetupConfig();
        }

        private void SetupConfig()
        {
            var configFile = Path.Combine(StoragePath, "Wormhole.cfg");
            try
            {
                _config = Persistent<Config>.Load(configFile);
            }
            catch (Exception e)
            {
                Log.Warn(e);
            }

            if (_config?.Data == null)
            {

                Log.Info("Create Default Config, because none was found!");

                _config = new Persistent<Config>(configFile, new Config());
                _config.Save();
            }
            //Utilities.WormholeGateConfigUpdate();
        }

        public override void Update()
        {
            base.Update();
            if (++tick == Config.Tick)
            {
                tick = 0;
                try
                {
                    foreach (WormholeGate wormhole in Config.WormholeGates)
                    {
                        Vector3D gatepoint = new Vector3D(wormhole.X, wormhole.Y, wormhole.Z);
                        BoundingSphereD gate = new BoundingSphereD(gatepoint, Config.RadiusGate);
                        Wormholetransferout(wormhole.SendTo, gatepoint, gate);
                        Wormholetransferin(wormhole.Name.Trim(), gatepoint, gate);
                    }
                }
                catch (Exception e)
                {
                    Log.Error(e, "Could not run Wormhole");
                }
                try
                {
                    //check transfer status
                    DirectoryInfo gridDir = new DirectoryInfo(Config.Folder + "/" + admingatesfolder);
                    DirectoryInfo gridDirsent = new DirectoryInfo(Config.Folder + "/" + admingatesconfirmsentfolder);
                    DirectoryInfo gridDirreceived = new DirectoryInfo(Config.Folder + "/" + admingatesconfirmreceivedfolder);
                    foreach (var file in gridDirreceived.GetFiles())
                    {
                        //if all other files have been correctly removed then remove safety to stop duplication
                        if (!File.Exists(gridDirsent.FullName + "/" + file.Name) && !File.Exists(gridDir.FullName + "/" + file.Name))
                        {
                            file.Delete();
                        }
                    }
                }
                catch
                {
                    //no issue file might in deletion process
                }
            }
        }


        public void Wormholetransferout(string sendto, Vector3D gatepoint, BoundingSphereD gate)
        {
            foreach (var grid in MyAPIGateway.Entities.GetTopMostEntitiesInSphere(ref gate).OfType<IMyCubeGrid>())
            {
                var gts = MyAPIGateway.TerminalActionsHelper.GetTerminalSystemForGrid(grid);

                if (gts != null)
                {
                    var WormholeDrives = new List<IMyJumpDrive>();
                    gts.GetBlocksOfType(WormholeDrives);

                    foreach (var WormholeDrive in WormholeDrives)
                    {
                        WormholeTransferOutFile(sendto, grid, WormholeDrive, gatepoint, WormholeDrives);
                    }
                }
            }
        }

        private void WormholeTransferOutFile(string sendto, IMyCubeGrid grid, IMyJumpDrive WormholeDrive, Vector3D gatepoint, List<IMyJumpDrive> WormholeDrives)
        {
            if (!Config.JumpDriveSubid.Split(',').Any(s => s.Trim() == WormholeDrive.BlockDefinition.SubtypeId) && !Config.WorkWithAllJD)
                return;

            Request request = default;
            try
            {
                request = MyAPIGateway.Utilities.SerializeFromXML<Request>(WormholeDrive.CustomData);
            }
            catch { }

            string pickeddestination = default;

            if (request != null)
            {
                if (request.PluginRequest)
                {
                    if (request.Destination != null)
                    {
                        if (sendto.Split(',').Any(s => s.Trim() == request.Destination.Trim()))
                        {
                            pickeddestination = request.Destination.Trim();
                        }
                    }
                    Request reply = new Request
                    {
                        PluginRequest = false,
                        Destination = null,
                        Destinations = sendto.Split(',').Select(s => s.Trim()).ToArray()
                    };
                    WormholeDrive.CustomData = MyAPIGateway.Utilities.SerializeToXML<Request>(reply);
                }
            }
            else
            {
                Request reply = new Request
                {
                    PluginRequest = false,
                    Destination = null,
                    Destinations = sendto.Split(',').Select(s => s.Trim()).ToArray()
                };
                WormholeDrive.CustomData = MyAPIGateway.Utilities.SerializeToXML<Request>(reply);
            }

            if (Config.AutoSend && sendto.Split(',').Length == 1)
                pickeddestination = sendto.Split(',')[0].Trim();

            if (pickeddestination == null)
                return;

            if (!WormholeDrive.IsWorking || WormholeDrive.CurrentStoredPower != WormholeDrive.MaxStoredPower)
                return;

            var playerInCharge = MyAPIGateway.Players.GetPlayerControllingEntity(grid);
            if (playerInCharge == null || !Utilities.HasRightToMove(playerInCharge, grid as MyCubeGrid))
                return;

            WormholeDrive.CurrentStoredPower = 0;
            foreach (var DisablingWormholeDrive in WormholeDrives)
            {
                if (Config.JumpDriveSubid.Split(',').Any(s => s.Trim() == DisablingWormholeDrive.BlockDefinition.SubtypeId) || Config.WorkWithAllJD)
                {
                    DisablingWormholeDrive.Enabled = false;
                }
            }
            List<MyCubeGrid> grids = Utilities.FindGridList(grid.EntityId.ToString(), playerInCharge as MyCharacter, Config.IncludeConnectedGrids);

            if (grids == null)
                return;

            if (grids.Count == 0)
                return;

            MyVisualScriptLogicProvider.CreateLightning(gatepoint);

            //NEED TO DROP ENEMY GRIDS
            if (Config.WormholeGates.Any(s => s.Name.Trim() == pickeddestination.Split(':')[0]))
            {
                foreach (WormholeGate internalwormhole in Config.WormholeGates)
                {
                    if (internalwormhole.Name.Trim() == pickeddestination.Split(':')[0].Trim())
                    {
                        var box = WormholeDrive.GetTopMostParent().WorldAABB;
                        var togatepoint = new Vector3D(internalwormhole.X, internalwormhole.Y, internalwormhole.Z);
                        var togate = new BoundingSphereD(togatepoint, Config.RadiusGate);
                        Utilities.UpdateGridsPositionAndStopLive(WormholeDrive.GetTopMostParent(), Utilities.FindFreePos(togate, (float)(Vector3D.Distance(box.Center, box.Max) + 50)));
                        MyVisualScriptLogicProvider.CreateLightning(togatepoint);
                    }
                }
            }
            else
            {
                var destination = pickeddestination.Split(':');

                if (3 != destination.Length)
                {
                    throw new ArgumentException("failed parsing destination '" + destination + "'");
                }

                var transferFileInfo = new Utilities.TransferFileInfo
                {
                    destinationWormhole = destination[0],
                    steamUserId = playerInCharge.SteamUserId,
                    playerName = playerInCharge.DisplayName,
                    gridName = grid.DisplayName,
                    time = DateTime.Now
                };

                Log.Info("creating filetransfer:" + transferFileInfo.ToString());
                var filename = transferFileInfo.createFileName();

                List<MyObjectBuilder_CubeGrid> objectBuilders = new List<MyObjectBuilder_CubeGrid>();
                foreach (MyCubeGrid mygrid in grids)
                {
                    if (!(mygrid.GetObjectBuilder(true) is MyObjectBuilder_CubeGrid objectBuilder))
                    {
                        throw new ArgumentException(mygrid + " has a ObjectBuilder thats not for a CubeGrid");
                    }
                    objectBuilders.Add(objectBuilder);
                }
                MyObjectBuilder_ShipBlueprintDefinition definition = MyObjectBuilderSerializer.CreateNewObject<MyObjectBuilder_ShipBlueprintDefinition>();

                definition.Id = new MyDefinitionId(new MyObjectBuilderType(typeof(MyObjectBuilder_ShipBlueprintDefinition)), filename);

                definition.CubeGrids = objectBuilders.Select(x => (MyObjectBuilder_CubeGrid)x.Clone()).ToArray();

                HashSet<ulong> sittingPlayerSteamIds = new HashSet<ulong>();
                foreach (MyObjectBuilder_CubeGrid cubeGrid in definition.CubeGrids)
                {
                    foreach (MyObjectBuilder_CubeBlock cubeBlock in cubeGrid.CubeBlocks)
                    {
                        cubeBlock.Owner = 0L;
                        cubeBlock.BuiltBy = 0L;

                        if (!Config.ExportProjectorBlueprints)
                        {
                            if (cubeBlock is MyObjectBuilder_ProjectorBase projector)
                            {
                                projector.ProjectedGrids = null;
                            }
                        }
                        if (cubeBlock is MyObjectBuilder_Cockpit cockpit)
                        {
                            if (cockpit.Pilot != null)
                            {
                                var playerSteamId = cockpit.Pilot.PlayerSteamId;
                                sittingPlayerSteamIds.Add(playerSteamId);
                                ModCommunication.SendMessageTo(new JoinServerMessage(destination[1] + ":" + destination[2]), playerSteamId);
                            }
                        }
                    }
                }

                MyObjectBuilder_Definitions builderDefinition = MyObjectBuilderSerializer.CreateNewObject<MyObjectBuilder_Definitions>();
                builderDefinition.ShipBlueprints = new[] { definition };
                foreach (var playerSteamId in sittingPlayerSteamIds)
                {
                    KillCharacter(playerSteamId);
                }
                if (MyObjectBuilderSerializer.SerializeXML(Utilities.CreateBlueprintPath(Path.Combine(Config.Folder, admingatesfolder), filename), false, builderDefinition))
                {
                    // Saves the game if enabled in config.
                    if (Config.SaveOnExit)
                    {
                        grids.ForEach(b => b.Close());
                        // (re)Starts the task if it has never been started o´r is done
                        if ((saveOnExitTask is null) || saveOnExitTask.IsCompleted)
                            saveOnExitTask = Torch.Save();
                    }
                    else
                    {
                        grids.ForEach(b => b.Close());
                    }
                    DirectoryInfo gridDirsent = new DirectoryInfo(Config.Folder + "/" + admingatesconfirmsentfolder);
                    //creates just in case fir send
                    gridDirsent.Create();
                    File.Create(Utilities.CreateBlueprintPath(gridDirsent.FullName, filename));
                }
            }
        }
        public void Wormholetransferin(string wormholeName, Vector3D gatepoint, BoundingSphereD gate)
        {
            DirectoryInfo gridDir = new DirectoryInfo(Config.Folder + "/" + admingatesfolder);
            DirectoryInfo gridDirsent = new DirectoryInfo(Config.Folder + "/" + admingatesconfirmsentfolder);
            DirectoryInfo gridDirreceived = new DirectoryInfo(Config.Folder + "/" + admingatesconfirmreceivedfolder);
            gridDirreceived.Create();

            if (!gridDir.Exists || !gridDirsent.Exists)
                return;

            var changes = false;

            foreach (var file in gridDir.GetFiles().Where(s => s.Name.Split('_')[0] == wormholeName))
            {
                //if file not null if file exists if file is done being sent and if file hasnt been received before
                if (file != null && File.Exists(file.FullName) && File.Exists(gridDirsent.FullName + "/" + file.Name) && !File.Exists(gridDirreceived.FullName + "/" + file.Name))
                {
                    Log.Info("here 2");
                    var fileTransferInfo = Utilities.TransferFileInfo.parseFileName(file.Name);
                    if (fileTransferInfo.HasValue)
                    {
                        if (wormholeName == fileTransferInfo.Value.destinationWormhole)
                        {
                            WormholeTransferInFile(file, fileTransferInfo.Value, gatepoint, gate);
                            changes = true;
                            File.Delete(gridDirsent.FullName + "/" + file.Name);
                            File.Create(gridDirreceived.FullName + "/" + file.Name);
                        }
                    }
                }
            }

            // Saves game on enter if enabled in config.
            if (changes && Config.SaveOnEnter)
            {

                // (re)Starts the task if it has never been started o´r is done
                if ((saveOnEnterTask is null) || saveOnEnterTask.IsCompleted)
                    saveOnEnterTask = Torch.Save();
            }
        }

        private void WormholeTransferInFile(FileInfo fileInfo, Utilities.TransferFileInfo fileTransferInfo, Vector3D gatePosition, BoundingSphereD gate)
        {
            Log.Info("processing filetransfer:" + fileTransferInfo.createLogString());

            var playerid = MySession.Static.Players.TryGetIdentityId(fileTransferInfo.steamUserId); // defaults to 0
            if (playerid <= 0)
            {
                Log.Error("couldn't find player with steam id: " + fileTransferInfo.steamUserId);
                return;
            }

            if (!MyObjectBuilderSerializer.DeserializeXML(fileInfo.FullName, out MyObjectBuilder_Definitions myObjectBuilder_Definitions))
            {
                Log.Error("error deserializing xml: " + fileInfo.FullName);
                return;
            }

            var shipBlueprints = myObjectBuilder_Definitions.ShipBlueprints;
            if (shipBlueprints == null)
            {
                Log.Error("can't find any blueprints in xml: " + fileInfo.FullName);
                return;
            }

            foreach (var shipBlueprint in shipBlueprints)
            {
                var grids = shipBlueprint.CubeGrids;
                if (grids == null || grids.Length == 0)
                    continue;

                var pos = Utilities.FindFreePos(gate, Utilities.FindGridsRadius(grids));
                if (pos == null)
                {
                    Log.Warn("no free space available for grid '" + shipBlueprint.DisplayName + "' at wormhole '" + fileTransferInfo.destinationWormhole + "'");
                    continue;
                }

                if (Utilities.UpdateGridsPositionAndStop(grids, pos))
                {
                    foreach (var mygrid in grids)
                    {
                        // takeover ownership
                        foreach (MyObjectBuilder_CubeBlock block in mygrid.CubeBlocks)
                        {
                            block.BuiltBy = playerid;
                            block.Owner = playerid;
                        }

                        foreach (MyObjectBuilder_Cockpit cockpit in mygrid.CubeBlocks.Where(block => block is MyObjectBuilder_Cockpit))
                        {
                            if (cockpit.Pilot == null || !Config.PlayerRespawn)
                            {
                                cockpit.Pilot = null;
                                continue;
                            }

                            var pilotSteamId = cockpit.Pilot.PlayerSteamId;
                            var pilotIdentityId = MyAPIGateway.Multiplayer.Players.TryGetIdentityId(pilotSteamId);
                            if (pilotIdentityId == -1)
                            {
                                Log.Info("cannot find player, removing character from cockpit, steamid: " + pilotSteamId);
                                cockpit.Pilot = null;
                                continue;
                            }
                            cockpit.Pilot.OwningPlayerIdentityId = pilotIdentityId;

                            var pilotIdentity = MySession.Static.Players.TryGetIdentity(pilotIdentityId);
                            if (pilotIdentity.Character != null)
                            {
                                // if there is a character, kill it
                                if (Config.ThisIp != null && Config.ThisIp != "")
                                {
                                    ModCommunication.SendMessageTo(new JoinServerMessage(Config.ThisIp), pilotSteamId);
                                }
                                KillCharacter(pilotSteamId);
                            }
                            pilotIdentity.PerformFirstSpawn();
                            pilotIdentity.SavedCharacters.Clear();
                            pilotIdentity.SavedCharacters.Add(cockpit.Pilot.EntityId);
                            MyAPIGateway.Multiplayer.Players.SetControlledEntity(pilotSteamId, cockpit.Pilot as VRage.ModAPI.IMyEntity);
                        }
                    }
                }

                List<MyObjectBuilder_EntityBase> objectBuilderList = new List<MyObjectBuilder_EntityBase>(grids.ToList());
                MyEntities.RemapObjectBuilderCollection(objectBuilderList);
                if (objectBuilderList.Count > 1)
                {
                    if (MyEntities.Load(objectBuilderList, out _))
                    {
                        fileInfo.Delete();
                    }
                }
                else
                {
                    foreach (var ob in objectBuilderList)
                    {
                        if (MyEntities.CreateFromObjectBuilderParallel(ob, true) != null)
                        {
                            fileInfo.Delete();
                        }
                    }
                }

                MyVisualScriptLogicProvider.CreateLightning(gatePosition);
            }
        }
        private static void KillCharacter(ulong steamId)
        {
            Log.Info("killing character, steamid: " + steamId);

            var player = MySession.Static.Players.TryGetPlayerBySteamId(steamId);
            if (player != null)
            {
                var playerIdentity = player.Identity;
                playerIdentity.Character.EnableBag(false);
                MyVisualScriptLogicProvider.SetPlayersHealth(playerIdentity.IdentityId, 0);
                playerIdentity.Character.Close();
            }
        }
    }
}

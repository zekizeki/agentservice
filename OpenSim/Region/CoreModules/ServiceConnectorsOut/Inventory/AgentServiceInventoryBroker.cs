/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using log4net;
using Nini.Config;
using System;
using System.Collections.Generic;
using System.Reflection;
using OpenSim.Framework;
using OpenSim.Framework.Communications.Cache;
using OpenSim.Server.Base;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;
using OpenSim.Services.Connectors;
using OpenMetaverse;

namespace OpenSim.Region.CoreModules.ServiceConnectorsOut.Inventory
{
    public class AgentServiceInventoryBroker : BaseInventoryConnector, ISharedRegionModule, IInventoryService
    {
        private static readonly ILog m_log =
                LogManager.GetLogger(
                MethodBase.GetCurrentMethod().DeclaringType);

        private bool m_Enabled = false;
        private bool m_Initialized = false;
        private Scene m_Scene;
        private IOGPModule m_OGPModule;
        private UserProfileCacheService m_UserProfileService; // This should change to IUserProfileService

        private IInventoryService m_GridService;
        private ISessionAuthInventoryService m_AgentServiceService;

        private string m_LocalGridInventoryURI = string.Empty;
        public Type ReplaceableInterface 
        {
            get { return null; }
        }

        public string Name
        {
            get { return "AgentServiceInventoryBroker"; }
        }

        public void Initialise(IConfigSource source)
        {
            IConfig moduleConfig = source.Configs["Modules"];
            if (moduleConfig != null)
            {
                string name = moduleConfig.GetString("InventoryServices", "");
                if (name == Name)
                {
                    IConfig inventoryConfig = source.Configs["InventoryService"];
                    if (inventoryConfig == null)
                    {
                        m_log.Error("[AgentService INVENTORY BROKER]: InventoryService missing from OpenSim.ini");
                        return;
                    }

                    string localDll = inventoryConfig.GetString("LocalGridInventoryService",
                            String.Empty);
                    string AgentServiceDll = inventoryConfig.GetString("ASInventoryService",
                            String.Empty);

                    if (localDll == String.Empty)
                    {
                        m_log.Error("[AgentService INVENTORY BROKER]: No LocalGridInventoryService named in section InventoryService");
                        //return;
                        throw new Exception("Unable to proceed. Please make sure your ini files in config-include are updated according to .example's");
                    }

                    if (AgentServiceDll == String.Empty)
                    {
                        m_log.Error("[AgentService INVENTORY BROKER]: No AgentServiceInventoryService named in section InventoryService");
                        //return;
                        throw new Exception("Unable to proceed. Please make sure your ini files in config-include are updated according to .example's");
                    }

                    Object[] args = new Object[] { source };
                    
                    // this uses the quick and dirty temporary inventory service
                    m_GridService =
                            ServerUtils.LoadPlugin<IInventoryService>(localDll,
                            args);


                    // this uses the InventoryServiceConnector TODO change this to an AgentService one
                    m_AgentServiceService =
                            ServerUtils.LoadPlugin<ISessionAuthInventoryService>(AgentServiceDll,
                            args);

                    if (m_GridService == null)
                    {
                        m_log.Error("[AgentService INVENTORY BROKER]: Can't load local inventory service");
                        return;
                    }
                    if (m_AgentServiceService == null)
                    {
                        m_log.Error("[AgentService INVENTORY BROKER]: Can't load AgentService inventory service");
                        return;
                    }

                    m_LocalGridInventoryURI = inventoryConfig.GetString("InventoryServerURI", string.Empty);

                    Init(source);

                    m_Enabled = true;
                    m_log.Info("[AgentService INVENTORY BROKER]: AgentService inventory broker enabled");
                }
            }
        }

        public void PostInitialise()
        {
                
        }

        public void Close()
        {
        }

        public void AddRegion(Scene scene)
        {
            if (!m_Enabled)
                return;

            if (!m_Initialized)
            {
                m_Scene = scene;
                // HACK for now. Ugh!
                m_UserProfileService = m_Scene.CommsManager.UserProfileCacheService;
                // ugh!
                m_UserProfileService.SetInventoryService(this);
                scene.CommsManager.UserService.SetInventoryService(this);

                m_Initialized = true;
                
                 
            }

            m_OGPModule = m_Scene.RequestModuleInterface<IOGPModule>();
            
            scene.RegisterModuleInterface<IInventoryService>(this);
            m_cache.AddRegion(scene);
            
           
        }

        public void RemoveRegion(Scene scene)
        {
            if (!m_Enabled)
                return;
            
            m_cache.RemoveRegion(scene);
        }

        public void RegionLoaded(Scene scene)
        {
            if (!m_Enabled)
                return;

            m_log.InfoFormat("[AgentService INVENTORY BROKER]: Enabled AgentService inventory for region {0}", scene.RegionInfo.RegionName);

        }

        #region IInventoryService

        public override bool CreateUserInventory(UUID userID)
        {
            return m_GridService.CreateUserInventory(userID);
        }

        public override List<InventoryFolderBase> GetInventorySkeleton(UUID userId)
        {
            return m_GridService.GetInventorySkeleton(userId);
        }

        public override InventoryCollection GetUserInventory(UUID userID)
        {
            if (IsLocalGridUser(userID))
                return m_GridService.GetUserInventory(userID);
            else
                return null;
        }

        public override void GetUserInventory(UUID userID, InventoryReceiptCallback callback)
        {
            if (IsLocalGridUser(userID))
                m_GridService.GetUserInventory(userID, callback);
            else
            {
                m_log.ErrorFormat("[AgentServiceInventoryBroker]: GetUserInventory ERROR this shouldnt need calling {0}", userID);
                // We don't need this as the viewer gets the inventory direct from the agent service
                //UUID sessionID = GetSessionID(userID);
                //string uri = GetUserInventoryURI(userID) + "/" + userID.ToString();
                //m_AgentServiceService.GetUserInventory(uri, sessionID, callback);
            }
        }

        // Inherited. See base
        //public override InventoryFolderBase GetFolderForType(UUID userID, AssetType type)
        //{
        //    if (IsLocalGridUser(userID))
        //        return m_GridService.GetFolderForType(userID, type);
        //    else
        //    {
        //        UUID sessionID = GetSessionID(userID);
        //        string uri = GetUserInventoryURI(userID) + "/" + userID.ToString();
        //        // !!!!!!
        //        return null;
        //        //return m_AgentServiceService.GetFolderForType(uri, sessionID, type);
        //    }
        //}

        public override InventoryCollection GetFolderContent(UUID userID, UUID folderID)
        {
            if (IsLocalGridUser(userID))
                return m_GridService.GetFolderContent(userID, folderID);
            else
            {
                m_log.ErrorFormat("[AgentServiceInventoryBroker]: GetFolderContent ERROR this shouldnt need calling {0}", userID);
                //UUID sessionID = GetSessionID(userID);
                //string uri = GetUserInventoryURI(userID) + "/" + userID.ToString();
                //return m_AgentServiceService.GetFolderContent(uri, folderID, sessionID);
                return null;
            }
        }

        public override Dictionary<AssetType, InventoryFolderBase> GetSystemFolders(UUID userID)
        {
            if (IsLocalGridUser(userID))
            {
                // This is not pretty, but it will have to do for now
                if (m_GridService is BaseInventoryConnector)
                {
                    m_log.DebugFormat("[AgentService INVENTORY BROKER]: GetSystemsFolders redirected to RemoteInventoryServiceConnector module");
                    return ((BaseInventoryConnector)m_GridService).GetSystemFolders(userID);
                }
                else
                {
                    m_log.DebugFormat("[AgentService INVENTORY BROKER]: GetSystemsFolders redirected to GetSystemFoldersLocal");
                    return GetSystemFoldersLocal(userID);
                }
            }
            else
            {
                m_log.ErrorFormat("[AgentServiceInventoryBroker]: GetSystemFolders ERROR this shouldnt need calling {0} call local service for now", userID);
                return GetSystemFoldersLocal(userID);
                //UUID sessionID = GetSessionID(userID);
                //string uri = GetUserInventoryURI(userID) + "/" + userID.ToString();
                //return m_AgentServiceService.GetSystemFolders(uri, sessionID);
                //return null;
            }
        }

        private Dictionary<AssetType, InventoryFolderBase> GetSystemFoldersLocal(UUID userID)
        {
            InventoryFolderBase root = m_GridService.GetRootFolder(userID);
            if (root != null)
            {
                InventoryCollection content = m_GridService.GetFolderContent(userID, root.ID);
                if (content != null)
                {
                    Dictionary<AssetType, InventoryFolderBase> folders = new Dictionary<AssetType, InventoryFolderBase>();
                    foreach (InventoryFolderBase folder in content.Folders)
                    {
                        //m_log.DebugFormat("[AgentService INVENTORY BROKER]: scanning folder type {0}", (AssetType)folder.Type);
                        if ((folder.Type != (short)AssetType.Folder) && (folder.Type != (short)AssetType.Unknown))
                            folders[(AssetType)folder.Type] = folder;
                    }
                    // Put the root folder there, as type Folder
                    folders[AssetType.Folder] = root;
                    m_log.DebugFormat("[AgentService INVENTORY BROKER]: System folders count for {0}: {1}", userID, folders.Count);
                    return folders;
                }
                m_log.DebugFormat("[AgentService INVENTORY BROKER]: Root folder content not found for {0}", userID);

            }

            m_log.DebugFormat("[AgentService INVENTORY BROKER]: Root folder not found for {0}", userID);

            return new Dictionary<AssetType, InventoryFolderBase>();
        }

        public override List<InventoryItemBase> GetFolderItems(UUID userID, UUID folderID)
        {
            if (IsLocalGridUser(userID))
                return m_GridService.GetFolderItems(userID, folderID);
            else
            {
                m_log.ErrorFormat("[AgentServiceInventoryBroker]: GetFolderItems ERROR this shouldnt need calling {0}", userID);
                //UUID sessionID = GetSessionID(userID);
                //string uri = GetUserInventoryURI(userID) + "/" + userID;
                //return m_AgentServiceService.GetFolderItems(uri, folderID, sessionID);
                return null;
            }
        }

        public override bool AddFolder(InventoryFolderBase folder)
        {
            if (folder == null)
                return false;

            if (IsLocalGridUser(folder.Owner))
                return m_GridService.AddFolder(folder);
            else
            {
                UUID sessionID = GetSessionID(folder.Owner);
                //string uri = GetUserInventoryURI(folder.Owner) + "/" + folder.Owner.ToString();
                string uri = m_OGPModule.GetInventoryFolderCreateCap(folder.Owner);
                return m_AgentServiceService.AddFolder(uri, folder, sessionID);
            }
        }

        public override bool UpdateFolder(InventoryFolderBase folder)
        {
            if (folder == null)
                return false;

            if (IsLocalGridUser(folder.Owner))
                return m_GridService.UpdateFolder(folder);
            else
            {
                UUID sessionID = GetSessionID(folder.Owner);
                //string uri = GetUserInventoryURI(folder.Owner) + "/" + folder.Owner.ToString();
                string uri = m_OGPModule.GetInventoryFolderUpdateCap(folder.Owner);
                return m_AgentServiceService.UpdateFolder(uri, folder, sessionID);
            }
        }

        public override bool DeleteFolders(UUID ownerID, List<UUID> folderIDs)
        {
            if (folderIDs == null)
                return false;
            if (folderIDs.Count == 0)
                return false;

            if (IsLocalGridUser(ownerID))
                return m_GridService.DeleteFolders(ownerID, folderIDs);
            else
            {
                UUID sessionID = GetSessionID(ownerID);
                string uri = GetUserInventoryURI(ownerID) + "/" + ownerID.ToString();
                return m_AgentServiceService.DeleteFolders(uri, folderIDs, sessionID);
            }
        }

        public override bool MoveFolder(InventoryFolderBase folder)
        {
            if (folder == null)
                return false;

            if (IsLocalGridUser(folder.Owner))
                return m_GridService.MoveFolder(folder);
            else
            {
                UUID sessionID = GetSessionID(folder.Owner);
                string uri = GetUserInventoryURI(folder.Owner) + "/" + folder.Owner.ToString();
                return m_AgentServiceService.MoveFolder(uri, folder, sessionID);
            }
        }

        public override bool PurgeFolder(InventoryFolderBase folder)
        {
            if (folder == null)
                return false;

            if (IsLocalGridUser(folder.Owner))
                return m_GridService.PurgeFolder(folder);
            else
            {
                UUID sessionID = GetSessionID(folder.Owner);
                string uri = GetUserInventoryURI(folder.Owner) + "/" + folder.Owner.ToString();
                return m_AgentServiceService.PurgeFolder(uri, folder, sessionID);
            }
        }

        // public bool AddItem(InventoryItemBase item) inherited
        // Uses AddItemPlain

        protected override bool AddItemPlain(InventoryItemBase item)
        {
            if (item == null)
                return false;

            if (IsLocalGridUser(item.Owner))
            {
                return m_GridService.AddItem(item);
            }
            else
            {
                UUID sessionID = GetSessionID(item.Owner);
                //string uri = GetUserInventoryURI(item.Owner) + "/" + item.Owner.ToString();
                string uri = m_OGPModule.GetInventoryCreateCap(folder.Owner);
                return m_AgentServiceService.AddItem(uri, item, sessionID);
            }
        }

        public override bool UpdateItem(InventoryItemBase item)
        {
            if (item == null)
                return false;

            if (IsLocalGridUser(item.Owner))
                return m_GridService.UpdateItem(item);
            else
            {
                UUID sessionID = GetSessionID(item.Owner);
                string uri = GetUserInventoryURI(item.Owner) + "/" + item.Owner.ToString();
                return m_AgentServiceService.UpdateItem(uri, item, sessionID);
            }
        }

        public override bool MoveItems(UUID ownerID, List<InventoryItemBase> items)
        {
            if (items == null)
                return false;
            if (items.Count == 0)
                return true;

            if (IsLocalGridUser(ownerID))
                return m_GridService.MoveItems(ownerID, items);
            else
            {
                UUID sessionID = GetSessionID(ownerID);
                string uri = GetUserInventoryURI(ownerID) + "/" + ownerID.ToString();
                return m_AgentServiceService.MoveItems(uri, items, sessionID);
            }
        }

        public override bool DeleteItems(UUID ownerID, List<UUID> itemIDs)
        {
            m_log.DebugFormat("[AgentService INVENTORY BROKER]: Delete {0} items for user {1}", itemIDs.Count, ownerID);

            if (itemIDs == null)
                return false;
            if (itemIDs.Count == 0)
                return true;

            if (IsLocalGridUser(ownerID))
                return m_GridService.DeleteItems(ownerID, itemIDs);
            else
            {
                UUID sessionID = GetSessionID(ownerID);
                string uri = GetUserInventoryURI(ownerID) + "/" + ownerID.ToString();
                return m_AgentServiceService.DeleteItems(uri, itemIDs, sessionID);
            }
        }

        public override InventoryItemBase GetItem(InventoryItemBase item)
        {
            if (item == null)
                return null;
            m_log.DebugFormat("[AgentService INVENTORY BROKER]: GetItem {0} for user {1}", item.ID, item.Owner);
            if (IsLocalGridUser(item.Owner))
                return m_GridService.GetItem(item);
            else
            {
                UUID sessionID = GetSessionID(item.Owner);
                string uri = GetUserInventoryURI(item.Owner) + "/" + item.Owner.ToString();
                return m_AgentServiceService.QueryItem(uri, item, sessionID);
            }
        }

        public override InventoryFolderBase GetFolder(InventoryFolderBase folder)
        {
            if (folder == null)
                return null;

            if (IsLocalGridUser(folder.Owner))
                return m_GridService.GetFolder(folder);
            else
            {
                // TODO the region needs this to continue with a folder update and move operation.
                m_log.ErrorFormat("[AgentServiceInventoryBroker]: GetFolder ERROR this shouldnt need calling?? {0}", folder);
                //UUID sessionID = GetSessionID(folder.Owner);
                //string uri = GetUserInventoryURI(folder.Owner) + "/" + folder.Owner.ToString();
                //return m_AgentServiceService.QueryFolder(uri, folder, sessionID);
                return null;
            }
        }

        public override bool HasInventoryForUser(UUID userID)
        {
            return false;
        }

        public override List<InventoryItemBase> GetActiveGestures(UUID userId)
        {
            return new List<InventoryItemBase>();
        }

        public override int GetAssetPermissions(UUID userID, UUID assetID)
        {
            if (IsLocalGridUser(userID))
                return m_GridService.GetAssetPermissions(userID, assetID);
            else
            {
                m_log.ErrorFormat("[AgentServiceInventoryBroker]: GetAssetPermissions ERROR this shouldnt need calling?? {0}", userID);
                //UUID sessionID = GetSessionID(userID);
                //string uri = GetUserInventoryURI(userID) + "/" + userID.ToString();
                //return m_AgentServiceService.GetAssetPermissions(uri, assetID, sessionID);
                return 0;
            }
        }

        #endregion

        private UUID GetSessionID(UUID userID)
        {
            CachedUserInfo uinfo = m_UserProfileService.GetUserDetails(userID);
            if (uinfo != null)
                return uinfo.SessionID;

            m_log.DebugFormat("[AgentService INVENTORY BROKER]: user profile for {0} not found", userID);
            return UUID.Zero;
        }

        private bool IsLocalGridUser(UUID userID)
        {
            m_log.DebugFormat("[AgentService INVENTORY BROKER]: IsLocalGridUser {0}", userID);
            
            // TODO check with the OGP module if this user is OGPified
            if(m_OGPModule.isOGPUser(userID))
                return false;
            else
                return true;
            
        }

        private string GetUserInventoryURI(UUID userID)
        {
            string invURI = m_LocalGridInventoryURI;

            CachedUserInfo uinfo = m_UserProfileService.GetUserDetails(userID);
            if ((uinfo == null) || (uinfo.UserProfile == null))
                return invURI;

            string userInventoryServerURI = Util.ServerURI(uinfo.UserProfile.UserInventoryURI);

            if ((userInventoryServerURI != null) &&
                (userInventoryServerURI != ""))
                invURI = userInventoryServerURI;
            return invURI;
        }


    }
}

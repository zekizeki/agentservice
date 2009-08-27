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
 *     * Neither the name of the OpenSim Project nor the
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
 
using System;
using System.Collections;
using System.Collections.Generic;
using System.Web;
using System.IO;
using System.Net;
using System.Reflection;
using log4net;
using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Framework.Client;
using OpenSim.Framework.Communications;
using OpenSim.Framework.Servers;
using OpenSim.Framework.Communications.Cache;
//using OpenSim.Framework.Communications.Capabilities;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

namespace OpenSim.Region.CoreModules.InterGrid
{

    public class InventoryReflector
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        
        Scene m_scene;
        OpenGridProtocolModule m_ogp;
        
        public InventoryReflector(Scene scene,OpenGridProtocolModule ogp)
        {
            m_scene = scene;
            m_ogp = ogp;
            
            scene.EventManager.OnClientConnect += OnClientConnect;
            scene.EventManager.OnNewInventoryItemUploadComplete += UploadInventoryItem;
            scene.EventManager.OnNewInventoryItemCreated += NewInventoryItem;
            
        }
        
          // register handlers for each client when they connect
        public void OnClientConnect(IClientCore clientcore)
        {
            IClientAPI remoteClient = (IClientAPI)clientcore;
            m_log.Debug("[OGP InventoryReflector]: OnClientConnect " + remoteClient.FirstName);
            
            // check that this is an OGP user before we register event handlers
            if(m_ogp.isOGPUser(remoteClient.AgentId))
            {
                //remoteClient.OnCreateNewInventoryItem = CreateNewInventoryItem;
                remoteClient.OnUpdateInventoryItem += UpdateInventoryItem;
                remoteClient.OnRemoveInventoryItem +=RemoveInventoryItem;
                remoteClient.OnCopyInventoryItem += CopyInventoryItem;
                remoteClient.OnMoveInventoryItem += MoveInventoryItem;
                remoteClient.OnPurgeInventoryDescendents += PurgeInventoryDescendents;
                
                remoteClient.OnRemoveInventoryFolder += RemoveInventoryFolder;
                remoteClient.OnCreateNewInventoryFolder +=CreateInventoryFolder;
                remoteClient.OnUpdateInventoryFolder +=UpdateInventoryFolder;
                remoteClient.OnMoveInventoryFolder += MoveInventoryFolder;
                remoteClient.OnAvatarNowWearing += AvatarNowWearing;
            }
        }
        
        
        public void CreateInventoryFolder( IClientAPI remoteClient, UUID folderID, ushort folderType, string folderName, UUID parentID)
        {
            m_log.Debug("[OGP InventoryReflector]: CreateInventoryFolder " + folderName);
            
            InventoryFolderBase folder = new InventoryFolderBase();
                folder.Name = folderName;
                folder.ID = folderID;
                folder.Owner = remoteClient.AgentId;
                folder.ParentID = parentID;
                folder.Type = (short)folderType;
                folder.Version = 1;
                
                
            string cap = m_ogp.GetInventoryFolderCreateCap(remoteClient.AgentId);
            
                try
                {
                    OSDMap requestMap = convertInventoryFolderToOSD(folder);
                    SendRequest(requestMap, cap);
                }
                catch (WebException e)
                {
                    m_log.ErrorFormat("[OGS1 INVENTORY SERVICE]: CreateInventoryFolder operation failed, {0} {1}",
                         e.Source, e.Message);
                }
            
            
        }
        
        public void UpdateInventoryFolder(IClientAPI remoteClient, UUID folderID, ushort type, string name, UUID parentID)
        {
            m_log.Debug("[OGP InventoryReflector]: UpdateInventoryFolder " + name);
            
            InventoryFolderBase folder = new InventoryFolderBase();
                folder.Name = name;
                folder.ID = folderID;
                folder.Owner = remoteClient.AgentId;
                folder.ParentID = parentID;
                folder.Type = (short) type;
                folder.Version = 1;
                
                string cap = m_ogp.GetInventoryFolderUpdateCap(remoteClient.AgentId);
            
                try
                {
                    OSDMap requestMap = convertInventoryFolderToOSD(folder);
                    SendRequest(requestMap, cap);
                }
                catch (WebException e)
                {
                    m_log.ErrorFormat("[OGS1 INVENTORY SERVICE]: UpdateInventoryFolder operation failed, {0} {1}",
                         e.Source, e.Message);
                }
                
                
            
        }
        
        public void MoveInventoryFolder(IClientAPI remoteClient, UUID folderID, UUID parentID)
        {
            m_log.Debug("[OGP InventoryReflector]: MoveInventoryFolder " + folderID);
            InventoryFolderBase folder = new InventoryFolderBase();
                folder.Name = "";
                folder.ID = folderID;
                folder.Owner = remoteClient.AgentId;
                folder.ParentID = parentID;
                folder.Type = (short) AssetType.Folder;
                folder.Version = 1;
                
                string cap = m_ogp.GetInventoryFolderMoveCap(remoteClient.AgentId);
            
                try
                {
                    OSDMap requestMap = convertInventoryFolderToOSD(folder);
                    SendRequest(requestMap, cap);
                }
                catch (WebException e)
                {
                    m_log.ErrorFormat("[OGS1 INVENTORY SERVICE]: MoveInventoryFolder operation failed, {0} {1}",
                         e.Source, e.Message);
                }
            
        }
        
        public void RemoveInventoryFolder(IClientAPI remoteClient, List<UUID> folderIDs)
        {
            m_log.Debug("[OGP InventoryReflector]: RemoveInventoryFolder ");
            
            foreach(UUID folderID in folderIDs)
            {
	            InventoryFolderBase folder = new InventoryFolderBase();
	                folder.Name = "";
	                folder.ID = folderID;
	                folder.Owner = remoteClient.AgentId;
	                folder.ParentID = folderID;
	                folder.Type = (short) AssetType.Folder;
	                folder.Version = 1;
	                
	                string cap = m_ogp.GetInventoryFolderDeleteCap(remoteClient.AgentId);
	            
	                try
	                {
	                    OSDMap requestMap = convertInventoryFolderToOSD(folder);
	                    SendRequest(requestMap, cap);
	                }
	                catch (WebException e)
	                {
	                    m_log.ErrorFormat("[OGS1 INVENTORY SERVICE]: RemoveInventoryFolder operation failed, {0} {1}",
	                         e.Source, e.Message);
	                }
            }
        }
        
        
        /**
        * This only actually gets called if a user deletes an individual item from their trash, when an actuall delete
        * from the main inventory happens the item is simply moved to the trash folder.
        **/
        public void RemoveInventoryItem(IClientAPI remoteClient, List<UUID> itemIDs)
        {
            m_log.Debug("[OGP InventoryReflector]: RemoveInventoryItem ");
            
            //InventoryItemBase item = new InventoryItemBase();
            //item.ID = itemID;
        }
        
        public void CopyInventoryItem(IClientAPI remoteClient, uint callbackID, UUID oldAgentID, UUID oldItemID, UUID newFolderID,string newName)
        {
            m_log.Debug("[OGP InventoryReflector]: CopyInventoryItem " + oldItemID);
            
            // copy is essentially a create so we already pick this event up in OnNewInventoryItemCreated
            
        }
        
        
        public void MoveInventoryItem(IClientAPI remoteClient, List<InventoryItemBase> items)
        {
            m_log.Debug("[OGP InventoryReflector]: MoveInventoryItem ");
             
            
            CachedUserInfo userInfo
                = m_scene.CommsManager.UserProfileCacheService.GetUserDetails(remoteClient.AgentId);
            
            string cap = m_ogp.GetInventoryMoveCap(remoteClient.AgentId);
            
            foreach (InventoryItemBase item in items) // Loop through List with foreach
            {
         
                try
                {
                    OSDMap requestMap = convertInventoryItemToOSD(item);
                    SendRequest(requestMap, cap);
                }
                catch (WebException e)
                {
                    m_log.ErrorFormat("[OGS1 INVENTORY SERVICE]: MoveInventoryItem operation failed, {0} {1}",
                         e.Source, e.Message);
                }
            }
            
            
        }
        
        /**
        * PurgeInventoryDescendents is called when the trash is emptied. If an individual
        * inventory item is deleted from trash RemoveInventoryItem is called.
        */
        public void PurgeInventoryDescendents( IClientAPI remoteClient, UUID folderID)
        {
            m_log.Debug("[OGP InventoryReflector]: PurgeInventoryDescendents calling RemoveInventoryFolder for" + folderID);
            
            List<UUID> folders = new List<UUID>();
            folders.Add(folderID);
            
            RemoveInventoryFolder(remoteClient, folders);
            
        }
        
        public void UploadInventoryItem(UUID avatarID, UUID assetID, string name, int userlevel)
        {
            m_log.Debug("[OGP InventoryReflector]: UploadInventoryItem " + name);
            CachedUserInfo userInfo = m_scene.CommsManager.UserProfileCacheService.GetUserDetails(avatarID);
            if (userInfo != null)
            {
               // m_assMapper.Post(assetID, avatarID);
            }
        }
        
        public void NewInventoryItem(InventoryItemBase item)
        {
            m_log.Debug("[OGP InventoryReflector]: NewInventoryItem " + item.Name);
            CachedUserInfo userInfo = m_scene.CommsManager.UserProfileCacheService.GetUserDetails(item.CreatorIdAsUuid);
            
            
            
            // check that this is an OGP user before we reflect the event
            if(m_ogp.isOGPUser(item.Owner))
            {
                string cap = m_ogp.GetInventoryCreateCap(item.Owner);
            
	            try
	            {
	                OSDMap requestMap = convertInventoryItemToOSD(item);
	                SendRequest(requestMap, cap);
	            }
	            catch (WebException e)
	            {
	                m_log.ErrorFormat("[OGS1 INVENTORY SERVICE]: Add new inventory item operation failed, {0} {1}",
	                     e.Source, e.Message);
	            }
            }

        }
        
        
        
        void UpdateInventoryItem(
        IClientAPI remoteClient, UUID transactionID, UUID itemID, InventoryItemBase item)
        {
            m_log.Debug("[OGP InventoryReflector]: UpdateInventoryItem " + item.Name + " owner " + remoteClient.AgentId);
            
            // we need to get the item from the cache to see whats changed.
            
            //CachedUserInfo userInfo
            //    = m_scene.CommsManager.UserProfileCacheService.GetUserDetails(remoteClient.AgentId);
            //InventoryItemBase item = itemUpd;
            /*
            if (userInfo != null && userInfo.RootFolder != null)
            {
                item = userInfo.RootFolder.FindItem(itemID);
                if (item != null)
                {
                    if (UUID.Zero == transactionID)
                    {
                        item.Name = itemUpd.Name;
                        item.Description = itemUpd.Description;
                        item.NextPermissions = itemUpd.NextPermissions;
                        item.CurrentPermissions |= 8; // Slam!
                        item.EveryOnePermissions = itemUpd.EveryOnePermissions;
                        item.GroupPermissions = itemUpd.GroupPermissions;

                        item.GroupID = itemUpd.GroupID;
                        item.GroupOwned = itemUpd.GroupOwned;
                        item.CreationDate = itemUpd.CreationDate;
                        // The client sends zero if its newly created?

                        if (itemUpd.CreationDate == 0)
                            item.CreationDate = Util.UnixTimeSinceEpoch();
                        else
                            item.CreationDate = itemUpd.CreationDate;

                        // TODO: Check if folder changed and move item
                        //item.NextPermissions = itemUpd.Folder;
                        item.InvType = itemUpd.InvType;
                        item.SalePrice = itemUpd.SalePrice;
                        item.SaleType = itemUpd.SaleType;
                        item.Flags = itemUpd.Flags;

                        userInfo.UpdateItem(item);
                    }
                }
            }
            */
            // check that this is an OGP user before we reflect the event
            if(m_ogp.isOGPUser(remoteClient.AgentId))
            {
                string inventoryUpdateCap = m_ogp.GetInventoryUpdateCap(remoteClient.AgentId);
            
                try
                {
                    OSDMap requestMap = convertInventoryItemToOSD(item);
                    SendRequest(requestMap, inventoryUpdateCap);
                }
                catch (WebException e)
                {
                    m_log.ErrorFormat("[OGS1 INVENTORY SERVICE]: Add new inventory item operation failed, {0} {1}",
                         e.Source, e.Message);
                }
            }
            
            
        }
        
        
        void AvatarNowWearing(Object sender,AvatarWearingArgs wearing)
        {
            IClientAPI remoteClient = (IClientAPI)sender;
            m_log.Debug("[OGP InventoryReflector]: AvatarNowWearing owner " + remoteClient.AgentId);
            
            OSDArray wearablesOSD = new OSDArray();
            
            //CachedUserInfo userInfo
            //    = m_scene.CommsManager.UserProfileCacheService.GetUserDetails(remoteClient.AgentId);
            //InventoryItemBase item; 
            
            foreach(OpenSim.Framework.AvatarWearingArgs.Wearable wearable in wearing.NowWearing)
            {
                 m_log.Debug("[OGP InventoryReflector]: AvatarNowWearing wearable is " +wearable);
                 OSDMap osdWearable = new OSDMap();
                
                 m_log.Debug("[OGP InventoryReflector]: AvatarNowWearing itemid "+ wearable.ItemID + " type "+wearable.Type);
                 osdWearable["ItemID"] = OSD.FromUUID(wearable.ItemID);     
                 //osdWearable["AssetID"] = OSD.FromUUID(UUID.Parse("00000000-0000-0000-0000-000000000000"));
                 // we need to get the AssetID of each item too
                 /*item = userInfo.RootFolder.FindItem(wearable.ItemID);  
                 if(item !=null)  
                 {
                     osdWearable["AssetID"] = OSD.FromUUID(item.AssetID);
                 }
                 else
                 {
                     AvatarAppearance def = new AvatarAppearance();
                     osdWearable["AssetID"] = OSD.FromUUID(UUID.Parse("00000000-0000-0000-0000-000000000000"));
                     osdWearable["AssetID"] = OSD.FromUUID(def.Wearables[wearable.Type].AssetID);
                 }*/
                 
                 wearablesOSD.Add(osdWearable);
            }
            
            OSDMap RAMap = new OSDMap();
            RAMap["avatar_wearables"] = wearablesOSD;
            
            string wearablesUpdateCap = m_ogp.GetWearablesUpdateCap(remoteClient.AgentId);
            
            try
                {
                    SendRequest(RAMap, wearablesUpdateCap);
                }
                catch (WebException e)
                {
                    m_log.ErrorFormat("[OGS1 INVENTORY SERVICE]: update appearance operation failed, {0} {1}",
                         e.Source, e.Message);
                }
            
        }
        
        
        private OSDMap convertInventoryItemToOSD(InventoryItemBase invItem)
        {
            OSDMap requestMap = new OSDMap();
            
            requestMap["Creator"] = OSD.FromString(invItem.CreatorId);
            requestMap["Owner"] = OSD.FromString(invItem.Owner.ToString());
            requestMap["GroupID"] = OSD.FromString(invItem.GroupID.ToString());
            requestMap["ID"] = OSD.FromString(invItem.ID.ToString());
            requestMap["AssetID"] = OSD.FromString(invItem.AssetID.ToString());
            requestMap["AssetType"] = OSD.FromInteger(invItem.AssetType);
            requestMap["Folder"] = OSD.FromString(invItem.Folder.ToString());
            requestMap["Name"] = OSD.FromString(invItem.Name);
            requestMap["Description"] = OSD.FromString(invItem.Description);
            requestMap["NextPermissions"] = OSD.FromInteger((uint)invItem.NextPermissions);
            requestMap["CurrentPermissions"] = OSD.FromInteger((uint)invItem.CurrentPermissions);
            requestMap["BasePermissions"] = OSD.FromInteger((uint)invItem.BasePermissions);
            requestMap["EveryOnePermissions"] = OSD.FromInteger((uint)invItem.EveryOnePermissions);
            requestMap["GroupPermissions"] = OSD.FromInteger((uint)invItem.GroupPermissions);
            requestMap["InvType"] = OSD.FromInteger((int)invItem.InvType);
            requestMap["SalePrice"] = OSD.FromInteger((int)invItem.SalePrice);
            requestMap["SaleType"] = OSD.FromInteger((uint)invItem.SaleType);
            requestMap["CreationDate"] = OSD.FromInteger((int)invItem.CreationDate);
            requestMap["GroupOwned"] = OSD.FromInteger(0);
            requestMap["Flags"] = OSD.FromInteger((uint)invItem.Flags);
            
            return requestMap;
            
        }
        
        private OSDMap convertInventoryFolderToOSD(InventoryFolderBase folder)
        {
            OSDMap requestMap = new OSDMap();
            
                requestMap["Name"] = OSD.FromString(folder.Name);
                requestMap["ID"] = OSD.FromString(folder.ID.ToString()); 
                requestMap["Owner"] = OSD.FromString(folder.Owner.ToString());
                requestMap["ParentID"] = OSD.FromString(folder.ParentID.ToString()); 
                requestMap["Type"] = OSD.FromInteger((int)folder.Type);
                requestMap["Version"] = OSD.FromInteger((int)folder.Version);   
                
            return requestMap;
        }
        
        // forward events onto the users Agent Service.
        private void SendRequest(OSDMap requestMap, string capAddress)
        {
            m_log.Debug("[OGP InventoryReflector]: SendRequest " + capAddress);
             
           
            WebRequest createRequest = null; 
            try 
            { 
                createRequest = WebRequest.Create(capAddress);
                createRequest.Method = "POST"; 
                createRequest.ContentType = "application/xml+llsd";  
            } 
            catch (Exception ex) 
            { 
                m_log.InfoFormat("[OGP InventoryReflector]: Bad URL inventory create cap {0} {1}", ex.Message,capAddress); 
                
            } 
            
            byte[] buffer1 = OSDParser.SerializeLLSDXmlBytes(requestMap);
             
             // Try to post the rez avatar reqest 
            Stream os1 = null; 
            try 
            { // send the Post 
                createRequest.ContentLength = buffer1.Length; //Count bytes to send 
                os1 = createRequest.GetRequestStream(); 
                os1.Write(buffer1, 0, buffer1.Length); //Send it 
                os1.Close(); 
                m_log.InfoFormat("[OGP InventoryReflector]: Sent inventory request to cap {0}", capAddress); 
            } 
            catch (Exception ex) 
            { 
                m_log.InfoFormat("[OGP InventoryReflector]: Bad send on derez_avatar {0}", ex.Message); 
            } 
        
            // Now we get to read the response 
            m_log.Info("[OGP InventoryReflector]: waiting for a reply after new inventory request send"); 
            
             // Read the reply 
            OSD resolverResponse = null; 
            OSDMap invResponseMap = new OSDMap(); 
            string invResponse;
            
            try 
                { 
                    WebResponse webResponse = createRequest.GetResponse(); 
                    if (webResponse == null) 
                    { 
                        m_log.Info("[OGP InventoryReflector]: Null reply on Inventory create post"); 
                        return;
                    } 
                    StreamReader sr = new StreamReader(webResponse.GetResponseStream()); 
                    invResponse = sr.ReadToEnd().Trim(); 
                    m_log.InfoFormat("[OGP InventoryReflector]: InventoryReflector reply was {0} ", invResponse); 
                } 
                catch (WebException ex) 
                { 
                    m_log.InfoFormat("[OGP InventoryReflector]: exception on read after send of inventory create {0}", ex.Message); 
                
                } 
            
            
             
            
        }
        
            
        
        
        
        
        
    }

}